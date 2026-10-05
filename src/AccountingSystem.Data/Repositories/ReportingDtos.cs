using System;

namespace AccountingSystem.Data.Repositories
{
    /// <summary>One row of the Trial Balance report.</summary>
    public class TrialBalanceRow
    {
        public int AccountId { get; set; }
        public string AccountCode { get; set; }
        public string AccountName { get; set; }
        public byte AccountType { get; set; }
        public string AccountTypeName { get; set; }
        public decimal TotalDebit { get; set; }
        public decimal TotalCredit { get; set; }
        public decimal NetBalance { get; set; }
    }

    /// <summary>One line of the Income Statement (P&amp;L).</summary>
    public class IncomeStatementRow
    {
        public int AccountId { get; set; }
        public string AccountCode { get; set; }
        public string AccountName { get; set; }
        public string SectionName { get; set; }
        public decimal Amount { get; set; }
    }

    /// <summary>Summary totals for the Income Statement.</summary>
    public class IncomeStatementSummary
    {
        public decimal TotalRevenue { get; set; }
        public decimal TotalExpense { get; set; }
        public decimal NetIncome { get; set; }
    }

    /// <summary>One line of the Balance Sheet.</summary>
    public class BalanceSheetRow
    {
        public int AccountId { get; set; }
        public string AccountCode { get; set; }
        public string AccountName { get; set; }
        public string SectionName { get; set; }
        public decimal Amount { get; set; }
    }

    /// <summary>Current-period earnings that close the accounting equation.</summary>
    public class BalanceSheetEarnings
    {
        public decimal TotalRevenue { get; set; }
        public decimal TotalExpense { get; set; }
        public decimal CurrentEarnings { get; set; }
    }

    /// <summary>Opening balance carried into a General Ledger Detail report.</summary>
    public class GlOpeningBalance
    {
        public int AccountId { get; set; }
        public string AccountCode { get; set; }
        public string AccountName { get; set; }
        public byte NormalBalance { get; set; }
        public decimal OpeningBalance { get; set; }
    }

    /// <summary>One movement line of the General Ledger Detail report.</summary>
    public class GlDetailRow
    {
        public int AccountId { get; set; }
        public string AccountCode { get; set; }
        public string AccountName { get; set; }
        public byte NormalBalance { get; set; }
        public long JournalId { get; set; }
        public string VoucherNumber { get; set; }
        public DateTime TransactionDate { get; set; }
        public string Reference { get; set; }
        public long JournalDetailId { get; set; }
        public int LineNumber { get; set; }
        public string Description { get; set; }
        public decimal Debit { get; set; }
        public decimal Credit { get; set; }

        /// <summary>Running balance, computed in code in document order.</summary>
        public decimal RunningBalance { get; set; }
    }
}
