/* ============================================================================
   05_SeedChartOfAccounts.sql
   ----------------------------------------------------------------------------
   Seeds the fiscal calendar and a standard hierarchical Chart of Accounts.

   Numbering convention (enforced by convention, validated by usp_ValidateAccountCode):
     1000-1999  Assets          (normal balance: Debit)
     2000-2999  Liabilities     (normal balance: Credit)
     3000-3999  Equity          (normal balance: Credit)
     4000-4999  Revenue         (normal balance: Credit)
     5000-5999  Expenses        (normal balance: Debit)

   Control accounts referenced by the subledger defaults:
     1200  Accounts Receivable control
     2100  Accounts Payable control
     1010  Cash on Hand / Bank
     2200  Sales Tax Payable
     4100  Sales Revenue

   Idempotent: only inserts codes that do not already exist.
   Run against [AccountingSystemDB].
   ============================================================================ */

USE [AccountingSystemDB];
GO

/* ------------------------------ Fiscal calendar --------------------------- */
IF NOT EXISTS (SELECT 1 FROM gl.FiscalPeriod)
BEGIN
    DECLARE @year SMALLINT = YEAR(SYSUTCDATETIME());
    DECLARE @m TINYINT = 1;
    WHILE @m <= 12
    BEGIN
        INSERT INTO gl.FiscalPeriod (FiscalYear, PeriodNumber, PeriodName, StartDate, EndDate)
        VALUES
        (
            @year, @m,
            CONCAT(@year, N'-', RIGHT(N'0' + CAST(@m AS NVARCHAR(2)), 2)),
            DATEFROMPARTS(@year, @m, 1),
            EOMONTH(DATEFROMPARTS(@year, @m, 1))
        );
        SET @m += 1;
    END
END
GO

/* ------------------------------ Chart of Accounts ------------------------- */
/* A helper temp table lets us wire ParentAccountId after insertion by code. */
DECLARE @coa TABLE
(
    AccountCode NVARCHAR(20),
    AccountName NVARCHAR(150),
    AccountType TINYINT,
    NormalBalance TINYINT,
    ParentCode NVARCHAR(20),
    IsPostable BIT
);

INSERT INTO @coa (AccountCode, AccountName, AccountType, NormalBalance, ParentCode, IsPostable) VALUES
-- ASSETS (1 = Asset, Debit)
(N'1000', N'ASSETS',                    1, 1, NULL,   0),
(N'1010', N'Cash on Hand and at Bank',  1, 1, N'1000', 1),
(N'1020', N'Petty Cash',                1, 1, N'1000', 1),
(N'1100', N'Accounts Receivable',       1, 1, N'1000', 1),
(N'1200', N'Inventory',                 1, 1, N'1000', 1),
(N'1300', N'Prepaid Expenses',          1, 1, N'1000', 1),
(N'1500', N'Property, Plant & Equipment', 1, 1, N'1000', 1),
(N'1510', N'Accumulated Depreciation',  1, 2, N'1500', 1),
-- LIABILITIES (2 = Liability, Credit)
(N'2000', N'LIABILITIES',               2, 2, NULL,   0),
(N'2100', N'Accounts Payable',          2, 2, N'2000', 1),
(N'2200', N'Sales Tax Payable',         2, 2, N'2000', 1),
(N'2300', N'Accrued Liabilities',       2, 2, N'2000', 1),
(N'2400', N'Payroll Liabilities',       2, 2, N'2000', 1),
(N'2500', N'Long-Term Loans',           2, 2, N'2000', 1),
-- EQUITY (3 = Equity, Credit)
(N'3000', N'EQUITY',                    3, 2, NULL,   0),
(N'3100', N'Owner''s Capital',          3, 2, N'3000', 1),
(N'3200', N'Retained Earnings',         3, 2, N'3000', 1),
(N'3300', N'Current Year Earnings',     3, 2, N'3000', 1),
-- REVENUE (4 = Revenue, Credit)
(N'4000', N'REVENUE',                   4, 2, NULL,   0),
(N'4100', N'Sales Revenue',             4, 2, N'4000', 1),
(N'4200', N'Service Revenue',           4, 2, N'4000', 1),
(N'4900', N'Other Income',              4, 2, N'4000', 1),
-- EXPENSES (5 = Expense, Debit)
(N'5000', N'EXPENSES',                  5, 1, NULL,   0),
(N'5100', N'Cost of Goods Sold',        5, 1, N'5000', 1),
(N'5200', N'Salaries & Wages',          5, 1, N'5000', 1),
(N'5300', N'Rent Expense',              5, 1, N'5000', 1),
(N'5400', N'Utilities Expense',         5, 1, N'5000', 1),
(N'5500', N'Office Supplies',           5, 1, N'5000', 1),
(N'5600', N'Professional Fees',         5, 1, N'5000', 1),
(N'5900', N'Depreciation Expense',      5, 1, N'5000', 1);

/* Insert roots first (ParentCode IS NULL), then children. */
INSERT INTO gl.Account (AccountCode, AccountName, AccountType, NormalBalance, ParentAccountId, IsPostable)
SELECT c.AccountCode, c.AccountName, c.AccountType, c.NormalBalance, NULL, c.IsPostable
  FROM @coa c
 WHERE c.ParentCode IS NULL
   AND NOT EXISTS (SELECT 1 FROM gl.Account a WHERE a.AccountCode = c.AccountCode);

INSERT INTO gl.Account (AccountCode, AccountName, AccountType, NormalBalance, ParentAccountId, IsPostable)
SELECT c.AccountCode, c.AccountName, c.AccountType, c.NormalBalance, p.AccountId, c.IsPostable
  FROM @coa c
  JOIN gl.Account p ON p.AccountCode = c.ParentCode
 WHERE c.ParentCode IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM gl.Account a WHERE a.AccountCode = c.AccountCode);
GO

/* ------------------------------ RBAC seed --------------------------------- */
IF NOT EXISTS (SELECT 1 FROM sec.AppRole)
BEGIN
    INSERT INTO sec.AppRole (RoleName, Description) VALUES
    (N'Administrator',   N'Full system access including user and period management.'),
    (N'Accountant',      N'Create/post journals, manage AR/AP, run all reports.'),
    (N'Approver',        N'Approve draft journals; cannot post directly.'),
    (N'Clerk',           N'Create draft documents only; no posting rights.'),
    (N'Auditor',         N'Read-only access to ledgers, reports and audit trail.');
END
GO

/* A bootstrap administrator. PasswordHash is NULL because the sample uses
   Windows authentication; replace with a PBKDF2 hash when forms auth is used. */
IF NOT EXISTS (SELECT 1 FROM sec.AppUser WHERE UserName = N'administrator')
BEGIN
    INSERT INTO sec.AppUser (UserName, DisplayName, RoleId, IsActive)
    SELECT N'administrator', N'Bootstrap Administrator', RoleId, 1
      FROM sec.AppRole WHERE RoleName = N'Administrator';
END
GO
