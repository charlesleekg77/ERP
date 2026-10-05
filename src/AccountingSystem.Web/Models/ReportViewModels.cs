using System;
using System.Collections.Generic;
using AccountingSystem.Data.Repositories;

namespace AccountingSystem.Web.Models
{
    /// <summary>View model for the Trial Balance report.</summary>
    public class TrialBalanceViewModel
    {
        public DateTime FromDate { get; set; }
        public DateTime ToDate { get; set; }
        public IList<TrialBalanceRow> Rows { get; set; } = new List<TrialBalanceRow>();
        public decimal TotalDebits { get; set; }
        public decimal TotalCredits { get; set; }
        public bool IsBalanced { get; set; }
    }

    /// <summary>View model for the Income Statement (P&amp;L) report.</summary>
    public class IncomeStatementViewModel
    {
        public DateTime FromDate { get; set; }
        public DateTime ToDate { get; set; }
        public IList<IncomeStatementRow> Lines { get; set; } = new List<IncomeStatementRow>();
        public IncomeStatementSummary Summary { get; set; } = new IncomeStatementSummary();
    }

    /// <summary>View model for the Balance Sheet report.</summary>
    public class BalanceSheetViewModel
    {
        public DateTime AsOfDate { get; set; }
        public IList<BalanceSheetRow> Lines { get; set; } = new List<BalanceSheetRow>();
        public decimal TotalAssets { get; set; }
        public decimal TotalLiabilities { get; set; }
        public decimal TotalEquity { get; set; }
        public decimal CurrentEarnings { get; set; }

        /// <summary>The accounting equation check: Assets = Liabilities + Equity.</summary>
        public bool IsEquationBalanced => TotalAssets == TotalLiabilities + TotalEquity;
    }

    /// <summary>View model for the General Ledger detail report.</summary>
    public class GeneralLedgerViewModel
    {
        public DateTime FromDate { get; set; }
        public DateTime ToDate { get; set; }
        public int? AccountId { get; set; }
        public string AccountLabel { get; set; }
        public IList<GlOpeningBalance> OpeningBalances { get; set; } = new List<GlOpeningBalance>();
        public IList<GlDetailRow> Movements { get; set; } = new List<GlDetailRow>();
    }

    /// <summary>Query string parameters shared by the report screens.</summary>
    public class ReportFilter
    {
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public DateTime? AsOfDate { get; set; }
        public int? AccountId { get; set; }
    }
}
