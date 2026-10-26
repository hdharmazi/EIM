-------------------------------------------------------------
-- 002a Create the MODIFIEDDATETIME trigger when it is missing
--
-- Inserted between 002 and 003 on purpose. This is the one exception to the
-- next-number rule; see docs/deployment/database-migrations.md.
--
-- 003 disables and re-enables the MODIFIEDDATETIME trigger on MESSAGEQUEUETABLE.
-- 001 no longer creates that trigger (it moved to the repeatable script
-- Repeatable/R__TR_MESSAGEQUEUETABLE_MODIFIEDDATETIME.sql), and repeatables run
-- after every versioned script. On a database created empty, such as the
-- production sqldb-eim-wtc-prd, 003 therefore failed: the trigger did not exist yet.
--
-- This creates the trigger only when it is missing, with the same body as the
-- repeatable script. On a database that already has it (eimdb) it does nothing,
-- and it is journaled like any other migration. The repeatable script stays the
-- owner of the definition: it runs after every versioned script and re-applies the
-- current body with CREATE OR ALTER.
--
-- DbUp orders scripts by embedded name, ordinally: Migrations.002_ sorts before
-- Migrations.002a_, which sorts before Migrations.003_.
-------------------------------------------------------------

IF OBJECT_ID(N'dbo.MODIFIEDDATETIME', N'TR') IS NULL
BEGIN
    -- CREATE TRIGGER must be the first statement in its batch, so it runs through EXEC.
    EXEC (N'
CREATE TRIGGER [dbo].[MODIFIEDDATETIME]
    ON [dbo].[MESSAGEQUEUETABLE]
    AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    -- The trigger updates its own table, so without this it re-enters once per row.
    IF TRIGGER_NESTLEVEL() > 1 RETURN;

    UPDATE q
    SET q.MODIFIEDDATETIME = SYSUTCDATETIME()
    FROM dbo.MESSAGEQUEUETABLE AS q
    INNER JOIN INSERTED AS i ON i.ID = q.ID;
END');
END
GO
