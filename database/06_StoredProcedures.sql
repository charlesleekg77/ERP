/* ============================================================================
   06_StoredProcedures.sql
   ----------------------------------------------------------------------------
   Transactional posting and reporting procedures.

   usp_PostJournalEntry is the canonical atomic posting path. It performs all
   validations and the status transition inside a single transaction with the
   SERIALIZABLE-safe locking needed to guarantee the debit/credit invariant
   under concurrency, and it is safe to call from the C# service layer.

   Reporting procedures return result sets consumed by the C# reporting
   repository (Trial Balance, Income Statement, Balance Sheet, GL detail).

   Run against [AccountingSystemDB] after the tables and triggers exist.
   ============================================================================ */

USE [AccountingSystemDB];
GO

/* ---------------------------------------------------------------------------
   usp_NextDocumentNumber
   Generates the next sequential document number for a prefix (e.g. JV, INV,
   BILL, RCPT). Uses an updatable counter table with UPDLOCK/HOLDLOCK so two
   concurrent callers cannot receive the same number.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'gl.DocumentSequence', N'U') IS NULL
BEGIN
    CREATE TABLE gl.DocumentSequence
    (
        Prefix       NVARCHAR(10)  NOT NULL,
        FiscalYear   SMALLINT      NOT NULL,
        LastNumber   INT           NOT NULL CONSTRAINT DF_DocSeq_Last DEFAULT (0),
        CONSTRAINT PK_DocumentSequence PRIMARY KEY CLUSTERED (Prefix, FiscalYear)
    );
END
GO

IF OBJECT_ID(N'gl.usp_NextDocumentNumber', N'P') IS NOT NULL
    DROP PROCEDURE gl.usp_NextDocumentNumber;
GO
CREATE PROCEDURE gl.usp_NextDocumentNumber
    @Prefix     NVARCHAR(10),
    @FiscalYear SMALLINT,
    @NextNumber INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRAN;

    IF NOT EXISTS (SELECT 1 FROM gl.DocumentSequence WITH (UPDLOCK, HOLDLOCK)
                   WHERE Prefix = @Prefix AND FiscalYear = @FiscalYear)
    BEGIN
        INSERT INTO gl.DocumentSequence (Prefix, FiscalYear, LastNumber)
        VALUES (@Prefix, @FiscalYear, 0);
    END

    UPDATE gl.DocumentSequence
       SET LastNumber = LastNumber + 1,
           @NextNumber = LastNumber + 1
     WHERE Prefix = @Prefix AND FiscalYear = @FiscalYear;

    COMMIT TRAN;
END
GO

/* ---------------------------------------------------------------------------
   gl.usp_PostJournalEntry
   Atomically posts a draft/approved journal entry.

   Validation performed:
     1. Header exists and is not already posted/voided.
     2. Fiscal period (if set) is not closed.
     3. At least two lines exist.
     4. Total debits = total credits and both are > 0.
     5. Every account is active and postable (leaf).
     6. Every line has exactly one non-zero side.

   On success: Status -> 2 (Posted), PostingDate/PostedBy/PostedAt populated,
   header totals recomputed from detail, and the JournalId returned.

   Raises with a descriptive message and rolls back on any violation.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'gl.usp_PostJournalEntry', N'P') IS NOT NULL
    DROP PROCEDURE gl.usp_PostJournalEntry;
GO
CREATE PROCEDURE gl.usp_PostJournalEntry
    @JournalId  BIGINT,
    @PostedBy   NVARCHAR(100),
    @PostingDate DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;          -- any error aborts and rolls back the tran

    IF @PostedBy IS NULL OR LTRIM(RTRIM(@PostedBy)) = N''
        RAISERROR(N'PostedBy is required.', 16, 1);
    IF @PostingDate IS NULL SET @PostingDate = CAST(SYSUTCDATETIME() AS DATE);

    BEGIN TRY
        BEGIN TRAN;

        /* ---- 1. Lock and load the header. -------------------------------- */
        DECLARE @Status TINYINT, @FiscalPeriodId INT;
        SELECT @Status = Status, @FiscalPeriodId = FiscalPeriodId
          FROM gl.JournalHeader WITH (UPDLOCK, HOLDLOCK)
         WHERE JournalId = @JournalId AND IsDeleted = 0;

        IF @Status IS NULL
            RAISERROR(N'Journal entry %d was not found.', 16, 1, @JournalId);
        IF @Status IN (2, 3)
            RAISERROR(N'Journal entry %d has already been posted or voided.', 16, 1, @JournalId);

        /* ---- 2. Reject a closed fiscal period. --------------------------- */
        IF @FiscalPeriodId IS NOT NULL
           AND EXISTS (SELECT 1 FROM gl.FiscalPeriod WHERE FiscalPeriodId = @FiscalPeriodId AND IsClosed = 1)
            RAISERROR(N'The fiscal period for this entry is closed.', 16, 1);

        /* ---- 3/4. Balance check over the detail lines. ------------------- */
        DECLARE @Lines INT, @Debits DECIMAL(19,4), @Credits DECIMAL(19,4);
        SELECT @Lines   = COUNT(*),
               @Debits  = ISNULL(SUM(Debit), 0),
               @Credits = ISNULL(SUM(Credit), 0)
          FROM gl.JournalDetail
         WHERE JournalId = @JournalId;

        IF @Lines < 2
            RAISERROR(N'A journal entry requires at least two lines (one debit, one credit).', 16, 1);
        IF @Debits <> @Credits
            RAISERROR(N'Journal entry is not balanced: debits (%s) must equal credits (%s).', 16, 1,
                      CONVERT(NVARCHAR(40), @Debits), CONVERT(NVARCHAR(40), @Credits));
        IF @Debits = 0
            RAISERROR(N'A journal entry must have a non-zero amount.', 16, 1);

        /* ---- 5/6. Validate each line's account and amount. --------------- */
        IF EXISTS
        (
            SELECT 1
              FROM gl.JournalDetail d
              JOIN gl.Account a ON a.AccountId = d.AccountId
             WHERE d.JournalId = @JournalId
               AND (a.IsActive = 0 OR a.IsPostable = 0 OR a.IsDeleted = 1)
        )
            RAISERROR(N'One or more lines reference an inactive or non-postable account.', 16, 1);

        IF EXISTS
        (
            SELECT 1 FROM gl.JournalDetail
             WHERE JournalId = @JournalId
               AND ((Debit > 0 AND Credit > 0) OR (Debit = 0 AND Credit = 0))
        )
            RAISERROR(N'Each journal line must have exactly one non-zero side (debit or credit).', 16, 1);

        /* ---- Commit the posting. ----------------------------------------- */
        UPDATE gl.JournalHeader
           SET Status        = 2,
               PostingDate   = @PostingDate,
               PostedBy      = @PostedBy,
               PostedAt      = SYSUTCDATETIME(),
               TotalDebit    = @Debits,
               TotalCredit   = @Credits
         WHERE JournalId = @JournalId;

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH
END
GO

