/* ============================================================================
   04_IndexesAndTriggers.sql
   ----------------------------------------------------------------------------
   Performance indexes and integrity triggers.

   The triggers enforce the accounting invariants at the database boundary so
   that no client (application, script, or ad-hoc query) can persist an
   unbalanced journal entry, modify a posted entry, or tamper with the audit
   trail.

   Run against [AccountingSystemDB].
   ============================================================================ */

USE [AccountingSystemDB];
GO

/* ---------------------------- Performance indexes ------------------------- */

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Account_Code')
    CREATE NONCLUSTERED INDEX IX_Account_Code ON gl.Account (AccountCode) INCLUDE (AccountName, AccountType, IsPostable, IsActive);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Account_Parent')
    CREATE NONCLUSTERED INDEX IX_Account_Parent ON gl.Account (ParentAccountId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_JournalHeader_Date_Status')
    CREATE NONCLUSTERED INDEX IX_JournalHeader_Date_Status ON gl.JournalHeader (TransactionDate, Status) INCLUDE (TotalDebit, TotalCredit, VoucherNumber);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_JournalDetail_Account')
    CREATE NONCLUSTERED INDEX IX_JournalDetail_Account ON gl.JournalDetail (AccountId) INCLUDE (JournalId, Debit, Credit);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_JournalDetail_Header')
    CREATE NONCLUSTERED INDEX IX_JournalDetail_Header ON gl.JournalDetail (JournalId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Invoice_Customer_Status')
    CREATE NONCLUSTERED INDEX IX_Invoice_Customer_Status ON ar.Invoice (CustomerId, Status) INCLUDE (TotalAmount, PaidAmount, DueDate);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Bill_Vendor_Status')
    CREATE NONCLUSTERED INDEX IX_Bill_Vendor_Status ON ap.VendorBill (VendorId, Status) INCLUDE (TotalAmount, PaidAmount, DueDate);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AuditLog_Record')
    CREATE NONCLUSTERED INDEX IX_AuditLog_Record ON sec.AuditLog (TableName, RecordId, OccurredAt DESC);
GO
