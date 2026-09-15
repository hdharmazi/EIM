-------------------------------------------------------------
-- 011 Gateway consumer, permission and idempotency tables.
--
-- These three back the D365 F&SC pass-through gateway. Nothing reads them until
-- that function app deploys, so this migration is inert on its own.
--
-- GATEWAYCONSUMER identifies a caller. It deliberately does NOT hold a key: the
-- secret stays in the Azure Functions host key store, where deleting it revokes
-- access immediately and across every instance without a database write or a
-- deploy. KEYHASH is only ever the SHA-256 of the presented key, and is nullable
-- because whether the host forwards x-functions-key to an isolated worker is
-- unverified. While it is null, identity comes from the X-Consumer-Id header and
-- is unverified - which is safe with exactly one consumer, because there is
-- nothing to escalate to, and unsafe the moment a second one exists.
--
-- GATEWAYPERMISSION is the authorization model: one row is one capability.
-- Granting per entity rather than per verb would mean a consumer allowed to read
-- an entity could also delete it - an invisible over-grant, because nobody
-- granting "CustomersV3" is thinking about DELETE. The dashboard presents these
-- rows as a verb matrix, so administrators still think in entities.
--
-- GATEWAYIDEMPOTENCY makes POST retries safe. A client cannot distinguish "never
-- arrived" from "succeeded, response lost", so retrying is correct behaviour and
-- is also what creates the duplicate ERP record.
-------------------------------------------------------------

IF OBJECT_ID('dbo.GATEWAYCONSUMER') IS NULL
BEGIN
    CREATE TABLE [dbo].[GATEWAYCONSUMER](
        [CONSUMERID]    NVARCHAR(50)  NOT NULL,
        [KEYHASH]       CHAR(64)      NULL,
        [SYSTEMPROFILE] NVARCHAR(30)  NOT NULL,
        [ENABLED]       BIT           NOT NULL,
        [EXPIRESON]     DATETIME2(3)  NULL,
        [DESCRIPTION]   NVARCHAR(200) NULL,
        [CREATEDBY]     NVARCHAR(100) NULL,
        [CREATEDON]     DATETIME2(3)  NOT NULL,
        [MODIFIEDBY]    NVARCHAR(100) NULL,
        [MODIFIEDON]    DATETIME2(3)  NULL,
        CONSTRAINT [PK_GATEWAYCONSUMER] PRIMARY KEY CLUSTERED ([CONSUMERID] ASC)
    );

    -- Deny by default: a consumer is inert until someone deliberately enables it.
    -- The opposite of ROUTETABLE, where defaulting to enabled reproduced existing
    -- behaviour; here there is no existing behaviour to preserve.
    ALTER TABLE [dbo].[GATEWAYCONSUMER]
        ADD CONSTRAINT [DF_GATEWAYCONSUMER_ENABLED] DEFAULT (0) FOR [ENABLED];

    ALTER TABLE [dbo].[GATEWAYCONSUMER]
        ADD CONSTRAINT [DF_GATEWAYCONSUMER_CREATEDON] DEFAULT (SYSUTCDATETIME()) FOR [CREATEDON];

    -- A raw function key is longer than 64 characters and is not hex, so this
    -- rejects the mistake that matters: storing the key instead of its hash.
    ALTER TABLE [dbo].[GATEWAYCONSUMER]
        ADD CONSTRAINT [CK_GATEWAYCONSUMER_KEYHASH]
            CHECK ([KEYHASH] IS NULL OR [KEYHASH] NOT LIKE '%[^0-9a-fA-F]%');

    -- Two consumers sharing a key would make identity ambiguous, which is the one
    -- thing KEYHASH exists to prevent.
    CREATE UNIQUE NONCLUSTERED INDEX [UX_GATEWAYCONSUMER_KEYHASH]
        ON [dbo].[GATEWAYCONSUMER] ([KEYHASH])
        WHERE [KEYHASH] IS NOT NULL;
END
GO

