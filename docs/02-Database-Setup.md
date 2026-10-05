# 2. Database setup (SQL Server Express)

This guide initialises the `AccountingSystemDB` database on a SQL Server Express
instance, applies the schema, seeds the Chart of Accounts, and sets the
permissions the web application needs.

## 2.1 Prerequisites

- SQL Server Express installed, typically as the named instance `.\SQLEXPRESS`.
  (LocalDB, `(localdb)\MSSQLLocalDB`, also works - substitute the server name.)
- SQL Server Management Studio (SSMS) or Visual Studio with SQL Server Object
  Explorer.
- A login with the `dbcreator` or `sysadmin` role to create the database
  (for example the Windows administrator who installed SQL Server, or `sa`).

Verify the instance is running:

```text
Windows:  Services (services.msc) -> "SQL Server (SQLEXPRESS)" should be Running.
T-SQL:    SELECT @@VERSION, @@SERVERNAME;
```

## 2.2 Attach or initialise the instance

SQL Express creates a default instance on install. If you need a fresh or
additional instance:

1. Open **SQL Server Configuration Manager**.
2. Under **SQL Server Services**, confirm the `SQL Server (SQLEXPRESS)` service
   is running and set to Automatic.
3. Under **SQL Server Network Configuration -> Protocols for SQLEXPRESS**,
   enable **TCP/IP** only if remote connections are required (not needed for a
   local IIS deployment).
4. Restart the service after any protocol change.

There is no database file to attach: `01_CreateDatabase.sql` creates
`AccountingSystemDB` from scratch.

## 2.3 Run the DDL scripts

Connect to `.\SQLEXPRESS` and run the scripts **in this order**. Each script is
idempotent and safe to re-run.

| Order | Script | Purpose |
|-------|--------|---------|
| 1 | `01_CreateDatabase.sql` | Creates `AccountingSystemDB` and the `gl`, `ar`, `ap`, `sec` schemas. |
| 2 | `02_CoreLedgerTables.sql` | Accounts, fiscal periods, journal header/detail, audit log, users and roles. |
| 3 | `03_SubledgerTables.sql` | Customers, invoices, receipts, vendors, POs, bills, payment vouchers. |
| 4 | `04_IndexesAndTriggers.sql` | Performance indexes. |
| 5 | `04b_IntegrityTriggers.sql` | Balance, immutability and audit triggers. |
| 6 | `05_SeedChartOfAccounts.sql` | Fiscal calendar, standard Chart of Accounts, RBAC seed. |
| 7 | `06_StoredProcedures.sql` | Posting, voiding, numbering and reporting procedures. |
| 8 | `07_SecurityAndPermissions.sql` | Application roles and least-privilege grants. |
| 9 | `08_SampleData.sql` | Optional smoke-test customer, vendor and opening entry. |

### Run with SSMS

1. **File -> Open -> File**, select a script.
2. In the toolbar, set the target database to `master` for script 1 and to
   `AccountingSystemDB` for the rest (each script also sets `USE` itself).
3. Press **F5** to execute.

### Run with Visual Studio

1. **View -> SQL Server Object Explorer**.
2. Expand the `.\SQLEXPRESS` node, right-click **Databases -> New Query**.
3. Paste each script in turn and click **Execute**.

### Run with sqlcmd

```bat
sqlcmd -S .\SQLEXPRESS -E -i database\01_CreateDatabase.sql
sqlcmd -S .\SQLEXPRESS -E -d AccountingSystemDB -i database\02_CoreLedgerTables.sql
sqlcmd -S .\SQLEXPRESS -E -d AccountingSystemDB -i database\03_SubledgerTables.sql
sqlcmd -S .\SQLEXPRESS -E -d AccountingSystemDB -i database\04_IndexesAndTriggers.sql
sqlcmd -S .\SQLEXPRESS -E -d AccountingSystemDB -i database\04b_IntegrityTriggers.sql
sqlcmd -S .\SQLEXPRESS -E -d AccountingSystemDB -i database\05_SeedChartOfAccounts.sql
sqlcmd -S .\SQLEXPRESS -E -d AccountingSystemDB -i database\06_StoredProcedures.sql
sqlcmd -S .\SQLEXPRESS -E -d AccountingSystemDB -i database\07_SecurityAndPermissions.sql
sqlcmd -S .\SQLEXPRESS -E -d AccountingSystemDB -i database\08_SampleData.sql
```