/* ---------------------------------------------------------------------------
   gl.usp_VoidJournalEntry
   Voids a posted entry. The original entry is retained (immutability) and a
   reversing entry is generated so the ledger nets to zero. Both operations
   occur in one transaction.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'gl.usp_VoidJournalEntry', N'P') IS NOT NULL
    DROP PROCEDURE gl.usp_VoidJournalEntry;
GO
CREATE PROCEDURE gl.usp_VoidJournalEntry
    @JournalId  BIGINT,
    @VoidedBy   NVARCHAR(100),
    @Reason     NVARCHAR(300),
    @ReversalJournalId BIGINT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRAN;

        DECLARE @Status TINYINT, @Voucher NVARCHAR(30), @TranDate DATE,
                @PeriodId INT, @Debits DECIMAL(19,4), @Credits DECIMAL(19,4);
        SELECT @Status = Status, @Voucher = VoucherNumber, @TranDate = TransactionDate,
               @PeriodId = FiscalPeriodId, @Debits = TotalDebit, @Credits = TotalCredit
          FROM gl.JournalHeader WITH (UPDLOCK, HOLDLOCK)
         WHERE JournalId = @JournalId AND IsDeleted = 0;

        IF @Status IS NULL  RAISERROR(N'Journal entry %d was not found.', 16, 1, @JournalId);
        IF @Status <> 2     RAISERROR(N'Only a posted journal entry can be voided.', 16, 1);

        /* Mark the original void (allowed by trg_JournalHeader_Immutable). */
        UPDATE gl.JournalHeader
           SET Status = 3, VoidedBy = @VoidedBy, VoidedAt = SYSUTCDATETIME(), VoidReason = @Reason
         WHERE JournalId = @JournalId;

        /* Build the reversing entry: debits and credits swapped.
           The reversal header is inserted as Draft first so that its detail
           lines can be added (trg_JournalDetail_Immutable blocks line inserts
           against a header that is already Posted), then it is posted. */
        DECLARE @NewNumber INT, @FiscalYear SMALLINT = YEAR(@TranDate);
        EXEC gl.usp_NextDocumentNumber N'REV', @FiscalYear, @NewNumber OUTPUT;

        INSERT INTO gl.JournalHeader
            (VoucherNumber, TransactionDate, PostingDate, Reference, Description,
             FiscalPeriodId, Status, SourceModule, CreatedBy, TotalDebit, TotalCredit)
        VALUES
            (CONCAT(N'REV-', @FiscalYear, N'-', RIGHT(N'000000' + CAST(@NewNumber AS NVARCHAR(6)), 6)),
             @TranDate, CAST(SYSUTCDATETIME() AS DATE), @Voucher,
             CONCAT(N'Reversal of ', @Voucher, N': ', @Reason),
             @PeriodId, 0, N'GL', @VoidedBy, @Credits, @Debits);

        SET @ReversalJournalId = SCOPE_IDENTITY();

        INSERT INTO gl.JournalDetail (JournalId, LineNumber, AccountId, Debit, Credit, Description)
        SELECT @ReversalJournalId, d.LineNumber, d.AccountId, d.Credit, d.Debit,
               CONCAT(N'Reversal: ', ISNULL(d.Description, N''))
          FROM gl.JournalDetail d
         WHERE d.JournalId = @JournalId;

        /* Post the reversal. Draft -> Posted is permitted by the header trigger. */
        UPDATE gl.JournalHeader
           SET Status = 2, PostedBy = @VoidedBy, PostedAt = SYSUTCDATETIME()
         WHERE JournalId = @ReversalJournalId;

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH
END
GO

