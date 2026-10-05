using System;
using System.Collections.Generic;
using AccountingSystem.Data.Repositories;
using AccountingSystem.Services.Security;

namespace AccountingSystem.Services.Reporting
{
    /// <summary>
    /// Business-facing reporting facade. Adds validation and authorisation on
    /// top of <see cref="ReportingRepository"/>, and normalises date ranges so a
    /// caller cannot request an inverted or open-ended period.
    /// </summary>
    public class ReportingService
    {
        private readonly ReportingRepository _repository;
        private readonly AuthorizationService _authorization;

        public ReportingService()
            : this(new ReportingRepository(), new AuthorizationService())
        {
        }

        public ReportingService(ReportingRepository repository, AuthorizationService authorization)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        }

        /// <summary>
        /// Trial balance for the period. Returns the rows plus the debit/credit
        /// control totals so the caller can display the "proof" line that must
        /// net to zero.
        /// </summary>
        public TrialBalanceReport GetTrialBalance(IEnumerable<string> roles, DateTime fromDate, DateTime toDate)
        {
            _authorization.Demand(roles, Permission.ViewLedger);
            Normalize(ref fromDate, ref toDate);

            var rows = _repository.GetTrialBalance(fromDate, toDate);
            return new TrialBalanceReport(rows, fromDate, toDate);
        }

        /// <summary>Profit &amp; Loss (Income Statement) for the period.</summary>
        public IncomeStatementResult GetIncomeStatement(IEnumerable<string> roles, DateTime fromDate, DateTime toDate)
        {
            _authorization.Demand(roles, Permission.ViewLedger);
            Normalize(ref fromDate, ref toDate);
            return _repository.GetIncomeStatement(fromDate, toDate);
        }

        /// <summary>Balance Sheet as of a date.</summary>
        public BalanceSheetResult GetBalanceSheet(IEnumerable<string> roles, DateTime asOfDate)
        {
            _authorization.Demand(roles, Permission.ViewLedger);
            return _repository.GetBalanceSheet(asOfDate.Date);
        }

        /// <summary>General Ledger detail with running balances.</summary>
        public GlDetailResult GetGeneralLedgerDetail(IEnumerable<string> roles, int? accountId, DateTime fromDate, DateTime toDate)
        {
            _authorization.Demand(roles, Permission.ViewLedger);
            Normalize(ref fromDate, ref toDate);
            return _repository.GetGeneralLedgerDetail(accountId, fromDate, toDate);
        }

        /// <summary>
        /// Defaults a missing range to the current calendar year and rejects an
        /// inverted range, keeping the queries bounded.
        /// </summary>
        private static void Normalize(ref DateTime fromDate, ref DateTime toDate)
        {
            if (fromDate == default(DateTime))
                fromDate = new DateTime(DateTime.UtcNow.Year, 1, 1);
            if (toDate == default(DateTime))
                toDate = DateTime.UtcNow.Date;
            if (toDate < fromDate)
                throw new ArgumentException("The end date must be on or after the start date.");
        }
    }

    /// <summary>Trial balance rows plus the control totals for the proof line.</summary>
    public class TrialBalanceReport
    {
        public TrialBalanceReport(IList<TrialBalanceRow> rows, DateTime fromDate, DateTime toDate)
        {
            Rows = rows;
            FromDate = fromDate;
            ToDate = toDate;
        }

        public IList<TrialBalanceRow> Rows { get; }
        public DateTime FromDate { get; }
        public DateTime ToDate { get; }

        public decimal TotalDebits { get; private set; }
        public decimal TotalCredits { get; private set; }

        /// <summary>
        /// Sums the natural-side nets into the two control columns. When the
        /// ledger is sound <see cref="IsBalanced"/> is true.
        /// </summary>
        public bool IsBalanced
        {
            get
            {
                decimal debitSide = 0, creditSide = 0;
                foreach (var row in Rows)
                {
                    if (row.AccountType == 1 || row.AccountType == 5) debitSide += row.NetBalance;
                    else creditSide += row.NetBalance;
                }
                TotalDebits = debitSide;
                TotalCredits = creditSide;
                return debitSide == creditSide;
            }
        }
    }
}
