# 4. Security and compliance

This document describes the controls that make the system suitable for financial
record keeping, and the responsibilities that remain with the operator.

## 4.1 Role-Based Access Control (RBAC)

Roles and permissions are defined in
`AccountingSystem.Services/Security/AuthorizationService.cs`. That file is the
single source of truth for the matrix below.

| Permission | Administrator | Accountant | Approver | Clerk | Auditor |
|------------|:---:|:---:|:---:|:---:|:---:|
| ViewLedger | yes | yes | yes | yes | yes |
| CreateDraftJournal | yes | yes | - | yes | - |
| ApproveJournal | yes | - | yes | - | - |
| PostJournal | yes | yes | - | - | - |
| VoidJournal | yes | - | - | - | - |
| ManageMasterData | yes | yes | - | - | - |
| ManageUsers | yes | - | - | - | - |
| ClosePeriod | yes | yes | - | - | - |
| ViewAuditTrail | yes | yes | - | - | yes |

Key separations of duties:

- **Approver cannot post.** Approval and posting are distinct permissions.
- **Clerk cannot post or approve.** A clerk prepares drafts only.
- **Auditor is read-only** but can view the audit trail.

Enforcement points:

1. `[RequirePermission(Permission.X)]` on controller actions returns HTTP 403
   when the current principal's roles lack the permission.
2. `AuthorizationService.Demand` inside `ReportingService` re-checks ledger
   access, so a UI bypass cannot read reports.
3. The database login itself is least-privilege (see 4.5).

### Mapping Windows groups to application roles

Under Windows authentication the principal's roles are Active Directory groups,
which are rarely named `Administrator` or `Accountant`. Map your real groups to
application roles in `Web.config`; `RoleResolver` reads this mapping and is the
single place both the permission filter and the controllers consult.

```xml
<add key="Accounting:RoleMap:Administrator" value="CONTOSO\Accounting-Admins" />
<add key="Accounting:RoleMap:Accountant"    value="CONTOSO\Accounting-Staff;CONTOSO\Finance" />
<add key="Accounting:RoleMap:Approver"      value="CONTOSO\Finance-Controllers" />
<add key="Accounting:RoleMap:Clerk"         value="CONTOSO\AP-Clerks" />
<add key="Accounting:RoleMap:Auditor"       value="CONTOSO\Internal-Audit" />
```

Multiple groups are separated by `;` or `,`. **If a role is left empty, no group
is mapped to it**, so an unmapped authenticated user has no permissions and is
denied. For local development you can set
`Accounting:DefaultRoleForAuthenticatedUsers` to, for example, `Administrator`;
leave it empty in production.

If you use a custom role provider (forms or claims authentication), the
principal's own role names are honoured directly and the mapping can stay empty.

## 4.2 Double-entry controls

The fundamental accounting rule - total debits equal total credits - is enforced
at three independent layers:

| Layer | Mechanism | File |
|-------|-----------|------|
| Application | `JournalService.ValidateBalanced` | `Services/Accounting/JournalService.cs` |
| Database procedure | `gl.usp_PostJournalEntry` raises and rolls back | `database/06_StoredProcedures.sql` |
| Database trigger | `gl.trg_JournalDetail_Balance` recomputes and rejects | `database/04b_IntegrityTriggers.sql` |

Additional structural rules:

