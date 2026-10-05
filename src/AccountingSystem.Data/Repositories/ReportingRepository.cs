using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using AccountingSystem.Data.Context;

namespace AccountingSystem.Data.Repositories
{
    /// <summary>
    /// Read-only reporting queries. Each method calls the corresponding stored
    /// procedure so that the heavy aggregation runs inside SQL Server Express
    /// rather than pulling the whole ledger into memory.
    ///
    /// All methods accept a date range and are safe to call from a read-only
    /// database principal (see 07_SecurityAndPermissions.sql).
    /// </summary>
    public class ReportingRepository
    {
        private const int CommandTimeoutSeconds = 120;

        // ------------------------------------------------------------------
        // Trial Balance
        // ------------------------------------------------------------------
        /// <summary>
        /// Trial balance for [fromDate, toDate]: per-account debit/credit totals
        /// and the net on the account's natural side. The sum of all net debit
        /// balances equals the sum of all net credit balances when the ledger is
        /// sound.
        /// </summary>
        public IList<TrialBalanceRow> GetTrialBalance(DateTime fromDate, DateTime toDate)
        {
            var results = new List<TrialBalanceRow>();

            using (var connection = DbConnectionFactory.CreateOpenConnection())
            using (var command = DbConnectionFactory.CreateCommand("gl.usp_GetTrialBalance", connection, CommandTimeoutSeconds))
            {
                command.Parameters.Add("@FromDate", SqlDbType.Date).Value = fromDate.Date;
                command.Parameters.Add("@ToDate", SqlDbType.Date).Value = toDate.Date;

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        results.Add(new TrialBalanceRow
                        {
                            AccountId = reader.GetInt32(reader.GetOrdinal("AccountId")),
                            AccountCode = reader.GetString(reader.GetOrdinal("AccountCode")),
                            AccountName = reader.GetString(reader.GetOrdinal("AccountName")),
                            AccountType = reader.GetByte(reader.GetOrdinal("AccountType")),
                            AccountTypeName = reader.GetString(reader.GetOrdinal("AccountTypeName")),
                            TotalDebit = reader.GetDecimal(reader.GetOrdinal("TotalDebit")),
                            TotalCredit = reader.GetDecimal(reader.GetOrdinal("TotalCredit")),
                            NetBalance = reader.GetDecimal(reader.GetOrdinal("NetBalance"))
                        });
                    }
                }
            }

