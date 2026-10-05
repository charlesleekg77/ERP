/* ============================================================================
   01_CreateDatabase.sql
   ----------------------------------------------------------------------------
   Creates the AccountingSystemDB database and the logical schemas used by the
   application. Target engine: Microsoft SQL Server Express Edition
   (instance: .\SQLEXPRESS) or LocalDB ((localdb)\MSSQLLocalDB).

   Run this script first, connected to the [master] database, using a login
   that has the dbcreator or sysadmin role (e.g. sa, or the Windows
   administrator running SSMS "as administrator").

   Idempotent: safe to re-run.
   ============================================================================ */

SET NOCOUNT ON;
GO

/* ---------------------------------------------------------------------------
   Create the database. The file paths below are the SQL Server Express
   defaults. If your instance stores data elsewhere, adjust Data/Log paths or
   simply omit the ON PRIMARY clause and let the instance decide.
   --------------------------------------------------------------------------- */
IF DB_ID(N'AccountingSystemDB') IS NULL
BEGIN
    CREATE DATABASE [AccountingSystemDB];
END
GO

ALTER DATABASE [AccountingSystemDB] SET RECOVERY SIMPLE;      -- Express edition friendly
ALTER DATABASE [AccountingSystemDB] SET AUTO_CLOSE OFF;       -- keep plans/pages warm
ALTER DATABASE [AccountingSystemDB] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
GO

USE [AccountingSystemDB];
GO

/* ---------------------------------------------------------------------------
   Logical schemas separate concerns:
     gl  - General Ledger (chart of accounts, journals, fiscal periods)
     ar  - Accounts Receivable (customers, invoices, receipts)
     ap  - Accounts Payable  (vendors, bills, disbursements)
     sec - Security & audit (users, roles, audit trail)
   --------------------------------------------------------------------------- */
IF SCHEMA_ID(N'gl')  IS NULL EXEC(N'CREATE SCHEMA gl  AUTHORIZATION dbo;');
IF SCHEMA_ID(N'ar')  IS NULL EXEC(N'CREATE SCHEMA ar  AUTHORIZATION dbo;');
IF SCHEMA_ID(N'ap')  IS NULL EXEC(N'CREATE SCHEMA ap  AUTHORIZATION dbo;');
IF SCHEMA_ID(N'sec') IS NULL EXEC(N'CREATE SCHEMA sec AUTHORIZATION dbo;');
GO
