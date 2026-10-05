using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.SqlClient;
using System.Linq;
using System.Transactions;
using AccountingSystem.Data;
using AccountingSystem.Domain.Entities;
using AccountingSystem.Domain.Enums;
using AccountingSystem.Domain.Interfaces;
using AccountingSystem.Services.Security;

namespace AccountingSystem.Services.Accounting
{
    /// <summary>
    /// The double-entry posting engine.
    ///
    /// Responsibilities:
    ///   * Create drafts with the balance invariant validated up front.
    ///   * Approve drafts (segregation of duties).
    ///   * Post entries atomically: the balance check and the Draft/Approved ->
    ///     Posted status change execute inside a single serialisable database
    ///     transaction, so two concurrent postings can never observe a torn
    ///     state or double-post the same voucher.
    ///   * Void posted entries by generating a reversing entry (posted entries
    ///     themselves are immutable).
    ///
    /// The service is the authoritative gate for the "Total Debits == Total
    /// Credits" rule in C#; the stored procedure <c>gl.usp_PostJournalEntry</c>
    /// and the table triggers enforce the same rule at the database boundary, so
    /// a bug or a direct SQL client still cannot break the ledger.
    /// </summary>
    public class JournalService : IJournalService
    {
        private readonly DocumentNumberService _numberService;
        private readonly AuditService _auditService;

        public JournalService()
            : this(new DocumentNumberService(), new AuditService())
        {
        }

        public JournalService(DocumentNumberService numberService, AuditService auditService)
        {
            _numberService = numberService ?? throw new ArgumentNullException(nameof(numberService));
            _auditService = auditService ?? throw new ArgumentNullException(nameof(auditService));
        }

