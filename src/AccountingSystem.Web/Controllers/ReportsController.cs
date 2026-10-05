using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Mvc;
using AccountingSystem.Services.Accounting;
using AccountingSystem.Services.Reporting;
using AccountingSystem.Services.Security;
using AccountingSystem.Web.Infrastructure;
using AccountingSystem.Web.Models;

namespace AccountingSystem.Web.Controllers
{
    /// <summary>
    /// Financial reporting screens. All actions require the ViewLedger
    /// permission and accept a date range that is validated and bounded by
    /// <see cref="ReportingService"/>.
    /// </summary>
    public class ReportsController : Controller
    {
        private readonly ReportingService _reportingService;
        private readonly AccountService _accountService;

        public ReportsController()
        {
            _reportingService = new ReportingService();
            _accountService = new AccountService();
        }

        /// <summary>Convenience index that links to each report.</summary>
        [RequirePermission(Permission.ViewLedger)]
        public ActionResult Index() => View();

        // ------------------------------------------------------------------
        // Trial Balance
        // ------------------------------------------------------------------
        [RequirePermission(Permission.ViewLedger)]
        public ActionResult TrialBalance(DateTime? fromDate, DateTime? toDate)
        {
            var from = fromDate ?? new DateTime(DateTime.UtcNow.Year, 1, 1);
            var to = toDate ?? DateTime.UtcNow.Date;

            var report = _reportingService.GetTrialBalance(CurrentRoles(), from, to);
            var balanced = report.IsBalanced;   // computing this fills the totals

            return View(new TrialBalanceViewModel
            {
                FromDate = report.FromDate,
                ToDate = report.ToDate,
                Rows = report.Rows,
                TotalDebits = report.TotalDebits,
                TotalCredits = report.TotalCredits,
                IsBalanced = balanced
            });
        }

        // ------------------------------------------------------------------
        // Income Statement
        // ------------------------------------------------------------------
        [RequirePermission(Permission.ViewLedger)]
        public ActionResult IncomeStatement(DateTime? fromDate, DateTime? toDate)
        {
            var from = fromDate ?? new DateTime(DateTime.UtcNow.Year, 1, 1);
            var to = toDate ?? DateTime.UtcNow.Date;

            var result = _reportingService.GetIncomeStatement(CurrentRoles(), from, to);

            return View(new IncomeStatementViewModel
            {
                FromDate = from,
                ToDate = to,
                Lines = result.Lines,
                Summary = result.Summary
            });
        }

        // ------------------------------------------------------------------
        // Balance Sheet
        // ------------------------------------------------------------------
        [RequirePermission(Permission.ViewLedger)]
        public ActionResult BalanceSheet(DateTime? asOfDate)
        {
            var asOf = asOfDate ?? DateTime.UtcNow.Date;
            var result = _reportingService.GetBalanceSheet(CurrentRoles(), asOf);

            var assets = result.TotalAssets;
            var liabilities = result.TotalLiabilities;
            var equity = result.TotalEquity;

            return View(new BalanceSheetViewModel
            {
                AsOfDate = asOf,
                Lines = result.Lines,
                TotalAssets = assets,
                TotalLiabilities = liabilities,
                TotalEquity = equity,
                CurrentEarnings = result.Earnings.CurrentEarnings
            });
        }

        // ------------------------------------------------------------------
        // General Ledger detail
        // ------------------------------------------------------------------
        [RequirePermission(Permission.ViewLedger)]
        public ActionResult GeneralLedger(int? accountId, DateTime? fromDate, DateTime? toDate)
        {
            var from = fromDate ?? new DateTime(DateTime.UtcNow.Year, 1, 1);
            var to = toDate ?? DateTime.UtcNow.Date;

            var result = _reportingService.GetGeneralLedgerDetail(CurrentRoles(), accountId, from, to);
            var accounts = _accountService.GetPostableAccounts();
            var selected = accountId.HasValue
                ? accounts.FirstOrDefault(a => a.AccountId == accountId.Value)
                : null;

            return View(new GeneralLedgerViewModel
            {
                FromDate = from,
                ToDate = to,
                AccountId = accountId,
                AccountLabel = selected != null ? $"{selected.AccountCode} - {selected.AccountName}" : "All accounts",
                OpeningBalances = result.OpeningBalances,
                Movements = result.Movements
            });
        }

        /// <summary>
        /// Returns the current user's application roles via the shared
        /// <see cref="RoleResolver"/>, so report authorisation agrees with the
        /// permission filter applied to these actions.
        /// </summary>
        private IEnumerable<string> CurrentRoles() => RoleResolver.GetRoles(User);
    }
}
