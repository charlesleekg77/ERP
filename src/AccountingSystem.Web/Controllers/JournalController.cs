using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Mvc;
using AccountingSystem.Domain.Entities;
using AccountingSystem.Domain.Enums;
using AccountingSystem.Services.Accounting;
using AccountingSystem.Services.Security;
using AccountingSystem.Web.Infrastructure;
using AccountingSystem.Web.Models;

namespace AccountingSystem.Web.Controllers
{
    /// <summary>
    /// Journal entry screens and AJAX endpoints.
    ///
    /// Server-side balancing is authoritative: even though the browser grid
    /// shows a live debit/credit total, <see cref="Create"/> re-validates on the
    /// server and <see cref="Post"/> delegates to the transactional
    /// <see cref="JournalService"/>. A tampered or scripted POST cannot post an
    /// unbalanced entry.
    /// </summary>
    public class JournalController : Controller
    {
        private readonly JournalService _journalService;
        private readonly AccountService _accountService;
        private readonly AuthorizationService _authorization;

        public JournalController()
        {
            _journalService = new JournalService();
            _accountService = new AccountService();
            _authorization = new AuthorizationService();
        }

        // ------------------------------------------------------------------
        // List
        // ------------------------------------------------------------------
        [RequirePermission(Permission.ViewLedger)]
        public ActionResult Index()
        {
            return View();
        }

        /// <summary>
        /// Server-side data source for the DataTables grid. Returns a paged,
        /// filtered projection - never the whole table.
        /// </summary>
        [RequirePermission(Permission.ViewLedger)]
        public JsonResult List(int draw, int start = 0, int length = 25, string status = null, string search = null)
        {
            // Bound the page size so a client cannot request the entire ledger.
            length = Math.Min(Math.Max(length, 1), 200);
            start = Math.Max(start, 0);

            using (var context = new AccountingSystem.Data.AccountingDbContext())
            {
                var query = context.JournalHeaders
                    .AsNoTracking()
                    .Where(h => !h.IsDeleted);

                if (!string.IsNullOrWhiteSpace(status) && byte.TryParse(status, out var statusValue))
                    query = query.Where(h => h.Status == (JournalStatus)statusValue);

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var term = search.Trim();
                    query = query.Where(h => h.VoucherNumber.Contains(term)
                                          || h.Reference.Contains(term)
                                          || h.Description.Contains(term));
                }

                var total = query.Count();
                var rows = query
                    .OrderByDescending(h => h.TransactionDate)
                    .ThenByDescending(h => h.JournalId)
                    .Skip(start)
                    .Take(length)
                    .Select(h => new
                    {
                        h.JournalId,
                        h.VoucherNumber,
                        TransactionDate = h.TransactionDate,
                        h.Reference,
                        Status = h.Status,
                        h.TotalDebit,
                        h.TotalCredit,
                        h.PostedBy
                    })
                    .ToList()
                    // Status is an enum; render its name for readability.
                    .Select(h => new
                    {
                        h.JournalId,
                        h.VoucherNumber,
                        TransactionDate = h.TransactionDate.ToString("yyyy-MM-dd"),
                        h.Reference,
                        Status = h.Status.ToString(),
                        h.TotalDebit,
                        h.TotalCredit,
                        h.PostedBy
                    })
                    .ToList();

                return Json(new { draw, recordsTotal = total, recordsFiltered = total, data = rows },
                            JsonRequestBehavior.AllowGet);
            }
        }

        // ------------------------------------------------------------------
        // Create (GET)
        // ------------------------------------------------------------------
        [RequirePermission(Permission.CreateDraftJournal)]
        public ActionResult Create()
        {
            var model = new JournalEntryViewModel
            {
                Accounts = _accountService.GetPostableAccounts(),
                Lines = new List<JournalLineViewModel>
                {
                    new JournalLineViewModel(),
                    new JournalLineViewModel()
                }
            };
            return View(model);
        }