/* ---------------------------------------------------------------------------
   gl.usp_GetTrialBalance
   Sums posted debits/credits per postable account within [@FromDate, @ToDate]
   and returns the net balance on the account's natural side.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'gl.usp_GetTrialBalance', N'P') IS NOT NULL
    DROP PROCEDURE gl.usp_GetTrialBalance;
GO
CREATE PROCEDURE gl.usp_GetTrialBalance
    @FromDate DATE = NULL,
    @ToDate   DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @FromDate IS NULL SET @FromDate = DATEFROMPARTS(YEAR(SYSUTCDATETIME()), 1, 1);
    IF @ToDate   IS NULL SET @ToDate   = CAST(SYSUTCDATETIME() AS DATE);

    SELECT
        a.AccountId,
        a.AccountCode,
        a.AccountName,
        a.AccountType,
        CASE a.AccountType
             WHEN 1 THEN N'Asset' WHEN 2 THEN N'Liability' WHEN 3 THEN N'Equity'
             WHEN 4 THEN N'Revenue' WHEN 5 THEN N'Expense' END AS AccountTypeName,
        ISNULL(SUM(d.Debit), 0)  AS TotalDebit,
        ISNULL(SUM(d.Credit), 0) AS TotalCredit,
        /* Net on the natural side: debits positive for D-natural, credits
           positive for C-natural. */
        CASE WHEN a.NormalBalance = 1
             THEN ISNULL(SUM(d.Debit), 0) - ISNULL(SUM(d.Credit), 0)
             ELSE ISNULL(SUM(d.Credit), 0) - ISNULL(SUM(d.Debit), 0)
        END AS NetBalance
      FROM gl.Account a
      LEFT JOIN gl.JournalDetail d ON d.AccountId = a.AccountId
      LEFT JOIN gl.JournalHeader h ON h.JournalId = d.JournalId
                                  AND h.Status = 2
                                  AND h.IsDeleted = 0
                                  AND h.TransactionDate BETWEEN @FromDate AND @ToDate
     WHERE a.IsDeleted = 0
     GROUP BY a.AccountId, a.AccountCode, a.AccountName, a.AccountType, a.NormalBalance
    HAVING ISNULL(SUM(d.Debit), 0) <> 0 OR ISNULL(SUM(d.Credit), 0) <> 0
     ORDER BY a.AccountCode;
END
GO