`-E` uses Windows authentication. For SQL authentication use
`-U AccountingUser -P <password>`.

## 2.4 Verify the deployment

```sql
USE AccountingSystemDB;

SELECT COUNT(*) AS AccountCount FROM gl.Account;              -- expect ~30
SELECT COUNT(*) AS PeriodCount  FROM gl.FiscalPeriod;         -- expect 12
SELECT name FROM sys.procedures WHERE schema_id = SCHEMA_ID('gl');

-- The opening entry from 08_SampleData.sql balances:
SELECT VoucherNumber, TotalDebit, TotalCredit, Status
  FROM gl.JournalHeader WHERE VoucherNumber = 'JV-OPENING';
```

## 2.5 Permissions for IIS and the worker process

The web application runs under an IIS application pool identity. Grant that
identity access to the database, not `sysadmin`.

### Option A - Integrated Security (recommended)

The application pool identity is `IIS APPPOOL\<AppPoolName>`. For the default
pool that is `IIS APPPOOL\DefaultAppPool`. `07_SecurityAndPermissions.sql`
creates the user and adds it to `AccountingAppRole`.

If you created a dedicated pool (recommended), substitute its name. For example,
for a pool called `AccountingPool`:

```sql
USE AccountingSystemDB;
CREATE USER [IIS APPPOOL\AccountingPool] FOR LOGIN [IIS APPPOOL\AccountingPool];
ALTER ROLE [AccountingAppRole] ADD MEMBER [IIS APPPOOL\AccountingPool];
```

> The login must exist at the server level first. Creating a database user for
> an application pool identity usually works because the identity is resolvable
> as a Windows principal; if the `CREATE USER` fails, create a server login
> first: `CREATE LOGIN [IIS APPPOOL\AccountingPool] FROM WINDOWS;`

When running from Visual Studio with IIS Express, the identity is **your**
Windows account, so grant that account the same role while developing:

```sql
CREATE USER [DOMAIN\YourUser] FOR LOGIN [DOMAIN\YourUser];
ALTER ROLE [AccountingAppRole] ADD MEMBER [DOMAIN\YourUser];
```

### Option B - SQL Server Authentication

`07_SecurityAndPermissions.sql` creates a dedicated login `AccountingUser` with
`CHECK_POLICY = ON` and adds it to `AccountingAppRole`. **Change the placeholder
password** before running the script in any real environment, and keep the real
value out of source control (see [Configuration](03-Configuration.md)).

### What the application role may do

| Granted | Denied |
|---------|--------|
| SELECT/INSERT/UPDATE on `gl`, `ar`, `ap` | DELETE on `gl`, `ar`, `ap` |
| SELECT on `sec`, INSERT on `sec.AuditLog` | UPDATE/DELETE on `sec.AuditLog` |
| EXECUTE on the posting and reporting procedures | ALTER/CONTROL/TAKE OWNERSHIP on schemas |

A read-only role, `AccountingReadOnlyRole`, exists for auditors and BI tools.

## 2.6 Troubleshooting

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| `Login failed for user 'IIS APPPOOL\...'` | App pool identity has no database user | Run the Option A grant for the correct pool name. |
| `Cannot open database "AccountingSystemDB"` | Scripts not run, or wrong server name | Confirm the server name in `web.config`; re-run script 1. |
| `The EXECUTE permission was denied` | Login not in `AccountingAppRole` | `ALTER ROLE [AccountingAppRole] ADD MEMBER [...]`. |
| `trg_JournalDetail_Balance` error on post | Entry is unbalanced | Correct the lines; the message states the difference. |
| `Login failed for user 'AccountingUser'` | Password policy/expiry | Reset the password; ensure `CHECK_POLICY` requirements are met. |
