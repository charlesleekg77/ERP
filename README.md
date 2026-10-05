# Accounting System - Production Blueprint

A web-based, double-entry accounting system for ASP.NET MVC 5 (C#), Microsoft
SQL Server Express Edition, and IIS. It implements a General Ledger with a
double-entry posting engine, Accounts Receivable, Accounts Payable, financial
reporting, role-based access control, and an append-only audit trail.

This repository is a **blueprint**: complete, production-oriented source,
database scripts, and operational documentation. It targets .NET Framework 4.8
and SQL Server Express (instance `.\SQLEXPRESS`), and also runs on LocalDB.

---

## Contents

| Area | Location |
|------|----------|
| Database scripts (DDL, triggers, procedures, seed) | [`database/`](database/) |
| Domain layer (entities, enums, interfaces) | [`src/AccountingSystem.Domain/`](src/AccountingSystem.Domain/) |
| Data access layer (DbContext, connection factory, reporting repository) | [`src/AccountingSystem.Data/`](src/AccountingSystem.Data/) |
| Service / business logic layer (posting engine, reporting, RBAC, audit) | [`src/AccountingSystem.Services/`](src/AccountingSystem.Services/) |
| Web presentation layer (MVC controllers, Razor views, JS) | [`src/AccountingSystem.Web/`](src/AccountingSystem.Web/) |
| Documentation | [`docs/`](docs/) |

## Documentation index

1. [Architecture](docs/01-Architecture.md) - layers, components, request flow, ERD.
2. [Database setup](docs/02-Database-Setup.md) - attach/initialize SQL Express, run scripts, permissions, SSMS.
3. [Configuration](docs/03-Configuration.md) - `web.config` connection strings (Windows and SQL auth), EF6, security headers.
4. [Security and compliance](docs/04-Security-and-Compliance.md) - RBAC, double-entry controls, audit trail, immutability.
5. [Deployment](docs/05-Deployment.md) - IIS setup, application pool identity, publishing, health checks.

## Architecture at a glance

```
                 +-------------------------------------------+
                 |  Presentation (ASP.NET MVC 5 + Razor)     |
                 |  Controllers, Views, jQuery, DataTables   |
                 +--------------------+----------------------+
                                      |  (calls)
                 +--------------------v----------------------+
                 |  Service / Business Logic                 |
                 |  JournalService, ReportingService,        |
                 |  AccountService, AuthorizationService,    |
                 |  AuditService                             |
                 +--------------------+----------------------+
                                      |  (uses)
                 +--------------------v----------------------+
                 |  Data Access                              |
                 |  AccountingDbContext (EF6),               |
                 |  DbConnectionFactory, ReportingRepository |
                 +--------------------+----------------------+
                                      |  (SQL)
                 +--------------------v----------------------+
                 |  Microsoft SQL Server Express             |
                 |  gl / ar / ap / sec schemas, triggers,    |
                 |  stored procedures                        |
                 +-------------------------------------------+
```

## Quick start

```text
1. Provision the database
   - Open SQL Server Management Studio (or Visual Studio SQL Server Object Explorer).
   - Connect to .\SQLEXPRESS.
   - Run, in order:
       database/01_CreateDatabase.sql
       database/02_CoreLedgerTables.sql
       database/03_SubledgerTables.sql
       database/04_IndexesAndTriggers.sql
       database/04b_IntegrityTriggers.sql
       database/05_SeedChartOfAccounts.sql
       database/06_StoredProcedures.sql
       database/07_SecurityAndPermissions.sql
       database/08_SampleData.sql        -- optional smoke-test data

2. Configure the connection
   - Edit src/AccountingSystem.Web/Web.config.
   - Choose Option 1 (Integrated Security) or Option 2 (SQL authentication).

3. Build and run
   - Open AccountingSystem.sln in Visual Studio.
   - Restore NuGet packages.
   - Set AccountingSystem.Web as the startup project and press F5.
   - Browse to /Home/Health to confirm database readiness.
```

## Core design rules

- **Double entry is enforced three times.** In C# (`JournalService`), in the
  stored procedure (`gl.usp_PostJournalEntry`), and by database triggers. No
  path can persist an unbalanced entry.
- **Posted records are immutable.** Status only moves Draft -> Approved ->
  Posted, and Void is reached by generating a reversing entry. The original
  remains for the audit trail.
- **Money is `DECIMAL(19,4)`.** Never floating point.
- **The audit trail is append-only.** `sec.AuditLog` rejects UPDATE and DELETE.
- **Least privilege.** The application database login can read and write
  operational data and execute the posting/reporting procedures, but cannot
  drop objects or delete financial history.

## License / status

Blueprint and reference implementation. Review the security and deployment
documentation before using it with real financial data, and have a qualified
accountant confirm the Chart of Accounts and tax handling for your jurisdiction.
