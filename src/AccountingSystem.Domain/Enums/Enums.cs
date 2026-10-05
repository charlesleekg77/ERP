using System;

namespace AccountingSystem.Domain.Enums
{
    /// <summary>
    /// Classification of a ledger account. The value is persisted as TINYINT and
    /// determines the section a balance rolls up into on the financial statements.
    /// </summary>
    public enum AccountType : byte
    {
        Asset = 1,
        Liability = 2,
        Equity = 3,
        Revenue = 4,
        Expense = 5
    }

    /// <summary>
    /// The side on which an account's balance naturally increases. Assets and
    /// expenses are debit-natural; liabilities, equity and revenue are
    /// credit-natural.
    /// </summary>
    public enum NormalBalance : byte
    {
        Debit = 1,
        Credit = 2
    }

    /// <summary>
    /// Journal entry lifecycle. Draft entries may be edited; Approved entries
    /// are locked for review; Posted entries are immutable and affect balances.
    /// Void is terminal and is always accompanied by a reversing entry.
    /// </summary>
    public enum JournalStatus : byte
    {
        Draft = 0,
        Approved = 1,
        Posted = 2,
        Void = 3
    }

    /// <summary>Status of a customer invoice or vendor bill.</summary>
    public enum DocumentStatus : byte
    {
        Draft = 0,
        Open = 1,
        PartiallyPaid = 2,
        Paid = 3,
        Void = 4
    }

    /// <summary>Status of a purchase order.</summary>
    public enum PurchaseOrderStatus : byte
    {
        Draft = 0,
        Issued = 1,
        PartiallyReceived = 2,
        Received = 3,
        Cancelled = 4
    }

    /// <summary>Audit action captured in the append-only audit trail.</summary>
    public enum AuditAction
    {
        Insert,
        Update,
        Delete
    }

    /// <summary>Logical grouping used when posting subledger documents to the GL.</summary>
    public enum SourceModule
    {
        GL,
        AR,
        AP
    }
}
