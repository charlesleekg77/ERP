/* ============================================================================
   08_SampleData.sql
   ----------------------------------------------------------------------------
   Minimal smoke-test data: a customer, a vendor, an opening-balance journal
   and a balanced sample entry. Useful for verifying the deployment before
   loading real data.

   Run against [AccountingSystemDB] after 05_SeedChartOfAccounts.sql.
   Idempotent by document code/number.
   ============================================================================ */

USE [AccountingSystemDB];
GO

/* ------------------------------- Customer --------------------------------- */
IF NOT EXISTS (SELECT 1 FROM ar.Customer WHERE Code = N'CUST-001')
BEGIN
    INSERT INTO ar.Customer (Code, Name, Email, Phone, CreditLimit, PaymentTermsDays)
    VALUES (N'CUST-001', N'Acme Trading Ltd', N'ap@acme.example', N'+1-555-0100', 50000, 30);
END
GO

/* -------------------------------- Vendor ---------------------------------- */
IF NOT EXISTS (SELECT 1 FROM ap.Vendor WHERE Code = N'VEND-001')
BEGIN
    INSERT INTO ap.Vendor (Code, Name, Email, Phone, PaymentTermsDays)
    VALUES (N'VEND-001', N'Globex Supplies Inc', N'billing@globex.example', N'+1-555-0200', 30);
END
GO

/* -------------------- Opening-balance journal entry ------------------------ */
/* Debit Cash 100,000 / Credit Owner's Capital 100,000, posted directly.      */
DECLARE @cash INT = (SELECT AccountId FROM gl.Account WHERE AccountCode = N'1010');
DECLARE @capital INT = (SELECT AccountId FROM gl.Account WHERE AccountCode = N'3100');
DECLARE @period INT = (SELECT FiscalPeriodId FROM gl.FiscalPeriod
                        WHERE FiscalYear   = YEAR(CAST(SYSUTCDATETIME() AS DATE))
                          AND PeriodNumber = MONTH(CAST(SYSUTCDATETIME() AS DATE)));

IF NOT EXISTS (SELECT 1 FROM gl.JournalHeader WHERE VoucherNumber = N'JV-OPENING')
BEGIN
    INSERT INTO gl.JournalHeader (VoucherNumber, TransactionDate, Reference, Description, FiscalPeriodId, Status, SourceModule, CreatedBy, TotalDebit, TotalCredit)
    VALUES (N'JV-OPENING', CAST(SYSUTCDATETIME() AS DATE), N'OPENING', N'Owner capital contribution', @period, 0, N'GL', N'system', 100000, 100000);

    DECLARE @j BIGINT = SCOPE_IDENTITY();

    INSERT INTO gl.JournalDetail (JournalId, LineNumber, AccountId, Debit, Credit, Description) VALUES
    (@j, 1, @cash,    100000, 0,      N'Cash contribution'),
    (@j, 2, @capital, 0,      100000, N'Owner capital');
END
GO
