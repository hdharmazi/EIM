-------------------------------------------------------------
-- 008 Give duplicate detection its own key
--
-- Dedupe was keyed on SOURCEID, which holds the *business record* key - the
-- customer account, contact id, address id. Every change to a record carries the
-- same SOURCEID, so an update to a customer already processed was rejected as a
-- duplicate of its own create. Deployed, each record would have been processed
-- exactly once and every later change silently discarded.
--
-- DEDUPEKEY identifies the *delivery* instead: the D365 operation id, or the
-- transport message id. Redelivery of the same operation repeats it; a genuine
-- second change does not.
--
-- Nullable, and a null key means no duplicate check. Historical rows have no key
-- and must not be treated as duplicates of each other.
-------------------------------------------------------------

IF COL_LENGTH('dbo.MESSAGEQUEUETABLE', 'DEDUPEKEY') IS NULL
BEGIN
    ALTER TABLE [dbo].[MESSAGEQUEUETABLE] ADD [DEDUPEKEY] NVARCHAR(200) NULL;
END
GO

-- Filtered: only rows that carry a key are ever probed, and historical rows are
-- the overwhelming majority.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_MESSAGEQUEUETABLE_DEDUPEKEY'
                 AND object_id = OBJECT_ID('dbo.MESSAGEQUEUETABLE'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_MESSAGEQUEUETABLE_DEDUPEKEY]
        ON [dbo].[MESSAGEQUEUETABLE] ([SOURCESYSTEM] ASC, [ENVIRONMENT] ASC, [DEDUPEKEY] ASC)
        INCLUDE ([STATUS])
        WHERE [DEDUPEKEY] IS NOT NULL;
END
GO