/* ---------------------------------------------------------------------------
   gl.usp_GetIncomeStatement
   Revenue (type 4) and Expense (type 5) activity for the period, with net
   income. Revenue is shown as credit-natural (positive).
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'gl.usp_GetIncomeStatement', N'P') IS NOT NULL
    DROP PROCEDURE gl.usp_GetIncomeStatement;
GO
CREATE PROCEDURE gl.usp_GetIncomeStatement
    @FromDate DATE = NULL,
    @ToDate   DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @FromDate IS NULL SET @FromDate = DATEFROMPARTS(YEAR(SYSUTCDATETIME()), 1, 1);
    IF @ToDate   IS NULL SET @ToDate   = CAST(SYSUTCDATETIME() AS DATE);

    ;WITH Activity AS
    (
        SELECT a.AccountId, a.AccountCode, a.AccountName, a.AccountType,
               ISNULL(SUM(d.Debit), 0)  AS TotalDebit,
               ISNULL(SUM(d.Credit), 0) AS TotalCredit
          FROM gl.Account a
          JOIN gl.JournalDetail d ON d.AccountId = a.AccountId
          JOIN gl.JournalHeader h ON h.JournalId = d.JournalId
                                 AND h.Status = 2 AND h.IsDeleted = 0
                                 AND h.TransactionDate BETWEEN @FromDate AND @ToDate
         WHERE a.IsDeleted = 0 AND a.AccountType IN (4, 5)
         GROUP BY a.AccountId, a.AccountCode, a.AccountName, a.AccountType
    )
    SELECT
        AccountId, AccountCode, AccountName,
        CASE AccountType WHEN 4 THEN N'Revenue' ELSE N'Expense' END AS SectionName,
        CASE WHEN AccountType = 4 THEN TotalCredit - TotalDebit
             ELSE TotalDebit - TotalCredit END AS Amount
      FROM Activity
     ORDER BY AccountType DESC, AccountCode;   -- Revenue (4) before Expense (5)

    /* Net income = total revenue - total expense. */
    SELECT
        ISNULL(SUM(CASE WHEN a.AccountType = 4 THEN d.Credit - d.Debit ELSE 0 END), 0) AS TotalRevenue,
        ISNULL(SUM(CASE WHEN a.AccountType = 5 THEN d.Debit - d.Credit ELSE 0 END), 0) AS TotalExpense,
        ISNULL(SUM(CASE WHEN a.AccountType = 4 THEN d.Credit - d.Debit ELSE 0 END), 0)
          - ISNULL(SUM(CASE WHEN a.AccountType = 5 THEN d.Debit - d.Credit ELSE 0 END), 0) AS NetIncome
      FROM gl.Account a
      JOIN gl.JournalDetail d ON d.AccountId = a.AccountId
      JOIN gl.JournalHeader h ON h.JournalId = d.JournalId
                             AND h.Status = 2 AND h.IsDeleted = 0
                             AND h.TransactionDate BETWEEN @FromDate AND @ToDate
     WHERE a.IsDeleted = 0 AND a.AccountType IN (4, 5);
END
GO