        // ==================================================================
        // Create
        // ==================================================================
        /// <summary>
        /// Persists a new journal entry as a Draft. The entry must satisfy the
        /// structural rules (at least two lines, one non-zero side per line,
        /// positive amounts) but is allowed to be temporarily unbalanced while
        /// the user is still editing it.
        /// </summary>
        public long CreateDraft(JournalHeader header, IEnumerable<JournalDetail> lines, string userName)
        {
            if (header == null) throw new ArgumentNullException(nameof(header));
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            if (string.IsNullOrWhiteSpace(userName))
                throw new ArgumentException("A user name is required to create a journal entry.", nameof(userName));

            var lineList = lines.Where(l => l != null).ToList();
            ValidateLineStructure(lineList);

            header.TransactionDate = header.TransactionDate.Date;
            header.Status = JournalStatus.Draft;
            header.CreatedBy = userName;
            header.CreatedAt = DateTime.UtcNow;
            header.IsDeleted = false;

            if (string.IsNullOrWhiteSpace(header.VoucherNumber))
                header.VoucherNumber = _numberService.GetNextNumber("JV", header.TransactionDate);

            // Compute denormalised totals so the grid and reports have them
            // without a join, and so the header carries its own balance check.
            header.TotalDebit = lineList.Sum(l => l.Debit);
            header.TotalCredit = lineList.Sum(l => l.Credit);

            var lineNumber = 1;
            foreach (var line in lineList)
                line.LineNumber = lineNumber++;

            header.Lines = lineList;

            using (var context = new AccountingDbContext())
            using (var transaction = context.Database.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                try
                {
                    context.JournalHeaders.Add(header);
                    context.SaveChanges();

                    _auditService.Write(context, "gl", "JournalHeader", header.JournalId.ToString(),
                        AuditAction.Insert, null, header, userName, null);

                    context.SaveChanges();
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }

            return header.JournalId;
        }

        // ==================================================================
        // Approve
        // ==================================================================
        /// <summary>
        /// Moves a Draft entry to Approved. Approval is a segregation-of-duties
        /// checkpoint: the approver is recorded separately from the creator, and
        /// the entry must be balanced before it can be approved.
        /// </summary>
        public void Approve(long journalId, string userName)
        {
            if (string.IsNullOrWhiteSpace(userName))
                throw new ArgumentException("A user name is required to approve a journal entry.", nameof(userName));

            using (var context = new AccountingDbContext())
            using (var transaction = context.Database.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                try
                {
                    var header = LoadHeaderWithLines(context, journalId, forUpdate: true);

                    if (header.Status != JournalStatus.Draft)
                        throw new InvalidOperationException(
                            $"Only a Draft entry can be approved. Entry {header.VoucherNumber} is {header.Status}.");

                    if (!header.IsBalanced)
                        throw new InvalidOperationException(
                            $"Entry {header.VoucherNumber} is not balanced: debits {header.TotalDebit:N2} " +
                            $"vs credits {header.TotalCredit:N2}. Correct the lines before approving.");

                    var oldValues = Snapshot(header);

                    header.Status = JournalStatus.Approved;
                    header.ApprovedBy = userName;
                    header.ApprovedAt = DateTime.UtcNow;

                    _auditService.Write(context, "gl", "JournalHeader", header.JournalId.ToString(),
                        AuditAction.Update, oldValues, Snapshot(header), userName, null);

                    context.SaveChanges();
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        // ==================================================================
        // Post  (the core atomic operation)
        // ==================================================================
        /// <summary>
        /// Posts an entry. Steps:
        ///   1. Open a SERIALIZABLE transaction so the header row is locked for
        ///      the duration; concurrent postings of the same voucher block.
        ///   2. Re-validate every rule in C# (defence in depth).
        ///   3. Call gl.usp_PostJournalEntry, which re-checks the rules and
        ///      performs the status transition.
        ///   4. Write the audit row.
        ///   5. Commit. Any exception rolls the whole thing back.
        /// </summary>
        public void PostJournalEntry(long journalId, string userName)
        {
            if (string.IsNullOrWhiteSpace(userName))
                throw new ArgumentException("A user name is required to post a journal entry.", nameof(userName));

            using (var context = new AccountingDbContext())
            using (var transaction = context.Database.BeginTransaction(IsolationLevel.Serializable))
            {
                try
                {
                    // ---- 1/2. Load and validate under the lock. --------------
                    var header = LoadHeaderWithLines(context, journalId, forUpdate: true);

                    if (header.Status == JournalStatus.Posted || header.Status == JournalStatus.Void)
                        throw new InvalidOperationException(
                            $"Entry {header.VoucherNumber} has already been posted or voided.");

                    ValidateLineStructure(header.Lines.ToList());
                    ValidateBalanced(header);

                    if (header.FiscalPeriodId.HasValue)
                    {
                        var periodClosed = context.FiscalPeriods
                            .Where(p => p.FiscalPeriodId == header.FiscalPeriodId.Value)
                            .Select(p => p.IsClosed)
                            .FirstOrDefault();
                        if (periodClosed)
                            throw new InvalidOperationException(
                                $"The fiscal period for entry {header.VoucherNumber} is closed.");
                    }

                    var oldValues = Snapshot(header);

                    // ---- 3. Delegate the atomic transition to the procedure. --
                    var journalIdParam = new SqlParameter("@JournalId", journalId);
                    var postedByParam = new SqlParameter("@PostedBy", userName);
                    var postingDateParam = new SqlParameter("@PostingDate",
                        (object)DateTime.UtcNow.Date ?? DBNull.Value);

                    // Enlist the ADO.NET command in the ambient EF transaction so
                    // the status update and the audit write commit together.
                    var dbTransaction = transaction.UnderlyingTransaction as SqlTransaction;
                    if (dbTransaction != null)
                    {
                        using (var command = DbConnectionFactory.CreateCommand(
                                   "gl.usp_PostJournalEntry",
                                   (SqlConnection)context.Database.Connection,
                                   60))
                        {
                            command.Transaction = dbTransaction;
                            command.Parameters.Add(journalIdParam);
                            command.Parameters.Add(postedByParam);
                            command.Parameters.Add(postingDateParam);
                            command.ExecuteNonQuery();
                        }
                    }
                    else
                    {
                        // Fallback for providers without a SqlTransaction (tests):
                        // run the procedure on its own connection.
                        using (var connection = DbConnectionFactory.CreateOpenConnection())
                        using (var command = DbConnectionFactory.CreateCommand(
                                   "gl.usp_PostJournalEntry", connection, 60))
                        {
                            command.Parameters.Add(journalIdParam);
                            command.Parameters.Add(postedByParam);
                            command.Parameters.Add(postingDateParam);
                            command.ExecuteNonQuery();
                        }
                    }

                    // Refresh the in-memory entity so the audit snapshot is current.
                    context.Entry(header).Reload();

                    // ---- 4. Audit. -------------------------------------------
                    _auditService.Write(context, "gl", "JournalHeader", header.JournalId.ToString(),
                        AuditAction.Update, oldValues, Snapshot(header), userName, null);

                    context.SaveChanges();

                    // ---- 5. Commit. ------------------------------------------
                    transaction.Commit();
                }
                catch (DbUpdateException ex)
                {
                    transaction.Rollback();
                    throw new InvalidOperationException(
                        "The journal entry could not be posted because it would violate a " +
                        "database integrity rule: " + RootMessage(ex), ex);
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        // ==================================================================
        // Void
        // ==================================================================
        /// <summary>
        /// Voids a posted entry and generates its reversing entry. Both writes
        /// happen in one transaction; the original is retained for the audit trail.
        /// </summary>
        public long VoidJournalEntry(long journalId, string userName, string reason)
        {
            if (string.IsNullOrWhiteSpace(userName))
                throw new ArgumentException("A user name is required to void a journal entry.", nameof(userName));
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("A reason is required to void a journal entry.", nameof(reason));

            using (var context = new AccountingDbContext())
            using (var transaction = context.Database.BeginTransaction(IsolationLevel.Serializable))
            {
                try
                {
                    var header = LoadHeaderWithLines(context, journalId, forUpdate: true);
                    if (header.Status != JournalStatus.Posted)
                        throw new InvalidOperationException(
                            $"Only a posted entry can be voided. Entry {header.VoucherNumber} is {header.Status}.");

                    var oldValues = Snapshot(header);

                    long reversalId;
                    var dbTransaction = transaction.UnderlyingTransaction as SqlTransaction;
                    using (var command = DbConnectionFactory.CreateCommand(
                               "gl.usp_VoidJournalEntry",
                               (SqlConnection)context.Database.Connection, 60))
                    {
                        if (dbTransaction != null) command.Transaction = dbTransaction;
                        command.Parameters.Add(new SqlParameter("@JournalId", journalId));
                        command.Parameters.Add(new SqlParameter("@VoidedBy", userName));
                        command.Parameters.Add(new SqlParameter("@Reason", reason));
                        var outParam = command.Parameters.Add("@ReversalJournalId", System.Data.SqlDbType.BigInt);
                        outParam.Direction = System.Data.ParameterDirection.Output;

                        command.ExecuteNonQuery();
                        reversalId = outParam.Value == DBNull.Value ? 0L : (long)outParam.Value;
                    }

                    context.Entry(header).Reload();

                    _auditService.Write(context, "gl", "JournalHeader", header.JournalId.ToString(),
                        AuditAction.Update, oldValues, Snapshot(header), userName, null);
                    _auditService.Write(context, "gl", "JournalHeader", reversalId.ToString(),
                        AuditAction.Insert, null,
                        new { ReversalOf = header.VoucherNumber, Reason = reason }, userName, null);

                    context.SaveChanges();
                    transaction.Commit();
                    return reversalId;
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        // ==================================================================
        // Read
        // ==================================================================
        public JournalHeader GetById(long journalId)
        {
            using (var context = new AccountingDbContext())
            {
                return context.JournalHeaders
                    .Include(h => h.Lines.Select(l => l.Account))
                    .AsNoTracking()
                    .FirstOrDefault(h => h.JournalId == journalId && !h.IsDeleted);
            }
        }

        /// <summary>Returns the current status of an entry without loading lines.</summary>
        public JournalStatus GetStatus(long journalId)
        {
            using (var context = new AccountingDbContext())
            {
                return context.JournalHeaders
                    .Where(h => h.JournalId == journalId)
                    .Select(h => h.Status)
                    .FirstOrDefault();
            }
        }

        // ==================================================================
        // Private helpers
        // ==================================================================

        /// <summary>
        /// Loads a header and its lines. When <paramref name="forUpdate"/> is
        /// true the read is issued with UPDLOCK so the row is held for the
        /// surrounding transaction, preventing a concurrent post/void race.
        /// </summary>
        private static JournalHeader LoadHeaderWithLines(AccountingDbContext context, long journalId, bool forUpdate)
        {
            JournalHeader header;
            if (forUpdate)
            {
                // A raw locking hint cannot be expressed in LINQ; the surrounding
                // SERIALIZABLE transaction already takes the necessary range locks,
                // and this explicit UPDLOCK makes the intent unambiguous.
                header = context.JournalHeaders
                    .SqlQuery("SELECT * FROM gl.JournalHeader WITH (UPDLOCK) WHERE JournalId = @p0", journalId)
                    .Include(h => h.Lines.Select(l => l.Account))
                    .FirstOrDefault();
            }
            else
            {
                header = context.JournalHeaders
                    .Include(h => h.Lines.Select(l => l.Account))
                    .FirstOrDefault(h => h.JournalId == journalId);
            }

            if (header == null)
                throw new InvalidOperationException($"Journal entry {journalId} was not found.");

            return header;
        }

        /// <summary>Structural rules that every entry must satisfy regardless of state.</summary>
        private static void ValidateLineStructure(IList<JournalDetail> lines)
        {
            if (lines == null || lines.Count < 2)
                throw new InvalidOperationException(
                    "A journal entry requires at least two lines (one debit and one credit).");

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.AccountId <= 0)
                    throw new InvalidOperationException($"Line {i + 1} does not reference an account.");
                if (line.Debit < 0 || line.Credit < 0)
                    throw new InvalidOperationException($"Line {i + 1} has a negative amount.");
                if (line.Debit > 0 && line.Credit > 0)
                    throw new InvalidOperationException(
                        $"Line {i + 1} has both a debit and a credit. Use one side per line.");
                if (line.Debit == 0 && line.Credit == 0)
                    throw new InvalidOperationException($"Line {i + 1} has no amount.");
            }
        }

        /// <summary>
        /// The fundamental double-entry rule. Compares to the cent (4 dp) so
        /// floating noise cannot mask a real imbalance.
        /// </summary>
        private static void ValidateBalanced(JournalHeader header)
        {
            var debits = header.Lines.Sum(l => l.Debit);
            var credits = header.Lines.Sum(l => l.Credit);

            if (debits == 0 && credits == 0)
                throw new InvalidOperationException("The journal entry has no monetary value.");

            if (debits != credits)
                throw new InvalidOperationException(
                    $"The journal entry is not balanced. Total debits {debits:N2} must equal " +
                    $"total credits {credits:N2} (difference {Math.Abs(debits - credits):N2}).");
        }

        /// <summary>Small anonymous snapshot used for the audit before/after image.</summary>
        private static object Snapshot(JournalHeader header) => new
        {
            header.JournalId,
            header.VoucherNumber,
            header.TransactionDate,
            Status = header.Status.ToString(),
            header.TotalDebit,
            header.TotalCredit,
            header.PostedBy,
            header.PostedAt,
            header.VoidedBy,
            header.VoidedAt
        };

        private static string RootMessage(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }
    }
}
