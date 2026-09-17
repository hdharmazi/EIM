-------------------------------------------------------------
-- 005 Extend MESSAGEQUEUETABLE for hold, release and retry
--
--   RETRYCOUNT / NEXTATTEMPTDATETIME  support status 7 (Retry Scheduled)
--   IX_..._STATUS                     supports the release and retry sweeps and
--                                     the dashboard's GROUP BY STATUS count,
--                                     which is a full scan today
--   IX_..._SOURCEID                   supports duplicate detection on ingest
--   DF_MESSAGEQUEUETABLE_CREATED      moves from server-local to UTC
-------------------------------------------------------------

IF COL_LENGTH('dbo.MESSAGEQUEUETABLE', 'RETRYCOUNT') IS NULL
BEGIN
    ALTER TABLE [dbo].[MESSAGEQUEUETABLE]
        ADD [RETRYCOUNT] INT NOT NULL CONSTRAINT [DF_MESSAGEQUEUETABLE_RETRYCOUNT] DEFAULT (0);
END
GO

IF COL_LENGTH('dbo.MESSAGEQUEUETABLE', 'NEXTATTEMPTDATETIME') IS NULL
BEGIN
    ALTER TABLE [dbo].[MESSAGEQUEUETABLE]
        ADD [NEXTATTEMPTDATETIME] DATETIME2(3) NULL;
END
GO

-- STATUS leads so the same index serves GROUP BY STATUS; the route columns and ID
-- follow so the release sweep can claim one route's held rows in ascending ID order.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_MESSAGEQUEUETABLE_STATUS'
                 AND object_id = OBJECT_ID('dbo.MESSAGEQUEUETABLE'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_MESSAGEQUEUETABLE_STATUS]
        ON [dbo].[MESSAGEQUEUETABLE] ([STATUS] ASC, [ENVIRONMENT] ASC, [SOURCESYSTEM] ASC, [SOURCEENTITY] ASC, [ID] ASC)
        INCLUDE ([NEXTATTEMPTDATETIME]);
END
GO

-- Scoped by source system and environment: the same source id from two systems,
-- or the same id replayed into a different environment, are not duplicates.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_MESSAGEQUEUETABLE_SOURCEID'
                 AND object_id = OBJECT_ID('dbo.MESSAGEQUEUETABLE'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_MESSAGEQUEUETABLE_SOURCEID]
        ON [dbo].[MESSAGEQUEUETABLE] ([SOURCESYSTEM] ASC, [ENVIRONMENT] ASC, [SOURCEID] ASC)
        INCLUDE ([STATUS]);
END
GO

-- Not unique: duplicates already exist historically, and an unprocessed row is
-- re-driven rather than rejected, so uniqueness would reject legitimate retries.

IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_MESSAGEQUEUETABLE_CREATED')
BEGIN
    ALTER TABLE [dbo].[MESSAGEQUEUETABLE] DROP CONSTRAINT [DF_MESSAGEQUEUETABLE_CREATED];
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_MESSAGEQUEUETABLE_CREATED')
BEGIN
    ALTER TABLE [dbo].[MESSAGEQUEUETABLE]
        ADD CONSTRAINT [DF_MESSAGEQUEUETABLE_CREATED] DEFAULT (SYSUTCDATETIME()) FOR [CREATEDDATETIME];
END
GO

-- Existing CREATEDDATETIME values are left as they are. They were written in server
-- local time, and Azure SQL runs in UTC, so they are already UTC in practice. A
-- blanket shift would corrupt them if that assumption ever failed to hold.
