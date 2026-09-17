-------------------------------------------------------------
-- REPEATABLE: MODIFIEDDATETIME trigger on MESSAGEQUEUETABLE
-- Re-applied on every deployment so the deployed definition always
-- matches this file. Do not journal this script.
-------------------------------------------------------------
GO

CREATE OR ALTER TRIGGER [dbo].[MODIFIEDDATETIME]
    ON [dbo].[MESSAGEQUEUETABLE]
    AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    -- The trigger updates its own table, so without this it re-enters once per row.
    IF TRIGGER_NESTLEVEL() > 1 RETURN;

    -- Joined to INSERTED rather than capturing a single id. The previous version did
    -- SELECT @ID = INSERTED.ID, which picks one arbitrary row, so a multi-row UPDATE
    -- stamped one row and silently left the rest with a stale timestamp. The sweeps
    -- claim batches in a single UPDATE and filter on this column.
    UPDATE q
    SET q.MODIFIEDDATETIME = SYSUTCDATETIME()
    FROM dbo.MESSAGEQUEUETABLE AS q
    INNER JOIN INSERTED AS i ON i.ID = q.ID;
END
GO

-- UTC, not SYSDATETIME. This trigger overwrites whatever the application wrote, so
-- normalising the application alone would have had no effect at all.
