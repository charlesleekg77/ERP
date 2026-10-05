/* ============================================================================
   03_SubledgerTables.sql
   ----------------------------------------------------------------------------
   Accounts Receivable (ar) and Accounts Payable (ap) subledger tables.

   Design notes:
     * Monetary columns are DECIMAL(19,4); tax rates are DECIMAL(9,4).
     * Every recognised document carries JournalId, the GL entry that posted it,
       so the subledger always reconciles to the General Ledger.
     * Master records (Customer, Vendor) use soft delete (IsDeleted); the
       application role has no DELETE grant on these schemas.
     * Receipts and PaymentVouchers allocate across many documents through the
       allocation tables, so partial payments are supported.

   Run against [AccountingSystemDB] AFTER 02_CoreLedgerTables.sql.
   ============================================================================ */

USE [AccountingSystemDB];
GO

/* ===========================================================================
   ACCOUNTS RECEIVABLE
   =========================================================================== */

/* ---------------------------------------------------------------------------
   ar.Customer - customer master with credit limit and running balance.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'ar.Customer', N'U') IS NULL
BEGIN
    CREATE TABLE ar.Customer
    (
        CustomerId       INT             IDENTITY(1,1) NOT NULL,
        Code             NVARCHAR(20)    NOT NULL,
        Name             NVARCHAR(150)   NOT NULL,
        Email            NVARCHAR(150)   NULL,
        Phone            NVARCHAR(50)    NULL,
        BillingAddress   NVARCHAR(400)   NULL,
        TaxId            NVARCHAR(50)    NULL,
        CreditLimit      DECIMAL(19,4)   NOT NULL CONSTRAINT DF_Customer_CreditLimit DEFAULT (0),
        PaymentTermsDays SMALLINT        NOT NULL CONSTRAINT DF_Customer_Terms DEFAULT (30),
        ArAccountId      INT             NULL,   -- AR control account override
        Balance          DECIMAL(19,4)   NOT NULL CONSTRAINT DF_Customer_Balance DEFAULT (0),
        IsActive         BIT             NOT NULL CONSTRAINT DF_Customer_IsActive DEFAULT (1),
        IsDeleted        BIT             NOT NULL CONSTRAINT DF_Customer_IsDeleted DEFAULT (0),
        CreatedBy        NVARCHAR(100)   NOT NULL CONSTRAINT DF_Customer_CreatedBy DEFAULT (SUSER_SNAME()),
        CreatedAt        DATETIME2(0)    NOT NULL CONSTRAINT DF_Customer_CreatedAt DEFAULT (SYSUTCDATETIME()),
        ModifiedBy       NVARCHAR(100)   NULL,
        ModifiedAt       DATETIME2(0)    NULL,
        RowVersion       ROWVERSION      NOT NULL,
        CONSTRAINT PK_Customer PRIMARY KEY CLUSTERED (CustomerId),
        CONSTRAINT UQ_Customer_Code UNIQUE (Code),
        CONSTRAINT FK_Customer_ArAccount FOREIGN KEY (ArAccountId) REFERENCES gl.Account (AccountId),
        CONSTRAINT CK_Customer_CreditLimit CHECK (CreditLimit >= 0)
    );
END
GO

/* ---------------------------------------------------------------------------
   ar.Invoice - customer invoice header.
   Status: 0 Draft, 1 Open, 2 PartiallyPaid, 3 Paid, 4 Void.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'ar.Invoice', N'U') IS NULL
BEGIN
    CREATE TABLE ar.Invoice
    (
        InvoiceId     BIGINT          IDENTITY(1,1) NOT NULL,
        InvoiceNumber NVARCHAR(30)    NOT NULL,
        CustomerId    INT             NOT NULL,
        IssueDate     DATE            NOT NULL,
        DueDate       DATE            NOT NULL,
        SubTotal      DECIMAL(19,4)   NOT NULL CONSTRAINT DF_Invoice_SubTotal DEFAULT (0),
        TaxAmount     DECIMAL(19,4)   NOT NULL CONSTRAINT DF_Invoice_TaxAmount DEFAULT (0),
        TotalAmount   DECIMAL(19,4)   NOT NULL CONSTRAINT DF_Invoice_TotalAmount DEFAULT (0),
        PaidAmount    DECIMAL(19,4)   NOT NULL CONSTRAINT DF_Invoice_PaidAmount DEFAULT (0),
        Status        TINYINT         NOT NULL CONSTRAINT DF_Invoice_Status DEFAULT (0),
        JournalId     BIGINT          NULL,   -- GL entry that recognised the sale
        Notes         NVARCHAR(1000)  NULL,
        IsDeleted     BIT             NOT NULL CONSTRAINT DF_Invoice_IsDeleted DEFAULT (0),
        CreatedBy     NVARCHAR(100)   NOT NULL CONSTRAINT DF_Invoice_CreatedBy DEFAULT (SUSER_SNAME()),
        CreatedAt     DATETIME2(0)    NOT NULL CONSTRAINT DF_Invoice_CreatedAt DEFAULT (SYSUTCDATETIME()),
        ModifiedBy    NVARCHAR(100)   NULL,
        ModifiedAt    DATETIME2(0)    NULL,
        RowVersion    ROWVERSION      NOT NULL,
        CONSTRAINT PK_Invoice PRIMARY KEY CLUSTERED (InvoiceId),
        CONSTRAINT UQ_Invoice_Number UNIQUE (InvoiceNumber),
        CONSTRAINT FK_Invoice_Customer FOREIGN KEY (CustomerId) REFERENCES ar.Customer (CustomerId),
        CONSTRAINT FK_Invoice_Journal  FOREIGN KEY (JournalId)  REFERENCES gl.JournalHeader (JournalId),
        CONSTRAINT CK_Invoice_Status   CHECK (Status BETWEEN 0 AND 4),
        CONSTRAINT CK_Invoice_DueDate  CHECK (DueDate >= IssueDate),
        CONSTRAINT CK_Invoice_Amounts  CHECK (PaidAmount >= 0 AND PaidAmount <= TotalAmount)
    );
END
GO

/* ---------------------------------------------------------------------------
   ar.InvoiceLine - itemised invoice lines with per-line tax.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'ar.InvoiceLine', N'U') IS NULL
BEGIN
    CREATE TABLE ar.InvoiceLine
    (
        InvoiceLineId    BIGINT          IDENTITY(1,1) NOT NULL,
        InvoiceId        BIGINT          NOT NULL,
        LineNumber       INT             NOT NULL,
        Description      NVARCHAR(300)   NOT NULL,
        Quantity         DECIMAL(19,4)   NOT NULL CONSTRAINT DF_InvoiceLine_Qty DEFAULT (1),
        UnitPrice        DECIMAL(19,4)   NOT NULL CONSTRAINT DF_InvoiceLine_UnitPrice DEFAULT (0),
        TaxRate          DECIMAL(9,4)    NOT NULL CONSTRAINT DF_InvoiceLine_TaxRate DEFAULT (0),
        LineTotal        DECIMAL(19,4)   NOT NULL CONSTRAINT DF_InvoiceLine_LineTotal DEFAULT (0),
        RevenueAccountId INT             NULL,
        CONSTRAINT PK_InvoiceLine PRIMARY KEY CLUSTERED (InvoiceLineId),
        CONSTRAINT FK_InvoiceLine_Invoice FOREIGN KEY (InvoiceId) REFERENCES ar.Invoice (InvoiceId),
        CONSTRAINT FK_InvoiceLine_RevenueAccount FOREIGN KEY (RevenueAccountId) REFERENCES gl.Account (AccountId),
        CONSTRAINT CK_InvoiceLine_Qty CHECK (Quantity > 0),
        CONSTRAINT CK_InvoiceLine_TaxRate CHECK (TaxRate >= 0)
    );
END
GO

/* ---------------------------------------------------------------------------
   ar.Receipt - money received from a customer. A receipt may be allocated to
   one or more invoices (partial or full payment).
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'ar.Receipt', N'U') IS NULL
BEGIN
    CREATE TABLE ar.Receipt
    (
        ReceiptId     BIGINT          IDENTITY(1,1) NOT NULL,
        ReceiptNumber NVARCHAR(30)    NOT NULL,
        CustomerId    INT             NOT NULL,
        ReceiptDate   DATE            NOT NULL,
        Amount        DECIMAL(19,4)   NOT NULL,
        DepositAccountId INT          NULL,   -- bank/cash account debited
        Reference     NVARCHAR(100)   NULL,
        JournalId     BIGINT          NULL,
        IsDeleted     BIT             NOT NULL CONSTRAINT DF_Receipt_IsDeleted DEFAULT (0),
        CreatedBy     NVARCHAR(100)   NOT NULL CONSTRAINT DF_Receipt_CreatedBy DEFAULT (SUSER_SNAME()),
        CreatedAt     DATETIME2(0)    NOT NULL CONSTRAINT DF_Receipt_CreatedAt DEFAULT (SYSUTCDATETIME()),
        RowVersion    ROWVERSION      NOT NULL,
        CONSTRAINT PK_Receipt PRIMARY KEY CLUSTERED (ReceiptId),
        CONSTRAINT UQ_Receipt_Number UNIQUE (ReceiptNumber),
        CONSTRAINT FK_Receipt_Customer FOREIGN KEY (CustomerId) REFERENCES ar.Customer (CustomerId),
        CONSTRAINT FK_Receipt_Journal  FOREIGN KEY (JournalId)  REFERENCES gl.JournalHeader (JournalId),
        CONSTRAINT CK_Receipt_Amount   CHECK (Amount > 0)
    );
END
GO

IF OBJECT_ID(N'ar.ReceiptAllocation', N'U') IS NULL
BEGIN
    CREATE TABLE ar.ReceiptAllocation
    (
        ReceiptAllocationId BIGINT        IDENTITY(1,1) NOT NULL,
        ReceiptId           BIGINT        NOT NULL,
        InvoiceId           BIGINT        NOT NULL,
        AllocatedAmount     DECIMAL(19,4) NOT NULL,
        CreatedAt           DATETIME2(0)  NOT NULL CONSTRAINT DF_ReceiptAlloc_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ReceiptAllocation PRIMARY KEY CLUSTERED (ReceiptAllocationId),
        CONSTRAINT FK_ReceiptAllocation_Receipt FOREIGN KEY (ReceiptId) REFERENCES ar.Receipt (ReceiptId),
        CONSTRAINT FK_ReceiptAllocation_Invoice FOREIGN KEY (InvoiceId) REFERENCES ar.Invoice (InvoiceId),
        CONSTRAINT UQ_ReceiptAllocation UNIQUE (ReceiptId, InvoiceId),
        CONSTRAINT CK_ReceiptAllocation_Amount CHECK (AllocatedAmount > 0)
    );
END
GO

/* ===========================================================================
   ACCOUNTS PAYABLE
   =========================================================================== */

