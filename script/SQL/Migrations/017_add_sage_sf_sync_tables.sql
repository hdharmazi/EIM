-------------------------------------------------------------
-- 017 Sage↔Salesforce sync tables
--
-- sync_link: one row per synced record, tracking both-side
--   hashes for echo suppression and modification times.
-- sage_outbound: planned Sage writes (field-level diffs)
--   queued for review/approval before BOI application.
-- sync_conflict: audit log of both-sides-changed conflicts.
--
-- These are new tables for the Sage↔SF integration; they
-- share the EIMDB alongside the existing EIM tables.
-------------------------------------------------------------

IF OBJECT_ID('dbo.sync_link') IS NULL
BEGIN
    CREATE TABLE [dbo].[sync_link](
        [entity]         NVARCHAR(50)   NOT NULL,
        [sage_key]       NVARCHAR(50)   NOT NULL,
        [sf_id]          NVARCHAR(18)   NULL,
        [sage_hash]      CHAR(64)       NULL,
        [sf_hash]        CHAR(64)       NULL,
        [sage_modified]  DATETIME2(3)   NULL,
        [sf_modified]    DATETIME2(3)   NULL,
        [last_synced_at] DATETIME2(3)   NULL,
        [last_direction] NVARCHAR(10)   NULL,
        [state]          NVARCHAR(20)   NOT NULL
            CONSTRAINT [DF_sync_link_state] DEFAULT ('active'),

        CONSTRAINT [PK_sync_link] PRIMARY KEY CLUSTERED
            ([entity] ASC, [sage_key] ASC)
    );

    CREATE NONCLUSTERED INDEX [IX_sync_link_sf_id]
        ON [dbo].[sync_link] ([entity], [sf_id])
        WHERE [sf_id] IS NOT NULL;

    CREATE NONCLUSTERED INDEX [IX_sync_link_state]
        ON [dbo].[sync_link] ([state])
        WHERE [state] IN ('sage_deleted', 'sf_deleted', 'orphan');
END
GO

IF OBJECT_ID('dbo.sage_outbound') IS NULL
BEGIN
    CREATE TABLE [dbo].[sage_outbound](
        [id]              BIGINT IDENTITY(1,1) NOT NULL,
        [entity]          NVARCHAR(50)   NOT NULL,
        [sage_key]        NVARCHAR(50)   NULL,
        [sf_id]           NVARCHAR(18)   NOT NULL,
        [changes]         NVARCHAR(MAX)  NOT NULL,
        [base_sage_hash]  CHAR(64)       NULL,
        [status]          NVARCHAR(20)   NOT NULL
            CONSTRAINT [DF_sage_outbound_status] DEFAULT ('pending'),
        [boi_message]     NVARCHAR(MAX)  NULL,
        [created_at]      DATETIME2(3)   NOT NULL
            CONSTRAINT [DF_sage_outbound_created] DEFAULT (SYSUTCDATETIME()),
        [modified_at]     DATETIME2(3)   NOT NULL
            CONSTRAINT [DF_sage_outbound_modified] DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT [PK_sage_outbound] PRIMARY KEY CLUSTERED ([id] ASC),
        CONSTRAINT [CK_sage_outbound_status] CHECK (
            [status] IN ('pending','validated','approved','applied',
                         'rejected','held_conflict','failed'))
    );

    CREATE NONCLUSTERED INDEX [IX_sage_outbound_status]
        ON [dbo].[sage_outbound] ([status])
        WHERE [status] IN ('pending', 'validated', 'approved', 'held_conflict', 'failed');
END
GO

IF OBJECT_ID('dbo.sync_conflict') IS NULL
BEGIN
    CREATE TABLE [dbo].[sync_conflict](
        [id]              BIGINT IDENTITY(1,1) NOT NULL,
        [entity]          NVARCHAR(50)   NOT NULL,
        [sage_key]        NVARCHAR(50)   NOT NULL,
        [field_name]      NVARCHAR(100)  NOT NULL,
        [sage_value]      NVARCHAR(MAX)  NULL,
        [sf_value]        NVARCHAR(MAX)  NULL,
        [sage_modified]   DATETIME2(3)   NULL,
        [sf_modified]     DATETIME2(3)   NULL,
        [resolution]      NVARCHAR(20)   NOT NULL,
        [reviewed]        BIT            NOT NULL
            CONSTRAINT [DF_sync_conflict_reviewed] DEFAULT (0),
        [created_at]      DATETIME2(3)   NOT NULL
            CONSTRAINT [DF_sync_conflict_created] DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT [PK_sync_conflict] PRIMARY KEY CLUSTERED ([id] ASC)
    );

    CREATE NONCLUSTERED INDEX [IX_sync_conflict_unreviewed]
        ON [dbo].[sync_conflict] ([reviewed])
        WHERE [reviewed] = 0;
END
GO

-------------------------------------------------------------
-- Seed Sage→SF routes into ROUTETABLE
-------------------------------------------------------------
MERGE dbo.ROUTETABLE AS target
USING (VALUES
        (N'DEV', N'Sage', N'Customer', N'Customer_To_Salesforce', 1, N'Hold', SYSUTCDATETIME()),
        (N'DEV', N'Sage', N'Contact',  N'Contact_To_Salesforce',  1, N'Hold', SYSUTCDATETIME()),
        (N'DEV', N'Sage', N'Item',     N'Item_To_Salesforce',     1, N'Hold', SYSUTCDATETIME())
      ) AS source ([ENVIRONMENT],[SOURCESYSTEM],[SOURCEENTITY],[ORCHESTRATIONTYPE],[ENABLED],[ONPAUSE],[MODIFIEDDATETIME])
ON  target.[ENVIRONMENT]  = source.[ENVIRONMENT]
AND target.[SOURCESYSTEM] = source.[SOURCESYSTEM]
AND target.[SOURCEENTITY] = source.[SOURCEENTITY]
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([ENVIRONMENT],[SOURCESYSTEM],[SOURCEENTITY],[ORCHESTRATIONTYPE],[ENABLED],[ONPAUSE],[MODIFIEDDATETIME])
    VALUES (source.[ENVIRONMENT],source.[SOURCESYSTEM],source.[SOURCEENTITY],source.[ORCHESTRATIONTYPE],source.[ENABLED],source.[ONPAUSE],source.[MODIFIEDDATETIME]);
GO
