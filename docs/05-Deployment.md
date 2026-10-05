# 5. Deployment

Target: Windows Server with IIS, SQL Server Express, ASP.NET MVC 5 on .NET
Framework 4.8.

## 5.1 Build and publish

1. Open `AccountingSystem.sln` in Visual Studio 2022.
2. **Build -> Restore NuGet Packages**, then **Build -> Rebuild Solution**.
3. Right-click **AccountingSystem.Web -> Publish**.
4. Choose **Folder** (then copy to the server) or **Web Deploy** (IIS).
5. Configuration: **Release**.

The publish output includes `bin/`, `Views/`, `Scripts/`, `Content/` and the
transformed `Web.config` (with `Web.Release.config` applied).

> NuGet packages (`EntityFramework`, `Microsoft.AspNet.Mvc`, `Newtonsoft.Json`,
> `Microsoft.AspNet.WebPages`, `Microsoft.AspNet.Razor`) are restored into a
> solution-level `packages/` folder. Ensure `nuget restore` runs in CI before
> `msbuild`.

## 5.2 IIS setup

1. **Install the role**: Web Server (IIS), with ASP.NET 4.8 and Windows
   Authentication features.
2. **Create an application pool**:
   - .NET CLR version: **.NET CLR v4.0.30319**.
   - Managed pipeline mode: **Integrated**.
   - Identity: a dedicated service account (recommended) or
     `ApplicationPoolIdentity` for the default pool.
3. **Create the site/application** pointing at the publish folder. Set the
   application pool to the one created above.
4. **Enable Windows Authentication**, disable Anonymous Authentication, if the
   site uses Integrated Security for both IIS and the database.
5. Grant the application pool identity **read** access to the publish folder
   (`icacls "<publishFolder>" /grant "IIS AppPool\<PoolName>":(OI)(CI)RX`).

### Application pool identity and the database

The database user must match the pool identity. See
[Database setup](02-Database-Setup.md#25-permissions-for-iis-and-the-worker-process).

- Default pool -> `IIS APPPOOL\DefaultAppPool`.
- Custom pool -> `IIS APPPOOL\<PoolName>`.
- `NT AUTHORITY\SYSTEM` works only if the pool runs as LocalSystem, which is
  discouraged; prefer a dedicated account.

## 5.3 Application settings per environment

Do not ship a developer connection string. For each environment:

- Set `Data Source` and `Initial Catalog` for that environment.
- Prefer Integrated Security. If SQL authentication is required, encrypt the
  `<connectionStrings>` section with `aspnet_regiis -pe` after deployment, or
  inject the value from a secret store.
- Confirm `Accounting:AllowPostingToClosedPeriod` is `false`.

## 5.4 Post-deployment verification

1. Browse to `https://<host>/Home/Health`. A healthy deployment returns
   `{"healthy":true,"detail":"ready"}`.
2. Sign in and open **Journal Entries**; the DataTables grid should load.
3. Create a test journal entry that does not balance; it must be rejected with a
   clear message before saving.
4. Create a balanced entry, approve and post it, then confirm it appears as
   **Posted** and that a row was written to `sec.AuditLog`.
5. Open **Reports -> Trial Balance** and confirm it balances.

## 5.5 Health check

`HomeController.Health` calls `DbInitializer.EnsureDatabaseReady`, which verifies
connectivity and that the Chart of Accounts is seeded. It returns a terse JSON
payload with no connection details, safe for a monitoring probe. Point your
uptime monitor at `/Home/Health` and alert when `healthy` is `false`.

## 5.6 Backups and maintenance

SQL Server Express has **no SQL Server Agent**, so schedule backups externally
(Windows Task Scheduler running `sqlcmd`, or a backup tool).

```bat
:: Full backup
sqlcmd -S .\SQLEXPRESS -E -Q "BACKUP DATABASE [AccountingSystemDB] TO DISK = N'D:\Backups\AccountingSystemDB_Full.bak' WITH INIT, COMPRESSION, CHECKSUM"

:: Differential backup
sqlcmd -S .\SQLEXPRESS -E -Q "BACKUP DATABASE [AccountingSystemDB] TO DISK = N'D:\Backups\AccountingSystemDB_Diff.bak' WITH DIFFERENTIAL, INIT, COMPRESSION, CHECKSUM"
```

The database is created with `RECOVERY SIMPLE`, which suits Express. If you need
point-in-time recovery, switch to `RECOVERY FULL` and back up the log regularly.

Test a restore into a scratch database at least once per quarter.

## 5.7 Upgrades

1. Take a full backup.
2. Apply new DDL scripts in order (they are idempotent).
3. Publish the new application version.
4. Re-encrypt the connection string if you changed it.
5. Run the post-deployment verification in 5.4.

Because posted records are immutable and the audit trail is append-only, schema
upgrades must add columns rather than rewrite history. Never write a migration
that mutates or deletes posted journal data.

## 5.8 Scaling notes

SQL Server Express has a 10 GB database size limit and uses one socket or four
cores. For a larger deployment, point the same connection string at a full SQL
Server instance; no application change is required. Move the audit trail to a
separate filegroup or archive old periods to keep the operational database
within the Express limit.