/* ---------------------------------------------------------------------------
   ap.Vendor - vendor master with AP control account and running balance.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'ap.Vendor', N'U') IS NULL
BEGIN
    CREATE TABLE ap.Vendor
    (
        VendorId         INT             IDENTITY(1,1) NOT NULL,
        Code             NVARCHAR(20)    NOT NULL,
        Name             NVARCHAR(150)   NOT NULL,
        Email            NVARCHAR(150)   NULL,
        Phone            NVARCHAR(50)    NULL,
        Address          NVARCHAR(400)   NULL,
        TaxId            NVARCHAR(50)    NULL,
        PaymentTermsDays SMALLINT        NOT NULL CONSTRAINT DF_Vendor_Terms DEFAULT (30),
        ApAccountId      INT             NULL,   -- AP control account override
        Balance          DECIMAL(19,4)   NOT NULL CONSTRAINT DF_Vendor_Balance DEFAULT (0),
        IsActive         BIT             NOT NULL CONSTRAINT DF_Vendor_IsActive DEFAULT (1),
        IsDeleted        BIT             NOT NULL CONSTRAINT DF_Vendor_IsDeleted DEFAULT (0),
        CreatedBy        NVARCHAR(100)   NOT NULL CONSTRAINT DF_Vendor_CreatedBy DEFAULT (SUSER_SNAME()),
        CreatedAt        DATETIME2(0)    NOT NULL CONSTRAINT DF_Vendor_CreatedAt DEFAULT (SYSUTCDATETIME()),
        ModifiedBy       NVARCHAR(100)   NULL,
        ModifiedAt       DATETIME2(0)    NULL,
        RowVersion       ROWVERSION      NOT NULL,
        CONSTRAINT PK_Vendor PRIMARY KEY CLUSTERED (VendorId),
        CONSTRAINT UQ_Vendor_Code UNIQUE (Code),
        CONSTRAINT FK_Vendor_ApAccount FOREIGN KEY (ApAccountId) REFERENCES gl.Account (AccountId)
    );
END
GO

/* ---------------------------------------------------------------------------
   ap.PurchaseOrder - commitment document. Status: 0 Draft, 1 Issued,
   2 PartiallyReceived, 3 Received, 4 Cancelled.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'ap.PurchaseOrder', N'U') IS NULL
BEGIN
    CREATE TABLE ap.PurchaseOrder
    (
        PurchaseOrderId BIGINT        IDENTITY(1,1) NOT NULL,
        PONumber        NVARCHAR(30)  NOT NULL,
        VendorId        INT           NOT NULL,
        OrderDate       DATE          NOT NULL,
        ExpectedDate    DATE          NULL,
        Status          TINYINT       NOT NULL CONSTRAINT DF_PO_Status DEFAULT (0),
        Notes           NVARCHAR(1000) NULL,
        IsDeleted       BIT           NOT NULL CONSTRAINT DF_PO_IsDeleted DEFAULT (0),
        CreatedBy       NVARCHAR(100) NOT NULL CONSTRAINT DF_PO_CreatedBy DEFAULT (SUSER_SNAME()),
        CreatedAt       DATETIME2(0)  NOT NULL CONSTRAINT DF_PO_CreatedAt DEFAULT (SYSUTCDATETIME()),
        RowVersion      ROWVERSION    NOT NULL,
        CONSTRAINT PK_PurchaseOrder PRIMARY KEY CLUSTERED (PurchaseOrderId),
        CONSTRAINT UQ_PurchaseOrder_Number UNIQUE (PONumber),
        CONSTRAINT FK_PurchaseOrder_Vendor FOREIGN KEY (VendorId) REFERENCES ap.Vendor (VendorId),
        CONSTRAINT CK_PurchaseOrder_Status CHECK (Status BETWEEN 0 AND 4)
    );
END
GO

IF OBJECT_ID(N'ap.PurchaseOrderLine', N'U') IS NULL
BEGIN
    CREATE TABLE ap.PurchaseOrderLine
    (
        PurchaseOrderLineId BIGINT        IDENTITY(1,1) NOT NULL,
        PurchaseOrderId     BIGINT        NOT NULL,
        LineNumber          INT           NOT NULL,
        Description         NVARCHAR(300) NOT NULL,
        Quantity            DECIMAL(19,4) NOT NULL CONSTRAINT DF_POLine_Qty DEFAULT (1),
        UnitPrice           DECIMAL(19,4) NOT NULL CONSTRAINT DF_POLine_UnitPrice DEFAULT (0),
        ExpenseAccountId    INT           NULL,
        CONSTRAINT PK_PurchaseOrderLine PRIMARY KEY CLUSTERED (PurchaseOrderLineId),
        CONSTRAINT FK_POLine_PurchaseOrder FOREIGN KEY (PurchaseOrderId) REFERENCES ap.PurchaseOrder (PurchaseOrderId),
        CONSTRAINT FK_POLine_ExpenseAccount FOREIGN KEY (ExpenseAccountId) REFERENCES gl.Account (AccountId),
        CONSTRAINT CK_POLine_Qty CHECK (Quantity > 0)
    );
END
GO

/* ---------------------------------------------------------------------------
   ap.VendorBill - the vendor's invoice to us. Matches the VendorBill entity.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'ap.VendorBill', N'U') IS NULL
BEGIN
    CREATE TABLE ap.VendorBill
    (
        BillId          BIGINT          IDENTITY(1,1) NOT NULL,
        BillNumber      NVARCHAR(30)    NOT NULL,
        VendorInvoiceNo NVARCHAR(50)    NULL,
        VendorId        INT             NOT NULL,
        PurchaseOrderId BIGINT          NULL,
        IssueDate       DATE            NOT NULL,
        DueDate         DATE            NOT NULL,
        SubTotal        DECIMAL(19,4)   NOT NULL CONSTRAINT DF_Bill_SubTotal DEFAULT (0),
        TaxAmount       DECIMAL(19,4)   NOT NULL CONSTRAINT DF_Bill_TaxAmount DEFAULT (0),
        TotalAmount     DECIMAL(19,4)   NOT NULL CONSTRAINT DF_Bill_TotalAmount DEFAULT (0),
        PaidAmount      DECIMAL(19,4)   NOT NULL CONSTRAINT DF_Bill_PaidAmount DEFAULT (0),
        Status          TINYINT         NOT NULL CONSTRAINT DF_Bill_Status DEFAULT (0),
        JournalId       BIGINT          NULL,
        Notes           NVARCHAR(1000)  NULL,
        IsDeleted       BIT             NOT NULL CONSTRAINT DF_Bill_IsDeleted DEFAULT (0),
        CreatedBy       NVARCHAR(100)   NOT NULL CONSTRAINT DF_Bill_CreatedBy DEFAULT (SUSER_SNAME()),
        CreatedAt       DATETIME2(0)    NOT NULL CONSTRAINT DF_Bill_CreatedAt DEFAULT (SYSUTCDATETIME()),
        ModifiedBy      NVARCHAR(100)   NULL,
        ModifiedAt      DATETIME2(0)    NULL,
        RowVersion      ROWVERSION      NOT NULL,
        CONSTRAINT PK_VendorBill PRIMARY KEY CLUSTERED (BillId),
        CONSTRAINT UQ_VendorBill_Number UNIQUE (BillNumber),
        CONSTRAINT FK_VendorBill_Vendor FOREIGN KEY (VendorId) REFERENCES ap.Vendor (VendorId),
        CONSTRAINT FK_VendorBill_PO     FOREIGN KEY (PurchaseOrderId) REFERENCES ap.PurchaseOrder (PurchaseOrderId),
        CONSTRAINT FK_VendorBill_Journal FOREIGN KEY (JournalId) REFERENCES gl.JournalHeader (JournalId),
        CONSTRAINT CK_VendorBill_Status CHECK (Status BETWEEN 0 AND 4),
        CONSTRAINT CK_VendorBill_Amounts CHECK (PaidAmount >= 0 AND PaidAmount <= TotalAmount)
    );
END
GO

IF OBJECT_ID(N'ap.VendorBillLine', N'U') IS NULL
BEGIN
    CREATE TABLE ap.VendorBillLine
    (
        BillLineId       BIGINT          IDENTITY(1,1) NOT NULL,
        BillId           BIGINT          NOT NULL,
        LineNumber       INT             NOT NULL,
        Description      NVARCHAR(300)   NOT NULL,
        Quantity         DECIMAL(19,4)   NOT NULL CONSTRAINT DF_BillLine_Qty DEFAULT (1),
        UnitPrice        DECIMAL(19,4)   NOT NULL CONSTRAINT DF_BillLine_UnitPrice DEFAULT (0),
        TaxRate          DECIMAL(9,4)    NOT NULL CONSTRAINT DF_BillLine_TaxRate DEFAULT (0),
        LineTotal        DECIMAL(19,4)   NOT NULL CONSTRAINT DF_BillLine_LineTotal DEFAULT (0),
        ExpenseAccountId INT             NULL,
        CONSTRAINT PK_VendorBillLine PRIMARY KEY CLUSTERED (BillLineId),
        CONSTRAINT FK_BillLine_VendorBill FOREIGN KEY (BillId) REFERENCES ap.VendorBill (BillId),
        CONSTRAINT FK_BillLine_ExpenseAccount FOREIGN KEY (ExpenseAccountId) REFERENCES gl.Account (AccountId),
        CONSTRAINT CK_BillLine_Qty CHECK (Quantity > 0),
        CONSTRAINT CK_BillLine_TaxRate CHECK (TaxRate >= 0)
    );
END
GO

/* ---------------------------------------------------------------------------
   ap.PaymentVoucher - money paid to a vendor, allocated across bills.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'ap.PaymentVoucher', N'U') IS NULL
BEGIN
    CREATE TABLE ap.PaymentVoucher
    (
        PaymentVoucherId BIGINT        IDENTITY(1,1) NOT NULL,
        VoucherNumber    NVARCHAR(30)  NOT NULL,
        VendorId         INT           NOT NULL,
        PaymentDate      DATE          NOT NULL,
        Amount           DECIMAL(19,4) NOT NULL,
        PaymentAccountId INT           NULL,   -- bank/cash account credited
        Method           NVARCHAR(30)  NULL,   -- Cheque, ACH, Wire, Cash
        Reference        NVARCHAR(100) NULL,
        JournalId        BIGINT        NULL,
        IsDeleted        BIT           NOT NULL CONSTRAINT DF_Payment_IsDeleted DEFAULT (0),
        CreatedBy        NVARCHAR(100) NOT NULL CONSTRAINT DF_Payment_CreatedBy DEFAULT (SUSER_SNAME()),
        CreatedAt        DATETIME2(0)  NOT NULL CONSTRAINT DF_Payment_CreatedAt DEFAULT (SYSUTCDATETIME()),
        RowVersion       ROWVERSION    NOT NULL,
        CONSTRAINT PK_PaymentVoucher PRIMARY KEY CLUSTERED (PaymentVoucherId),
        CONSTRAINT UQ_PaymentVoucher_Number UNIQUE (VoucherNumber),
        CONSTRAINT FK_PaymentVoucher_Vendor FOREIGN KEY (VendorId) REFERENCES ap.Vendor (VendorId),
        CONSTRAINT FK_PaymentVoucher_Journal FOREIGN KEY (JournalId) REFERENCES gl.JournalHeader (JournalId),
        CONSTRAINT CK_PaymentVoucher_Amount CHECK (Amount > 0)
    );
END
GO

IF OBJECT_ID(N'ap.PaymentAllocation', N'U') IS NULL
BEGIN
    CREATE TABLE ap.PaymentAllocation
    (
        PaymentAllocationId BIGINT        IDENTITY(1,1) NOT NULL,
        PaymentVoucherId    BIGINT        NOT NULL,
        BillId              BIGINT        NOT NULL,
        AllocatedAmount     DECIMAL(19,4) NOT NULL,
        CreatedAt           DATETIME2(0)  NOT NULL CONSTRAINT DF_PaymentAlloc_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_PaymentAllocation PRIMARY KEY CLUSTERED (PaymentAllocationId),
        CONSTRAINT FK_PaymentAllocation_Voucher FOREIGN KEY (PaymentVoucherId) REFERENCES ap.PaymentVoucher (PaymentVoucherId),
        CONSTRAINT FK_PaymentAllocation_Bill FOREIGN KEY (BillId) REFERENCES ap.VendorBill (BillId),
        CONSTRAINT UQ_PaymentAllocation UNIQUE (PaymentVoucherId, BillId),
        CONSTRAINT CK_PaymentAllocation_Amount CHECK (AllocatedAmount > 0)
    );
END
GO

/* ---------------------------------------------------------------------------
   gl.DocumentSequence - backing store for gl.usp_NextDocumentNumber. Declared
   here so the subledger and GL share one numbering mechanism.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'gl.DocumentSequence', N'U') IS NULL
BEGIN
    CREATE TABLE gl.DocumentSequence
    (
        Prefix     NVARCHAR(10) NOT NULL,
        FiscalYear SMALLINT     NOT NULL,
        LastNumber INT          NOT NULL CONSTRAINT DF_DocumentSequence_LastNumber DEFAULT (0),
        CONSTRAINT PK_DocumentSequence PRIMARY KEY CLUSTERED (Prefix, FiscalYear)
    );
END
GO
