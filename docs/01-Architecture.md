# 1. Architecture

## 1.1 Overview

The system is a layered ASP.NET MVC 5 application backed by SQL Server Express.
Each layer depends only on the layer beneath it, and the domain layer depends on
nothing.

```
Presentation  ->  Services  ->  Data  ->  SQL Server Express
 (MVC/Views)     (business)    (EF6/ADO.NET)   (gl/ar/ap/sec)
```

| Layer | Project | Responsibility |
|-------|---------|----------------|
| Domain | `AccountingSystem.Domain` | Entities, enums, service interfaces. No dependencies. |
| Data | `AccountingSystem.Data` | EF6 `DbContext`, connection factory, read-only reporting repository. |
| Services | `AccountingSystem.Services` | Posting engine, reporting facade, RBAC, audit, numbering. |
| Presentation | `AccountingSystem.Web` | MVC controllers, Razor views, jQuery/AJAX, DataTables, Chart.js. |

## 1.2 Domain model (ERD)

```
gl.Account (self-referencing tree)
   AccountId PK
   AccountCode (unique)
   AccountName
   AccountType        1 Asset | 2 Liability | 3 Equity | 4 Revenue | 5 Expense
   NormalBalance      1 Debit | 2 Credit
   ParentAccountId FK -> gl.Account
   IsPostable, IsActive, IsDeleted

gl.FiscalPeriod
   FiscalPeriodId PK, FiscalYear, PeriodNumber, StartDate, EndDate, IsClosed

gl.JournalHeader 1 ------< gl.JournalDetail >------ gl.Account
   JournalId PK              JournalDetailId PK         AccountId FK
   VoucherNumber (unique)    JournalId FK
   TransactionDate           LineNumber
   Status 0 Draft            Debit  DECIMAL(19,4)
          1 Approved         Credit DECIMAL(19,4)
          2 Posted           Description
          3 Void
   TotalDebit, TotalCredit

ar.Customer 1 ----< ar.Invoice 1 ----< ar.InvoiceLine
                       |
                       +---- JournalId FK -> gl.JournalHeader

ar.Receipt 1 ----< ar.ReceiptAllocation >---- ar.Invoice

ap.Vendor 1 ----< ap.PurchaseOrder
          1 ----< ap.VendorBill 1 ----< ap.VendorBillLine
                       |
                       +---- JournalId FK -> gl.JournalHeader

ap.PaymentVoucher 1 ----< ap.PaymentAllocation >---- ap.VendorBill

sec.AppRole 1 ----< sec.AppUser
sec.AuditLog  (append-only, no FKs by design)
```

Relationships in words:

- One `JournalHeader` has two or more `JournalDetail` lines; each line points at
  exactly one `Account`. The sum of `Debit` must equal the sum of `Credit`.
- An `Invoice`, `VendorBill`, `Receipt` or `PaymentVoucher` records the
  `JournalId` of the GL entry that recognised it, keeping the subledger
  reconcilable to the ledger.
- A `Receipt` is allocated across one or more `Invoice` rows through
  `ReceiptAllocation`, so partial payments are supported. `PaymentVoucher` works
  the same way against `VendorBill`.

## 1.3 The double-entry engine

`JournalService` is the authoritative gate for the accounting rules.

```
CreateDraft   validate structure -> save header + lines as Draft
Approve       Draft -> Approved, requires balance and segregation of duties
PostJournalEntry
    open SERIALIZABLE transaction
    reload header with UPDLOCK
    validate: not already posted/voided, period open, >= 2 lines,
              debits == credits, accounts active and postable
    call gl.usp_PostJournalEntry  (re-validates, sets Status = Posted)
    write audit row
    commit                (any error -> full rollback)
VoidJournalEntry
    open transaction
    call gl.usp_VoidJournalEntry  (mark Void, create reversing entry)
    write audit rows
    commit
```

The same balance rule is enforced at three levels:

1. **C#** - `JournalService.ValidateBalanced` throws before any write.
2. **Stored procedure** - `gl.usp_PostJournalEntry` raises and rolls back.
3. **Triggers** - `gl.trg_JournalDetail_Balance` recomputes header totals and
   rejects an unbalanced Approved or Posted entry; `gl.trg_JournalDetail_Immutable`
   and `gl.trg_JournalHeader_Immutable` block edits to posted data.

This defence in depth means a bug in one layer, or a direct SQL client, cannot
break the ledger.

## 1.4 Request flow - posting a journal entry

```
Browser (Create.cshtml + journal-entry.js)
   |  POST /Journal/Create  (form, anti-forgery token)
   v
JournalController.Create
   |  server-side balance + account validation
   v
JournalService.CreateDraft  ->  AccountingDbContext  ->  gl.JournalHeader/Detail
   |
   |  POST /Journal/Post  (AJAX)
   v
JournalController.Post
   |  [RequirePermission(PostJournal)]
   v
JournalService.PostJournalEntry
   |  SERIALIZABLE transaction
   |  gl.usp_PostJournalEntry
   v
Posted ledger + sec.AuditLog row
```

## 1.5 Reporting

Reporting is read-only and runs the aggregation inside SQL Server Express.

| Report | Procedure | Notes |
|--------|-----------|-------|
| Trial Balance | `gl.usp_GetTrialBalance` | Debit/credit totals and natural-side net per account. |
| Income Statement | `gl.usp_GetIncomeStatement` | Revenue and expense detail plus net income. |
| Balance Sheet | `gl.usp_GetBalanceSheet` | Asset/liability/equity balances plus current earnings. |
| GL Detail | `gl.usp_GetGeneralLedgerDetail` | Opening balance and movements with a running balance. |

`ReportingRepository` maps result sets to DTOs; `ReportingService` adds
authorisation and date-range validation; the controller shapes view models and
the Razor views render them (with optional Chart.js visualisation).

## 1.6 Cross-cutting concerns

- **Configuration** - `DbConnectionFactory` reads the single named connection
  string from `web.config`. Nothing is hard-coded.
- **Concurrency** - posting uses a `SERIALIZABLE` transaction and `UPDLOCK`;
  master records carry a `ROWVERSION` for optimistic concurrency.
- **Numbering** - `gl.usp_NextDocumentNumber` allocates sequential document
  numbers under `UPDLOCK/HOLDLOCK`, so concurrent callers cannot collide.
- **Audit** - `AuditService` writes before/after JSON images in the same
  transaction as the change it describes; triggers capture direct SQL access.
- **Authorisation** - `AuthorizationService` holds the role/permission matrix;
  `[RequirePermission]` enforces it at the controller boundary.