/* ---------------------------------------------------------------------------
   gl.usp_GetBalanceSheet
   Balances as of @AsOfDate. Assets = Liabilities + Equity + current-period
   earnings (revenue - expense), which closes the accounting equation.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'gl.usp_GetBalanceSheet', N'P') IS NOT NULL
    DROP PROCEDURE gl.usp_GetBalanceSheet;
GO
CREATE PROCEDURE gl.usp_GetBalanceSheet
    @AsOfDate DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOfDate IS NULL SET @AsOfDate = CAST(SYSUTCDATETIME() AS DATE);

    ;WITH Bal AS
    (
        SELECT a.AccountId, a.AccountCode, a.AccountName, a.AccountType, a.NormalBalance,
               ISNULL(SUM(d.Debit), 0)  AS TotalDebit,
               ISNULL(SUM(d.Credit), 0) AS TotalCredit
          FROM gl.Account a
          LEFT JOIN gl.JournalDetail d ON d.AccountId = a.AccountId
          LEFT JOIN gl.JournalHeader h ON h.JournalId = d.JournalId
                                      AND h.Status = 2 AND h.IsDeleted = 0
                                      AND h.TransactionDate <= @AsOfDate
         WHERE a.IsDeleted = 0 AND a.AccountType IN (1, 2, 3)
         GROUP BY a.AccountId, a.AccountCode, a.AccountName, a.AccountType, a.NormalBalance
    )
    SELECT
        AccountId, AccountCode, AccountName,
        CASE AccountType WHEN 1 THEN N'Asset' WHEN 2 THEN N'Liability' ELSE N'Equity' END AS SectionName,
        CASE WHEN NormalBalance = 1 THEN TotalDebit - TotalCredit
             ELSE TotalCredit - TotalDebit END AS Amount
      FROM Bal
     ORDER BY AccountType, AccountCode;

    /* Current-period earnings not yet closed to Retained Earnings. */
    DECLARE @Revenue DECIMAL(19,4), @Expense DECIMAL(19,4);
    SELECT @Revenue = ISNULL(SUM(d.Credit - d.Debit), 0)
      FROM gl.Account a
      JOIN gl.JournalDetail d ON d.AccountId = a.AccountId
      JOIN gl.JournalHeader h ON h.JournalId = d.JournalId
                             AND h.Status = 2 AND h.IsDeleted = 0
                             AND h.TransactionDate <= @AsOfDate
     WHERE a.AccountType = 4 AND a.IsDeleted = 0;

    SELECT @Expense = ISNULL(SUM(d.Debit - d.Credit), 0)
      FROM gl.Account a
      JOIN gl.JournalDetail d ON d.AccountId = a.AccountId
      JOIN gl.JournalHeader h ON h.JournalId = d.JournalId
                             AND h.Status = 2 AND h.IsDeleted = 0
                             AND h.TransactionDate <= @AsOfDate
     WHERE a.AccountType = 5 AND a.IsDeleted = 0;

    SELECT @Revenue AS TotalRevenue, @Expense AS TotalExpense, @Revenue - @Expense AS CurrentEarnings;
END
GO

/* ---------------------------------------------------------------------------
   gl.usp_GetGeneralLedgerDetail
   Running-balance ledger for one account (or all accounts) across a date range.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'gl.usp_GetGeneralLedgerDetail', N'P') IS NOT NULL
    DROP PROCEDURE gl.usp_GetGeneralLedgerDetail;
GO
CREATE PROCEDURE gl.usp_GetGeneralLedgerDetail
    @AccountId INT = NULL,
    @FromDate  DATE = NULL,
    @ToDate    DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @FromDate IS NULL SET @FromDate = DATEFROMPARTS(YEAR(SYSUTCDATETIME()), 1, 1);
    IF @ToDate   IS NULL SET @ToDate   = CAST(SYSUTCDATETIME() AS DATE);

    /* Opening balance carried forward from all postings before @FromDate. */
    SELECT
        a.AccountId, a.AccountCode, a.AccountName, a.NormalBalance,
        ISNULL(SUM(CASE WHEN a.NormalBalance = 1 THEN d.Debit - d.Credit
                        ELSE d.Credit - d.Debit END), 0) AS OpeningBalance
      FROM gl.Account a
      LEFT JOIN gl.JournalDetail d ON d.AccountId = a.AccountId
      LEFT JOIN gl.JournalHeader h ON h.JournalId = d.JournalId
                                  AND h.Status = 2 AND h.IsDeleted = 0
                                  AND h.TransactionDate < @FromDate
     WHERE a.IsDeleted = 0 AND (@AccountId IS NULL OR a.AccountId = @AccountId)
     GROUP BY a.AccountId, a.AccountCode, a.AccountName, a.NormalBalance;

    /* Movements in the range, ordered for running-balance computation in C#. */
    SELECT
        a.AccountId, a.AccountCode, a.AccountName, a.NormalBalance,
        h.JournalId, h.VoucherNumber, h.TransactionDate, h.Reference,
        d.JournalDetailId, d.LineNumber, d.Description, d.Debit, d.Credit
      FROM gl.JournalDetail d
      JOIN gl.JournalHeader h ON h.JournalId = d.JournalId
                             AND h.Status = 2 AND h.IsDeleted = 0
                             AND h.TransactionDate BETWEEN @FromDate AND @ToDate
      JOIN gl.Account a ON a.AccountId = d.AccountId
     WHERE a.IsDeleted = 0 AND (@AccountId IS NULL OR a.AccountId = @AccountId)
     ORDER BY a.AccountCode, h.TransactionDate, h.JournalId, d.LineNumber;
END
GO
