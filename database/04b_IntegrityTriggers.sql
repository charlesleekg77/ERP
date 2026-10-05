/* ============================================================================
   04b_IntegrityTriggers.sql
   ----------------------------------------------------------------------------
   Accounting-invariant triggers. These are the last line of defence: even a
   direct T-SQL INSERT cannot persist a broken ledger.

   Run against [AccountingSystemDB] AFTER 04_IndexesAndTriggers.sql.
   ============================================================================ */

USE [AccountingSystemDB];
GO

/* ---------------------------------------------------------------------------
   trg_JournalDetail_Balance
   After any change to journal lines, recompute the header totals, then reject
   the change (rolling back) if an Approved (1) or Posted (2) header no longer
   balances. Draft headers may be temporarily unbalanced while being typed.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'gl.trg_JournalDetail_Balance', N'TR') IS NOT NULL
    DROP TRIGGER gl.trg_JournalDetail_Balance;
GO
CREATE TRIGGER gl.trg_JournalDetail_Balance
ON gl.JournalDetail
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Affected TABLE (JournalId BIGINT PRIMARY KEY);
    INSERT INTO @Affected (JournalId)
    SELECT JournalId FROM inserted
    UNION
    SELECT JournalId FROM deleted;

    UPDATE h
       SET h.TotalDebit  = ISNULL(t.TotalDebit, 0),
           h.TotalCredit = ISNULL(t.TotalCredit, 0)
      FROM gl.JournalHeader h
      JOIN @Affected a ON a.JournalId = h.JournalId
      OUTER APPLY
      (
          SELECT SUM(d.Debit) AS TotalDebit, SUM(d.Credit) AS TotalCredit
            FROM gl.JournalDetail d
           WHERE d.JournalId = h.JournalId
      ) t;

    IF EXISTS
    (
        SELECT 1
          FROM gl.JournalHeader h
          JOIN @Affected a ON a.JournalId = h.JournalId
         WHERE h.Status IN (1, 2)                 -- Approved or Posted
           AND h.TotalDebit <> h.TotalCredit
    )
    BEGIN
        RAISERROR (N'Journal entry is not balanced: total debits must equal total credits.', 16, 1);
        ROLLBACK TRANSACTION;
        RETURN;
    END
END
GO

/* ---------------------------------------------------------------------------
   trg_JournalHeader_Immutable
   Posted (2) entries are immutable apart from the transition to Void (3).
   Posted/Void headers cannot be hard-deleted.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'gl.trg_JournalHeader_Immutable', N'TR') IS NOT NULL
    DROP TRIGGER gl.trg_JournalHeader_Immutable;
GO
CREATE TRIGGER gl.trg_JournalHeader_Immutable
ON gl.JournalHeader
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM inserted)   -- pure DELETE
       AND EXISTS (SELECT 1 FROM deleted WHERE Status IN (2, 3))
    BEGIN
        RAISERROR (N'Posted or voided journal entries cannot be deleted. Use a reversing entry.', 16, 1);
        ROLLBACK TRANSACTION;
        RETURN;
    END

    IF EXISTS
    (
        SELECT 1
          FROM inserted i
          JOIN deleted  d ON d.JournalId = i.JournalId
         WHERE d.Status = 2
           AND (
                   i.TotalDebit  <> d.TotalDebit
                OR i.TotalCredit <> d.TotalCredit
                OR i.TransactionDate <> d.TransactionDate
                OR i.VoucherNumber   <> d.VoucherNumber
                OR i.Status NOT IN (2, 3)
               )
    )
    BEGIN
        RAISERROR (N'A posted journal entry is immutable. Only a status change to Void is allowed.', 16, 1);
        ROLLBACK TRANSACTION;
        RETURN;
    END

    IF EXISTS
    (
        SELECT 1 FROM inserted i
          JOIN deleted  d ON d.JournalId = i.JournalId
         WHERE d.Status = 3 AND i.Status <> 3
    )
    BEGIN
        RAISERROR (N'A voided journal entry cannot be re-opened.', 16, 1);
        ROLLBACK TRANSACTION;
        RETURN;
    END
END
GO

/* ---------------------------------------------------------------------------
   trg_JournalDetail_Immutable - block line edits on posted/voided headers.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'gl.trg_JournalDetail_Immutable', N'TR') IS NOT NULL
    DROP TRIGGER gl.trg_JournalDetail_Immutable;
GO
CREATE TRIGGER gl.trg_JournalDetail_Immutable
ON gl.JournalDetail
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
        SELECT 1
          FROM (SELECT JournalId FROM inserted UNION ALL SELECT JournalId FROM deleted) x
          JOIN gl.JournalHeader h ON h.JournalId = x.JournalId
         WHERE h.Status IN (2, 3)
    )
    BEGIN
        RAISERROR (N'Journal lines of a posted or voided entry cannot be modified.', 16, 1);
        ROLLBACK TRANSACTION;
        RETURN;
    END
END
GO

/* ---------------------------------------------------------------------------
   trg_AuditLog_Immutable - append-only audit trail. A maintenance session may
   bypass by setting CONTEXT_INFO to 'AUDIT-MAINTENANCE...' (used by archival
   jobs only).
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'sec.trg_AuditLog_Immutable', N'TR') IS NOT NULL
    DROP TRIGGER sec.trg_AuditLog_Immutable;
GO
CREATE TRIGGER sec.trg_AuditLog_Immutable
ON sec.AuditLog
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @ctx VARBINARY(128) = CONTEXT_INFO();
    IF @ctx IS NULL OR CAST(@ctx AS VARCHAR(128)) NOT LIKE 'AUDIT-MAINTENANCE%'
    BEGIN
        RAISERROR (N'The audit log is append-only and cannot be modified or deleted.', 16, 1);
        ROLLBACK TRANSACTION;
        RETURN;
    END
END
GO

/* ---------------------------------------------------------------------------
   trg_JournalHeader_Audit - capture header mutations directly at the table.
   Complements the service-layer AuditService for direct SQL access.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'gl.trg_JournalHeader_Audit', N'TR') IS NOT NULL
    DROP TRIGGER gl.trg_JournalHeader_Audit;
GO
CREATE TRIGGER gl.trg_JournalHeader_Audit
ON gl.JournalHeader
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO sec.AuditLog (TableSchema, TableName, RecordId, [Action], OldValues, NewValues, UserId)
    SELECT N'gl', N'JournalHeader', CAST(COALESCE(i.JournalId, d.JournalId) AS NVARCHAR(64)),
           CASE WHEN d.JournalId IS NULL THEN N'INSERT'
                WHEN i.JournalId IS NULL THEN N'DELETE'
                ELSE N'UPDATE' END,
           CASE WHEN d.JournalId IS NULL THEN NULL ELSE
                (SELECT d.JournalId, d.VoucherNumber, d.TransactionDate, d.Status, d.TotalDebit, d.TotalCredit
                   FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END,
           CASE WHEN i.JournalId IS NULL THEN NULL ELSE
                (SELECT i.JournalId, i.VoucherNumber, i.TransactionDate, i.Status, i.TotalDebit, i.TotalCredit
                   FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END,
           SUSER_SNAME()
      FROM inserted i
      FULL OUTER JOIN deleted d ON d.JournalId = i.JournalId;
END
GO
