-------------------------------------------------------------
-- 004 STATUSTABLE and ROUTETABLE
--
-- STATUSTABLE is the display catalogue for MESSAGEQUEUETABLE.STATUS. The values
-- themselves live in ARM.Integration.EIM/MessageStatus.cs; this table exists so
-- the dashboard reads labels from data rather than a hardcoded badge map.
--
-- ROUTETABLE replaces the hardcoded EntityToOrchestration dictionary. ENVIRONMENT
-- is part of the key so a route can be paused in one environment while staying
-- live in another.
-------------------------------------------------------------

IF OBJECT_ID('dbo.STATUSTABLE') IS NULL
BEGIN
    CREATE TABLE [dbo].[STATUSTABLE](
        [STATUS]      INT          NOT NULL,
        [STATUSVALUE] NVARCHAR(30) NOT NULL,
        CONSTRAINT [PK_STATUSTABLE] PRIMARY KEY CLUSTERED ([STATUS] ASC)
    );
END
GO

MERGE dbo.STATUSTABLE AS target
USING (VALUES
        (0, N'New'),
        (1, N'Processing'),
        (2, N'Completed'),
        (3, N'Failed'),
        (4, N'Dead Letter'),
        (5, N'Awaiting D365'),
        (6, N'Held'),
        (7, N'Retry Scheduled')
      ) AS source ([STATUS], [STATUSVALUE])
ON target.[STATUS] = source.[STATUS]
WHEN MATCHED AND target.[STATUSVALUE] <> source.[STATUSVALUE]
    THEN UPDATE SET target.[STATUSVALUE] = source.[STATUSVALUE]
WHEN NOT MATCHED BY TARGET
    THEN INSERT ([STATUS], [STATUSVALUE]) VALUES (source.[STATUS], source.[STATUSVALUE]);
GO

IF OBJECT_ID('dbo.ROUTETABLE') IS NULL
BEGIN
    CREATE TABLE [dbo].[ROUTETABLE](
        [ENVIRONMENT]       NVARCHAR(10)  NOT NULL,
        [SOURCESYSTEM]      NVARCHAR(50)  NOT NULL,
        [SOURCEENTITY]      NVARCHAR(50)  NOT NULL,
        [ORCHESTRATIONTYPE] NVARCHAR(100) NOT NULL,
        [ENABLED]           BIT           NOT NULL,
        [ONPAUSE]           NVARCHAR(20)  NOT NULL,
        [MODIFIEDBY]        NVARCHAR(100) NULL,
        [MODIFIEDDATETIME]  DATETIME2(3)  NOT NULL,
        CONSTRAINT [PK_ROUTETABLE] PRIMARY KEY CLUSTERED
            ([ENVIRONMENT] ASC, [SOURCESYSTEM] ASC, [SOURCEENTITY] ASC)
    );

    -- Existing routes must default to enabled, so seeding reproduces today's behaviour exactly.
    ALTER TABLE [dbo].[ROUTETABLE]
        ADD CONSTRAINT [DF_ROUTETABLE_ENABLED] DEFAULT (1) FOR [ENABLED];

    ALTER TABLE [dbo].[ROUTETABLE]
        ADD CONSTRAINT [DF_ROUTETABLE_ONPAUSE] DEFAULT (N'Hold') FOR [ONPAUSE];

    ALTER TABLE [dbo].[ROUTETABLE]
        ADD CONSTRAINT [DF_ROUTETABLE_MODIFIEDDATETIME] DEFAULT (SYSUTCDATETIME()) FOR [MODIFIEDDATETIME];

    -- Drop is modelled but not implemented by this change; the constraint keeps an
    -- unimplemented policy from being set before the sweep understands it.
    ALTER TABLE [dbo].[ROUTETABLE]
        ADD CONSTRAINT [CK_ROUTETABLE_ONPAUSE] CHECK ([ONPAUSE] IN (N'Hold', N'Drop'));
END
GO