IF OBJECT_ID('dbo.GATEWAYPERMISSION') IS NULL
BEGIN
    CREATE TABLE [dbo].[GATEWAYPERMISSION](
        [CONSUMERID]   NVARCHAR(50)  NOT NULL,
        [ENVIRONMENT]  NVARCHAR(30)  NOT NULL,
        [RESOURCETYPE] NVARCHAR(20)  NOT NULL,
        [RESOURCE]     NVARCHAR(200) NOT NULL,
        [VERB]         NVARCHAR(10)  NOT NULL,
        [CREATEDBY]    NVARCHAR(100) NULL,
        [CREATEDON]    DATETIME2(3)  NOT NULL,
        CONSTRAINT [PK_GATEWAYPERMISSION] PRIMARY KEY CLUSTERED
            ([CONSUMERID] ASC, [ENVIRONMENT] ASC, [RESOURCETYPE] ASC, [RESOURCE] ASC, [VERB] ASC),
        CONSTRAINT [FK_GATEWAYPERMISSION_CONSUMER] FOREIGN KEY ([CONSUMERID])
            REFERENCES [dbo].[GATEWAYCONSUMER] ([CONSUMERID]) ON DELETE CASCADE
    );

    ALTER TABLE [dbo].[GATEWAYPERMISSION]
        ADD CONSTRAINT [DF_GATEWAYPERMISSION_CREATEDON] DEFAULT (SYSUTCDATETIME()) FOR [CREATEDON];

    ALTER TABLE [dbo].[GATEWAYPERMISSION]
        ADD CONSTRAINT [CK_GATEWAYPERMISSION_RESOURCETYPE]
            CHECK ([RESOURCETYPE] IN (N'DATA', N'ACTION', N'SERVICE'));

    -- DELETE is modelled in the verb vocabulary and shown disabled in the dashboard,
    -- but is not grantable in this release. Same reasoning as CK_ROUTETABLE_ONPAUSE:
    -- the constraint stops an unimplemented policy being set before the code honours
    -- it, and here it also closes the direct-SQL route into an irreversible verb.
    ALTER TABLE [dbo].[GATEWAYPERMISSION]
        ADD CONSTRAINT [CK_GATEWAYPERMISSION_VERB]
            CHECK ([VERB] IN (N'GET', N'POST', N'PATCH'));

    -- Bound actions and custom services are invoked by POST only.
    ALTER TABLE [dbo].[GATEWAYPERMISSION]
        ADD CONSTRAINT [CK_GATEWAYPERMISSION_NONDATA_POST]
            CHECK ([RESOURCETYPE] = N'DATA' OR [VERB] = N'POST');
END
GO

IF OBJECT_ID('dbo.GATEWAYIDEMPOTENCY') IS NULL
BEGIN
    CREATE TABLE [dbo].[GATEWAYIDEMPOTENCY](
        [CONSUMERID]     NVARCHAR(50)  NOT NULL,
        [IDEMPOTENCYKEY] NVARCHAR(100) NOT NULL,
        [REQUESTHASH]    CHAR(64)      NOT NULL,
        [STATUS]         NVARCHAR(20)  NOT NULL,
        [RESPONSESTATUS] INT           NULL,
        [RESPONSEBODY]   NVARCHAR(MAX) NULL,
        [CREATEDON]      DATETIME2(3)  NOT NULL,
        [EXPIRESON]      DATETIME2(3)  NOT NULL,
        CONSTRAINT [PK_GATEWAYIDEMPOTENCY] PRIMARY KEY CLUSTERED
            ([CONSUMERID] ASC, [IDEMPOTENCYKEY] ASC)
    );

    -- No foreign key to GATEWAYCONSUMER on purpose. This table takes an insert on
    -- every POST and is emptied by TTL, so it should not carry a constraint check
    -- on the hot path, nor expose a consumer deletion to an unbounded cascade.
    ALTER TABLE [dbo].[GATEWAYIDEMPOTENCY]
        ADD CONSTRAINT [DF_GATEWAYIDEMPOTENCY_CREATEDON] DEFAULT (SYSUTCDATETIME()) FOR [CREATEDON];

    ALTER TABLE [dbo].[GATEWAYIDEMPOTENCY]
        ADD CONSTRAINT [CK_GATEWAYIDEMPOTENCY_STATUS]
            CHECK ([STATUS] IN (N'INPROGRESS', N'COMPLETED'));

    -- Supports the retention purge, which is the only query that does not seek on
    -- the primary key.
    CREATE NONCLUSTERED INDEX [IX_GATEWAYIDEMPOTENCY_EXPIRESON]
        ON [dbo].[GATEWAYIDEMPOTENCY] ([EXPIRESON] ASC);
END
GO