            return results;
        }

        // ------------------------------------------------------------------
        // Income Statement
        // ------------------------------------------------------------------
        /// <summary>
        /// Income Statement for [fromDate, toDate]. Returns the detail lines and
        /// the summary totals (revenue, expense, net income) in one call.
        /// </summary>
        public IncomeStatementResult GetIncomeStatement(DateTime fromDate, DateTime toDate)
        {
            var result = new IncomeStatementResult();

            using (var connection = DbConnectionFactory.CreateOpenConnection())
            using (var command = DbConnectionFactory.CreateCommand("gl.usp_GetIncomeStatement", connection, CommandTimeoutSeconds))
            {
                command.Parameters.Add("@FromDate", SqlDbType.Date).Value = fromDate.Date;
                command.Parameters.Add("@ToDate", SqlDbType.Date).Value = toDate.Date;

                using (var reader = command.ExecuteReader())
                {
                    // Result set 1: detail lines.
                    while (reader.Read())
                    {
                        result.Lines.Add(new IncomeStatementRow
                        {
                            AccountId = reader.GetInt32(reader.GetOrdinal("AccountId")),
                            AccountCode = reader.GetString(reader.GetOrdinal("AccountCode")),
                            AccountName = reader.GetString(reader.GetOrdinal("AccountName")),
                            SectionName = reader.GetString(reader.GetOrdinal("SectionName")),
                            Amount = reader.GetDecimal(reader.GetOrdinal("Amount"))
                        });
                    }

                    // Result set 2: summary.
                    if (reader.NextResult() && reader.Read())
                    {
                        result.Summary = new IncomeStatementSummary
                        {
                            TotalRevenue = reader.GetDecimal(reader.GetOrdinal("TotalRevenue")),
                            TotalExpense = reader.GetDecimal(reader.GetOrdinal("TotalExpense")),
                            NetIncome = reader.GetDecimal(reader.GetOrdinal("NetIncome"))
                        };
                    }
                }
            }

            return result;
        }

        // ------------------------------------------------------------------
        // Balance Sheet
        // ------------------------------------------------------------------
        /// <summary>
        /// Balance Sheet as of <paramref name="asOfDate"/>. Returns asset,
        /// liability and equity lines plus the current-period earnings needed to
        /// make Assets = Liabilities + Equity hold before the year is closed.
        /// </summary>
        public BalanceSheetResult GetBalanceSheet(DateTime asOfDate)
        {
            var result = new BalanceSheetResult();

            using (var connection = DbConnectionFactory.CreateOpenConnection())
            using (var command = DbConnectionFactory.CreateCommand("gl.usp_GetBalanceSheet", connection, CommandTimeoutSeconds))
            {
                command.Parameters.Add("@AsOfDate", SqlDbType.Date).Value = asOfDate.Date;

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Lines.Add(new BalanceSheetRow
                        {
                            AccountId = reader.GetInt32(reader.GetOrdinal("AccountId")),
                            AccountCode = reader.GetString(reader.GetOrdinal("AccountCode")),
                            AccountName = reader.GetString(reader.GetOrdinal("AccountName")),
                            SectionName = reader.GetString(reader.GetOrdinal("SectionName")),
                            Amount = reader.GetDecimal(reader.GetOrdinal("Amount"))
                        });
                    }

                    if (reader.NextResult() && reader.Read())
                    {
                        result.Earnings = new BalanceSheetEarnings
                        {
                            TotalRevenue = reader.GetDecimal(reader.GetOrdinal("TotalRevenue")),
                            TotalExpense = reader.GetDecimal(reader.GetOrdinal("TotalExpense")),
                            CurrentEarnings = reader.GetDecimal(reader.GetOrdinal("CurrentEarnings"))
                        };
                    }
                }
            }

            return result;
        }

        // ------------------------------------------------------------------
        // General Ledger Detail
        // ------------------------------------------------------------------
        /// <summary>
        /// General Ledger detail with a running balance for one account (or all
        /// accounts when <paramref name="accountId"/> is null) across the range.
        /// The running balance is computed here, in document order, starting
        /// from the opening balance returned by the procedure.
        /// </summary>
        public GlDetailResult GetGeneralLedgerDetail(int? accountId, DateTime fromDate, DateTime toDate)
        {
            var result = new GlDetailResult();

            using (var connection = DbConnectionFactory.CreateOpenConnection())
            using (var command = DbConnectionFactory.CreateCommand("gl.usp_GetGeneralLedgerDetail", connection, CommandTimeoutSeconds))
            {
                command.Parameters.Add("@AccountId", SqlDbType.Int).Value = (object)accountId ?? DBNull.Value;
                command.Parameters.Add("@FromDate", SqlDbType.Date).Value = fromDate.Date;
                command.Parameters.Add("@ToDate", SqlDbType.Date).Value = toDate.Date;

                using (var reader = command.ExecuteReader())
                {
                    // Result set 1: opening balances per account.
                    var openings = new Dictionary<int, decimal>();
                    while (reader.Read())
                    {
                        var id = reader.GetInt32(reader.GetOrdinal("AccountId"));
                        openings[id] = reader.GetDecimal(reader.GetOrdinal("OpeningBalance"));
                        result.OpeningBalances.Add(new GlOpeningBalance
                        {
                            AccountId = id,
                            AccountCode = reader.GetString(reader.GetOrdinal("AccountCode")),
                            AccountName = reader.GetString(reader.GetOrdinal("AccountName")),
                            NormalBalance = reader.GetByte(reader.GetOrdinal("NormalBalance")),
                            OpeningBalance = openings[id]
                        });
                    }

                    // Result set 2: movements, already ordered by account, date, journal.
                    if (reader.NextResult())
                    {
                        while (reader.Read())
                        {
                            var row = new GlDetailRow
                            {
                                AccountId = reader.GetInt32(reader.GetOrdinal("AccountId")),
                                AccountCode = reader.GetString(reader.GetOrdinal("AccountCode")),
                                AccountName = reader.GetString(reader.GetOrdinal("AccountName")),
                                NormalBalance = reader.GetByte(reader.GetOrdinal("NormalBalance")),
                                JournalId = reader.GetInt64(reader.GetOrdinal("JournalId")),
                                VoucherNumber = reader.GetString(reader.GetOrdinal("VoucherNumber")),
                                TransactionDate = reader.GetDateTime(reader.GetOrdinal("TransactionDate")),
                                Reference = reader.IsDBNull(reader.GetOrdinal("Reference"))
                                            ? null : reader.GetString(reader.GetOrdinal("Reference")),
                                JournalDetailId = reader.GetInt64(reader.GetOrdinal("JournalDetailId")),
                                LineNumber = reader.GetInt32(reader.GetOrdinal("LineNumber")),
                                Description = reader.IsDBNull(reader.GetOrdinal("Description"))
                                            ? null : reader.GetString(reader.GetOrdinal("Description")),
                                Debit = reader.GetDecimal(reader.GetOrdinal("Debit")),
                                Credit = reader.GetDecimal(reader.GetOrdinal("Credit"))
                            };

                            // Carry the running balance per account, signed to the
                            // natural side so a debit-natural account increases on
                            // debit and a credit-natural account increases on credit.
                            decimal running;
                            openings.TryGetValue(row.AccountId, out running);
                            running += row.NormalBalance == 1
                                ? row.Debit - row.Credit
                                : row.Credit - row.Debit;
                            openings[row.AccountId] = running;
                            row.RunningBalance = running;

                            result.Movements.Add(row);
                        }
                    }
                }
            }

            return result;
        }
    }

    /// <summary>Combined result of the Income Statement query.</summary>
    public class IncomeStatementResult
    {
        public IList<IncomeStatementRow> Lines { get; } = new List<IncomeStatementRow>();
        public IncomeStatementSummary Summary { get; set; } = new IncomeStatementSummary();
    }

    /// <summary>Combined result of the Balance Sheet query.</summary>
    public class BalanceSheetResult
    {
        public IList<BalanceSheetRow> Lines { get; } = new List<BalanceSheetRow>();
        public BalanceSheetEarnings Earnings { get; set; } = new BalanceSheetEarnings();

        public decimal TotalAssets => Lines.Where(l => l.SectionName == "Asset").Sum(l => l.Amount);
        public decimal TotalLiabilities => Lines.Where(l => l.SectionName == "Liability").Sum(l => l.Amount);
        public decimal TotalEquity => Lines.Where(l => l.SectionName == "Equity").Sum(l => l.Amount)
                                     + Earnings.CurrentEarnings;
    }

    /// <summary>Combined result of the General Ledger Detail query.</summary>
    public class GlDetailResult
    {
        public IList<GlOpeningBalance> OpeningBalances { get; } = new List<GlOpeningBalance>();
        public IList<GlDetailRow> Movements { get; } = new List<GlDetailRow>();
    }
}