- A journal entry needs at least two lines.
- Each line carries a debit **or** a credit, never both and never neither
  (`CK_JournalDetail_Amounts`, re-checked in C#).
- Only active, postable (leaf) accounts may be used.
- Posting into a closed fiscal period is rejected.

Because the rules live in the database as well as the application, a bug in the
service layer or a direct SQL client still cannot persist an unbalanced entry.

## 4.3 Audit trail

Every mutating operation writes to `sec.AuditLog`:

| Column | Content |
|--------|---------|
| `TableSchema`, `TableName` | The affected object. |
| `RecordId` | Primary key of the affected row. |
| `Action` | INSERT, UPDATE or DELETE. |
| `OldValues` | JSON snapshot before the change (null on insert). |
| `NewValues` | JSON snapshot after the change (null on delete). |
| `UserId`, `UserName` | The authenticated principal. |
| `IpAddress` | Client address, when available. |
| `OccurredAt` | UTC timestamp with millisecond precision. |

Two write paths cover both application and direct database access:

- `AuditService.Write` is called by the service layer and commits in the **same
  transaction** as the change it describes.
- Triggers (`gl.trg_JournalHeader_Audit`) capture changes made directly in SQL.

The trail is **append-only**. `sec.trg_AuditLog_Immutable` rejects UPDATE and
DELETE. Only a session that has explicitly set `CONTEXT_INFO` to the
maintenance token can bypass it, which is reserved for archival jobs.

## 4.4 Immutability of posted financial records

The lifecycle is `Draft -> Approved -> Posted`, and `Void` is terminal.

- `gl.trg_JournalHeader_Immutable` blocks any change to a posted entry except
  the transition to Void, and blocks hard deletion of posted/voided entries.
- `gl.trg_JournalDetail_Immutable` blocks edits to the lines of a posted entry.
- Voiding does not delete: `gl.usp_VoidJournalEntry` marks the original Void and
  generates a **reversing entry** so the ledger nets to zero while history is
  preserved.

Master data (accounts, customers, vendors) uses a soft-delete pattern
(`IsDeleted`); the application role has no DELETE grant on financial schemas.

## 4.5 Database least privilege

`database/07_SecurityAndPermissions.sql` defines `AccountingAppRole`:

| Granted | Denied |
|---------|--------|
| SELECT/INSERT/UPDATE on `gl`, `ar`, `ap` | DELETE on `gl`, `ar`, `ap` |
| SELECT on `sec`, INSERT on `sec.AuditLog` | UPDATE/DELETE on `sec.AuditLog` |
| EXECUTE on posting/reporting procedures | ALTER, CONTROL, TAKE OWNERSHIP on schemas |

A separate `AccountingReadOnlyRole` serves auditors and BI tools.

## 4.6 Transport, session and input protection

- **Transport** - serve over HTTPS. `Web.config` includes HSTS (added in
  `Global.asax.cs` when the connection is secure), `X-Content-Type-Options`,
  `X-Frame-Options`, `Referrer-Policy` and a Content Security Policy.
- **Cookies** - `httpOnlyCookies="true"`, `sameSite="Lax"`. Set `requireSSL="true"`
  in production.
- **CSRF** - every state-changing POST carries `@Html.AntiForgeryToken()` and is
  validated with `[ValidateAntiForgeryToken]`.
- **Input validation** - model binding plus `DataAnnotations` on the view models;
  the server re-validates every accounting rule regardless of client behaviour.
- **Error handling** - `HandleErrorAttribute` plus `<customErrors mode="On" />`
  ensure no stack trace or connection detail reaches the client; details go to
  the server trace.
- **Version disclosure** - `Server`, `X-AspNet-Version`, `X-AspNetMvc-Version`
  and `X-Powered-By` are removed.

## 4.7 Separation of duties and period control

- A journal entry is prepared (Clerk/Accountant), approved (Approver), and posted
  (Accountant/Administrator). `Accounting:RequireApprovalBeforePosting` in
  `Web.config` controls whether the approval step is mandatory.
- Closing a fiscal period (`gl.FiscalPeriod.IsClosed`) blocks further postings
  into it, at both the service layer and in `gl.usp_PostJournalEntry`.

## 4.8 What this blueprint does not do

Be explicit about the boundary:

- **No encryption at rest beyond SQL Server's own features.** Use Transparent
  Data Encryption or BitLocker if your policy requires it.
- **No external identity provider by default.** The sample uses Windows
  authentication and a local role table. For an enterprise deployment, federate
  to Active Directory or Entra ID and map group membership to the roles in
  `AuthorizationService`.
- **No tax engine.** Tax is a rate on invoice/bill lines. Confirm treatment for
  your jurisdiction with a qualified accountant.
- **No multi-currency.** The base currency is a single configured value.
- **Password hashing is stubbed** (`sec.AppUser.PasswordHash` is null under
  Windows auth). If you enable forms authentication, use PBKDF2 or BCrypt and
  never store a plaintext or reversible value.

## 4.9 Pre-production checklist

- [ ] `Web.config` `compilation debug="false"` and `customErrors mode="On"`.
- [ ] Cookies set to `requireSSL="true"`; site served over HTTPS only.
- [ ] Connection string uses Integrated Security, or the SQL password is stored
      in an encrypted section or secret store, not in source control.
- [ ] Application pool identity added to `AccountingAppRole`; no `sysadmin`.
- [ ] `Accounting:AllowPostingToClosedPeriod` is `false`.
- [ ] A qualified accountant has reviewed the Chart of Accounts and tax setup.
- [ ] Audit log retention and backup schedule agreed and tested.
- [ ] Database backups configured (SQL Express supports full and differential
      backups; it has no SQL Server Agent, so schedule them externally).