        // ------------------------------------------------------------------
        // Create (POST)
        // ------------------------------------------------------------------
        /// <summary>
        /// Validates and persists a new draft journal entry. Re-checks the
        /// double-entry rules on the server before saving.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequirePermission(Permission.CreateDraftJournal)]
        public ActionResult Create(JournalEntryViewModel model)
        {
            // Drop completely empty rows the grid may have left behind.
            model.Lines = model.Lines?
                .Where(l => l.AccountId > 0 || l.Debit != 0 || l.Credit != 0)
                .ToList() ?? new List<JournalLineViewModel>();

            if (!ModelState.IsValid)
                return RejectWithModel(model, "Please correct the highlighted fields.");

            if (model.Lines.Count < 2)
                return RejectWithModel(model, "A journal entry requires at least two lines.");

            // --- Server-side double-entry validation (authoritative) ---------
            foreach (var line in model.Lines)
            {
                if (line.Debit < 0 || line.Credit < 0)
                    return RejectWithModel(model, "Amounts cannot be negative.");
                if (line.Debit > 0 && line.Credit > 0)
                    return RejectWithModel(model, "A line cannot have both a debit and a credit.");
                if (line.Debit == 0 && line.Credit == 0)
                    return RejectWithModel(model, "Every line must have either a debit or a credit.");
            }

            var totalDebit = model.Lines.Sum(l => l.Debit);
            var totalCredit = model.Lines.Sum(l => l.Credit);
            if (totalDebit != totalCredit)
            {
                return RejectWithModel(model,
                    $"The entry is not balanced. Debits {totalDebit:N2} vs credits {totalCredit:N2} " +
                    $"(difference {Math.Abs(totalDebit - totalCredit):N2}).");
            }

            // --- Verify the referenced accounts are usable -------------------
            var invalid = _accountService.FindInvalidAccounts(model.Lines.Select(l => l.AccountId));
            if (invalid.Count > 0)
                return RejectWithModel(model, "One or more lines reference an invalid or non-postable account.");

            // --- Persist ------------------------------------------------------
            var header = new JournalHeader
            {
                VoucherNumber = model.VoucherNumber,
                TransactionDate = model.TransactionDate,
                Reference = model.Reference,
                Description = model.Description,
                SourceModule = SourceModule.GL.ToString()
            };

            var lines = model.Lines.Select(l => new JournalDetail
            {
                AccountId = l.AccountId,
                Debit = l.Debit,
                Credit = l.Credit,
                Description = l.Description,
                CostCenter = l.CostCenter
            });

            try
            {
                var id = _journalService.CreateDraft(header, lines, User.Identity.Name);
                TempData["Success"] = $"Draft journal {header.VoucherNumber} saved.";
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (Exception ex)
            {
                return RejectWithModel(model, ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Details
        // ------------------------------------------------------------------
        [RequirePermission(Permission.ViewLedger)]
        public ActionResult Details(long id)
        {
            var header = _journalService.GetById(id);
            if (header == null) return HttpNotFound();
            return View(header);
        }

        // ------------------------------------------------------------------
        // Approve / Post / Void (AJAX)
        // ------------------------------------------------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequirePermission(Permission.ApproveJournal)]
        public JsonResult Approve(long id)
        {
            return Execute(() => _journalService.Approve(id, User.Identity.Name),
                           "Journal entry approved.");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequirePermission(Permission.PostJournal)]
        public JsonResult Post(long id)
        {
            return Execute(() => _journalService.PostJournalEntry(id, User.Identity.Name),
                           "Journal entry posted.");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequirePermission(Permission.VoidJournal)]
        public JsonResult Void(long id, string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return Json(new { success = false, message = "A reason is required to void an entry." });

            long reversalId = 0;
            return Execute(() => reversalId = _journalService.VoidJournalEntry(id, User.Identity.Name, reason),
                           "Journal entry voided and reversed.");
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------
        /// <summary>Runs an action, returning a JSON success/error envelope.</summary>
        private JsonResult Execute(Action action, string successMessage)
        {
            try
            {
                action();
                return Json(new { success = true, message = successMessage });
            }
            catch (UnauthorizedAccessException ex)
            {
                Response.StatusCode = 403;
                return Json(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                // Surface the business message; the detail is logged by HandleError.
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>Re-renders the create form with a model-level error.</summary>
        private ActionResult RejectWithModel(JournalEntryViewModel model, string message)
        {
            ModelState.AddModelError(string.Empty, message);
            model.Accounts = _accountService.GetPostableAccounts();
            return View(model);
        }
    }
}
