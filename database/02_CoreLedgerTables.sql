/* ============================================================================
   02_CoreLedgerTables.sql
   ----------------------------------------------------------------------------
   General Ledger core: Chart of Accounts, fiscal calendar, journal entries and
   the immutable audit trail. All monetary columns use DECIMAL(19,4) to avoid
   binary floating point rounding errors in financial calculations.

   Run against [AccountingSystemDB].
   ============================================================================ */

USE [AccountingSystemDB];
GO

/* ---------------------------------------------------------------------------
   gl.Account - Chart of Accounts (hierarchical, self-referencing).
   AccountType drives the natural balance side:
     1 = Asset      (normal balance: Debit)
     2 = Liability  (normal balance: Credit)
     3 = Equity     (normal balance: Credit)
     4 = Revenue    (normal balance: Credit)
     5 = Expense    (normal balance: Debit)
   Only leaf accounts (IsPostable = 1) may be used on journal lines.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'gl.Account', N'U') IS NULL
BEGIN
    CREATE TABLE gl.Account
    (
        AccountId        INT             IDENTITY(1,1) NOT NULL,
        AccountCode      NVARCHAR(20)    NOT NULL,
        AccountName      NVARCHAR(150)   NOT NULL,
        AccountType      TINYINT         NOT NULL,
        NormalBalance    TINYINT         NOT NULL,   -- 1 = Debit, 2 = Credit
        ParentAccountId  INT             NULL,
        Description      NVARCHAR(500)   NULL,
        IsPostable       BIT             NOT NULL CONSTRAINT DF_Account_IsPostable DEFAULT (1),
        IsActive         BIT             NOT NULL CONSTRAINT DF_Account_IsActive   DEFAULT (1),
        IsDeleted        BIT             NOT NULL CONSTRAINT DF_Account_IsDeleted  DEFAULT (0),
        CreatedBy        NVARCHAR(100)   NOT NULL CONSTRAINT DF_Account_CreatedBy  DEFAULT (SUSER_SNAME()),
        CreatedAt        DATETIME2(0)    NOT NULL CONSTRAINT DF_Account_CreatedAt  DEFAULT (SYSUTCDATETIME()),
        ModifiedBy       NVARCHAR(100)   NULL,
        ModifiedAt       DATETIME2(0)    NULL,
        RowVersion       ROWVERSION      NOT NULL,
        CONSTRAINT PK_Account PRIMARY KEY CLUSTERED (AccountId),
        CONSTRAINT FK_Account_Parent FOREIGN KEY (ParentAccountId)
            REFERENCES gl.Account (AccountId),
        CONSTRAINT CK_Account_Type   CHECK (AccountType BETWEEN 1 AND 5),
        CONSTRAINT CK_Account_Normal CHECK (NormalBalance IN (1, 2))
    );
END
GO

/* ---------------------------------------------------------------------------
   gl.FiscalPeriod - accounting calendar. A period is a posting window.
   Closed periods reject new postings (enforced in the service layer and in
   usp_PostJournalEntry).
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'gl.FiscalPeriod', N'U') IS NULL
BEGIN
    CREATE TABLE gl.FiscalPeriod
    (
        FiscalPeriodId  INT             IDENTITY(1,1) NOT NULL,
        FiscalYear      SMALLINT        NOT NULL,
        PeriodNumber    TINYINT         NOT NULL,      -- 1..12 (13 = year-end adj.)
        PeriodName      NVARCHAR(40)    NOT NULL,
        StartDate       DATE            NOT NULL,
        EndDate         DATE            NOT NULL,
        IsClosed        BIT             NOT NULL CONSTRAINT DF_FiscalPeriod_IsClosed DEFAULT (0),
        ClosedBy        NVARCHAR(100)   NULL,
        ClosedAt        DATETIME2(0)    NULL,
        CONSTRAINT PK_FiscalPeriod PRIMARY KEY CLUSTERED (FiscalPeriodId),
        CONSTRAINT UQ_FiscalPeriod_Year_Period UNIQUE (FiscalYear, PeriodNumber),
        CONSTRAINT CK_FiscalPeriod_Range CHECK (EndDate >= StartDate)
    );
END
GO

/* ---------------------------------------------------------------------------
   gl.JournalHeader - the voucher (transaction) envelope.
   Status lifecycle: 0 Draft -> 1 Approved -> 2 Posted (terminal)
                     3 Void  (terminal; only from Posted, reversal entry kept)
   Totals are denormalised so the balancing invariant can be checked without
   scanning detail rows, and are validated by trigger trg_JournalHeader_Balance.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'gl.JournalHeader', N'U') IS NULL
BEGIN
    CREATE TABLE gl.JournalHeader
    (
        JournalId        BIGINT          IDENTITY(1,1) NOT NULL,
        VoucherNumber    NVARCHAR(30)    NOT NULL,
        TransactionDate  DATE            NOT NULL,
        PostingDate      DATE            NULL,
        Reference        NVARCHAR(100)   NULL,
        Description      NVARCHAR(500)   NULL,
        FiscalPeriodId   INT             NULL,
        Status           TINYINT         NOT NULL CONSTRAINT DF_JournalHeader_Status DEFAULT (0),
        TotalDebit       DECIMAL(19,4)   NOT NULL CONSTRAINT DF_JournalHeader_TD DEFAULT (0),
        TotalCredit      DECIMAL(19,4)   NOT NULL CONSTRAINT DF_JournalHeader_TC DEFAULT (0),
        SourceModule     NVARCHAR(20)    NULL,   -- GL / AR / AP
        SourceDocumentId BIGINT          NULL,   -- InvoiceId, BillId, PaymentId ...
        CreatedBy        NVARCHAR(100)   NOT NULL CONSTRAINT DF_JournalHeader_CreatedBy DEFAULT (SUSER_SNAME()),
        CreatedAt        DATETIME2(0)    NOT NULL CONSTRAINT DF_JournalHeader_CreatedAt DEFAULT (SYSUTCDATETIME()),
        ApprovedBy       NVARCHAR(100)   NULL,
        ApprovedAt       DATETIME2(0)    NULL,
        PostedBy         NVARCHAR(100)   NULL,
        PostedAt         DATETIME2(0)    NULL,
        VoidedBy         NVARCHAR(100)   NULL,
        VoidedAt         DATETIME2(0)    NULL,
        VoidReason       NVARCHAR(300)   NULL,
        IsDeleted        BIT             NOT NULL CONSTRAINT DF_JournalHeader_IsDeleted DEFAULT (0),
        RowVersion       ROWVERSION      NOT NULL,
        CONSTRAINT PK_JournalHeader PRIMARY KEY CLUSTERED (JournalId),
        CONSTRAINT UQ_JournalHeader_Voucher UNIQUE (VoucherNumber),
        CONSTRAINT FK_JournalHeader_Period FOREIGN KEY (FiscalPeriodId)
            REFERENCES gl.FiscalPeriod (FiscalPeriodId),
        CONSTRAINT CK_JournalHeader_Status CHECK (Status BETWEEN 0 AND 3),
        CONSTRAINT CK_JournalHeader_NonNegative CHECK (TotalDebit >= 0 AND TotalCredit >= 0)
    );
END
GO

/* ---------------------------------------------------------------------------
   gl.JournalDetail - the debit/credit lines.
   A single line may be a debit OR a credit, never both (CK check), and never
   negative. Together the lines of a header must sum to zero.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'gl.JournalDetail', N'U') IS NULL
BEGIN
    CREATE TABLE gl.JournalDetail
    (
        JournalDetailId BIGINT          IDENTITY(1,1) NOT NULL,
        JournalId       BIGINT          NOT NULL,
        LineNumber      INT             NOT NULL,
        AccountId       INT             NOT NULL,
        Debit           DECIMAL(19,4)   NOT NULL CONSTRAINT DF_JournalDetail_Debit  DEFAULT (0),
        Credit          DECIMAL(19,4)   NOT NULL CONSTRAINT DF_JournalDetail_Credit DEFAULT (0),
        Description     NVARCHAR(300)   NULL,
        CostCenter      NVARCHAR(50)    NULL,
        CONSTRAINT PK_JournalDetail PRIMARY KEY CLUSTERED (JournalDetailId),
        CONSTRAINT FK_JournalDetail_Header FOREIGN KEY (JournalId)
            REFERENCES gl.JournalHeader (JournalId) ON DELETE CASCADE,
        CONSTRAINT FK_JournalDetail_Account FOREIGN KEY (AccountId)
            REFERENCES gl.Account (AccountId),
        CONSTRAINT CK_JournalDetail_Amounts CHECK
        (
            Debit >= 0 AND Credit >= 0
            AND NOT (Debit > 0 AND Credit > 0)   -- never both sides on one line
        )
    );
END
GO

/* ---------------------------------------------------------------------------
   sec.AuditLog - append-only audit trail. Captures the before/after image of
   every mutating operation. UPDATE and DELETE are denied by trg_AuditLog_Immutable
   so the trail cannot be tampered with from the application account.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'sec.AuditLog', N'U') IS NULL
BEGIN
    CREATE TABLE sec.AuditLog
    (
        AuditId     BIGINT          IDENTITY(1,1) NOT NULL,
        TableSchema NVARCHAR(20)    NOT NULL,
        TableName   NVARCHAR(128)   NOT NULL,
        RecordId    NVARCHAR(64)    NOT NULL,
        [Action]    NVARCHAR(10)    NOT NULL,   -- INSERT / UPDATE / DELETE
        OldValues   NVARCHAR(MAX)   NULL,
        NewValues   NVARCHAR(MAX)   NULL,
        UserId      NVARCHAR(100)   NULL,
        UserName    NVARCHAR(150)   NULL,
        IpAddress   NVARCHAR(45)    NULL,
        OccurredAt  DATETIME2(3)    NOT NULL CONSTRAINT DF_AuditLog_OccurredAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_AuditLog PRIMARY KEY CLUSTERED (AuditId),
        CONSTRAINT CK_AuditLog_Action CHECK ([Action] IN (N'INSERT', N'UPDATE', N'DELETE'))
    );
END
GO

/* ---------------------------------------------------------------------------
   sec.AppUser / sec.AppRole - minimal RBAC store used by the sample security
   layer. In an enterprise deployment this is typically federated to Active
   Directory / Entra ID; the table is retained here so the blueprint is
   self-contained and runnable on SQL Express.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'sec.AppRole', N'U') IS NULL
BEGIN
    CREATE TABLE sec.AppRole
    (
        RoleId      INT           IDENTITY(1,1) NOT NULL,
        RoleName    NVARCHAR(50)  NOT NULL,
        Description NVARCHAR(200) NULL,
        CONSTRAINT PK_AppRole PRIMARY KEY CLUSTERED (RoleId),
        CONSTRAINT UQ_AppRole_Name UNIQUE (RoleName)
    );
END
GO

IF OBJECT_ID(N'sec.AppUser', N'U') IS NULL
BEGIN
    CREATE TABLE sec.AppUser
    (
        UserId       INT           IDENTITY(1,1) NOT NULL,
        UserName     NVARCHAR(100) NOT NULL,
        DisplayName  NVARCHAR(150) NOT NULL,
        PasswordHash NVARCHAR(256) NULL,      -- PBKDF2/BCrypt; NULL when using Windows auth
        RoleId       INT           NOT NULL,
        IsActive     BIT           NOT NULL CONSTRAINT DF_AppUser_IsActive DEFAULT (1),
        CreatedAt    DATETIME2(0)  NOT NULL CONSTRAINT DF_AppUser_CreatedAt DEFAULT (SYSUTCDATETIME()),
        LastLoginAt  DATETIME2(0)  NULL,
        CONSTRAINT PK_AppUser PRIMARY KEY CLUSTERED (UserId),
        CONSTRAINT UQ_AppUser_UserName UNIQUE (UserName),
        CONSTRAINT FK_AppUser_Role FOREIGN KEY (RoleId) REFERENCES sec.AppRole (RoleId)
    );
END
GO
