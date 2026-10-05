/* ============================================================================
   07_SecurityAndPermissions.sql
   ----------------------------------------------------------------------------
   Creates the least-privilege database principals used by the web application
   and grants exactly the rights the application needs. This is the "principle
   of least privilege" applied to SQL Server Express.

   Two access models are supported (see web.config):
     A. Integrated Security  -> the IIS application pool identity is mapped to
        a database user. Run the IIS-identity section below.
     B. SQL Server Auth      -> a SQL login (AccountingUser) with a strong
        password. Run the SQL-login section below.

   Replace the placeholder password before executing in any real environment.
   Run against [AccountingSystemDB] as a sysadmin/db_owner.
   ============================================================================ */

USE [AccountingSystemDB];
GO

/* ===========================================================================
   MODEL A - Windows / Integrated Security
   The web app runs under an IIS application pool identity. Create a database
   user for that identity and grant the application role.
   =========================================================================== */

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'IIS APPPOOL\DefaultAppPool')
    CREATE USER [IIS APPPOOL\DefaultAppPool] FOR LOGIN [IIS APPPOOL\DefaultAppPool];
GO

/* ===========================================================================
   MODEL B - SQL Server Authentication
   Dedicated, non-sysadmin login. Change the password; store the real value in
   a secret store or an encrypted config section, never in source control.
   =========================================================================== */

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'AccountingUser')
    CREATE LOGIN [AccountingUser]
        WITH PASSWORD = N'Change_This_Strong_P@ssw0rd!',
             CHECK_POLICY = ON,
             CHECK_EXPIRATION = OFF,
             DEFAULT_DATABASE = [AccountingSystemDB];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'AccountingUser')
    CREATE USER [AccountingUser] FOR LOGIN [AccountingUser];
GO

/* ===========================================================================
   Application role with least privilege.
   The app may read and write operational tables and execute the procs, but it
   may NOT drop objects, alter schema, or delete the audit trail.
   =========================================================================== */

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'AccountingAppRole')
    CREATE ROLE [AccountingAppRole];
GO

/* Read/write on operational schemas (not sec.AuditLog - insert only). */
GRANT SELECT, INSERT, UPDATE ON SCHEMA::gl  TO [AccountingAppRole];
GRANT SELECT, INSERT, UPDATE ON SCHEMA::ar  TO [AccountingAppRole];
GRANT SELECT, INSERT, UPDATE ON SCHEMA::ap  TO [AccountingAppRole];
GRANT SELECT                 ON SCHEMA::sec TO [AccountingAppRole];
GRANT INSERT                 ON sec.AuditLog TO [AccountingAppRole];

/* EXECUTE on the posting/reporting procedures only. */
GRANT EXECUTE ON OBJECT::gl.usp_PostJournalEntry      TO [AccountingAppRole];
GRANT EXECUTE ON OBJECT::gl.usp_VoidJournalEntry      TO [AccountingAppRole];
GRANT EXECUTE ON OBJECT::gl.usp_NextDocumentNumber    TO [AccountingAppRole];
GRANT EXECUTE ON OBJECT::gl.usp_GetTrialBalance       TO [AccountingAppRole];
GRANT EXECUTE ON OBJECT::gl.usp_GetIncomeStatement    TO [AccountingAppRole];
GRANT EXECUTE ON OBJECT::gl.usp_GetBalanceSheet       TO [AccountingAppRole];
GRANT EXECUTE ON OBJECT::gl.usp_GetGeneralLedgerDetail TO [AccountingAppRole];

/* Explicitly deny destructive rights on financial history. */
DENY DELETE ON SCHEMA::gl TO [AccountingAppRole];
DENY DELETE ON SCHEMA::ar TO [AccountingAppRole];
DENY DELETE ON SCHEMA::ap TO [AccountingAppRole];
DENY ALTER, CONTROL, TAKE OWNERSHIP ON SCHEMA::gl TO [AccountingAppRole];
GO

/* Add the application principals to the role. */
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'AccountingUser')
    ALTER ROLE [AccountingAppRole] ADD MEMBER [AccountingUser];
GO
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'IIS APPPOOL\DefaultAppPool')
    ALTER ROLE [AccountingAppRole] ADD MEMBER [IIS APPPOOL\DefaultAppPool];
GO

/* ===========================================================================
   Reporting / read-only role for auditors and BI tools.
   =========================================================================== */
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'AccountingReadOnlyRole')
    CREATE ROLE [AccountingReadOnlyRole];
GO
GRANT SELECT ON SCHEMA::gl  TO [AccountingReadOnlyRole];
GRANT SELECT ON SCHEMA::ar  TO [AccountingReadOnlyRole];
GRANT SELECT ON SCHEMA::ap  TO [AccountingReadOnlyRole];
GRANT SELECT ON SCHEMA::sec TO [AccountingReadOnlyRole];
GRANT EXECUTE ON OBJECT::gl.usp_GetTrialBalance        TO [AccountingReadOnlyRole];
GRANT EXECUTE ON OBJECT::gl.usp_GetIncomeStatement     TO [AccountingReadOnlyRole];
GRANT EXECUTE ON OBJECT::gl.usp_GetBalanceSheet        TO [AccountingReadOnlyRole];
GRANT EXECUTE ON OBJECT::gl.usp_GetGeneralLedgerDetail TO [AccountingReadOnlyRole];
GO
