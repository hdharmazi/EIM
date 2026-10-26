-------------------------------------------------------------
-- 015 Seed the PRD routes
--
-- The production stamp (configurations prdshared and prd) gets its own database,
-- sqldb-eim-wtc-prd, which runs every migration from 001. Migrations 006 and 014
-- seed BAT, DEV, DEV2 and TEST only, so without this one every production message
-- would dead-letter as NoRoute. The Automation host's DefaultEnvironment is the
-- configuration name, 'prd'; the collation is case-insensitive, so it matches 'PRD'.
--
-- Ten rows, insert-only (WHEN NOT MATCHED), so an operator's later change to a PRD
-- route is never overwritten and a re-run changes nothing:
--   - the nine routes of 006, ENABLED = 1, ONPAUSE = 'Hold'
--   - the address delete route of 014, ENABLED = 0, ONPAUSE = 'Hold': production
--     deletes are held, not dead-lettered, until Capture confirms the delete
--     contract. It is enabled in the Routes console.
--
-- Safe on both databases and needs no code ordering:
--   - Non-production eimdb: adds ten rows for an ENVIRONMENT value no
--     non-production message carries; the running build ignores them.
--   - Production sqldb-eim-wtc-prd: 001 to 014 run first, then this adds PRD.
--   Every orchestration type below is already in OrchestrationRegistry, so
--   RouteRegistryValidator accepts the rows before or after a deployment.
-------------------------------------------------------------

MERGE dbo.ROUTETABLE AS target
USING (
    SELECT N'PRD' AS ENVIRONMENT, r.SOURCESYSTEM, r.SOURCEENTITY, r.ORCHESTRATIONTYPE, r.ENABLED
    FROM (VALUES
        -- Capture -> D365 FSC
        (N'CAPTURE', N'Customer',                                   N'Customer_To_D365FSC',               1),
        (N'CAPTURE', N'Contact',                                    N'Contact_To_D365FSC',                1),
        (N'CAPTURE', N'PartyContact',                               N'PartyContact_To_D365FSC',           1),
        (N'CAPTURE', N'CustomerAddress',                            N'CustomerAddress_To_D365FSC',        1),
        (N'CAPTURE', N'SalesOrderQueue',                            N'SalesOrder_To_D365FSC',             1),
        -- D365 FSC -> Capture
        (N'D365FSC', N'mserp_custcustomerv3entity',                 N'Customer_To_Capture',               1),
        (N'D365FSC', N'mserp_smmcontactpersonv2entity',             N'Contact_To_Capture',                1),
        (N'D365FSC', N'mserp_wtc_custpostaladdressv2entity',        N'CustomerAddress_To_Capture',        1),
        (N'D365FSC', N'mserp_dirpartycontactv3entity',              N'PartyContact_To_Capture',           1),
        -- Paused until Capture confirms the delete contract (held, not dead-lettered)
        (N'D365FSC', N'mserp_wtc_custpostaladdressdeletedv2entity', N'CustomerAddressDelete_To_Capture',  0)
    ) AS r(SOURCESYSTEM, SOURCEENTITY, ORCHESTRATIONTYPE, ENABLED)
) AS source
ON  target.ENVIRONMENT  = source.ENVIRONMENT
AND target.SOURCESYSTEM = source.SOURCESYSTEM
AND target.SOURCEENTITY = source.SOURCEENTITY
WHEN NOT MATCHED BY TARGET THEN
    INSERT (ENVIRONMENT, SOURCESYSTEM, SOURCEENTITY, ORCHESTRATIONTYPE, ENABLED, ONPAUSE, MODIFIEDBY)
    VALUES (source.ENVIRONMENT, source.SOURCESYSTEM, source.SOURCEENTITY, source.ORCHESTRATIONTYPE,
            source.ENABLED, N'Hold', N'Migrations.015_seed_prd_routes.sql');
GO

-------------------------------------------------------------
-- Parity check, PRD rows only: the non-production table holds more rows, and
-- 006 already checked those. A failure throws, so DbUp does not journal 015.
--   - Always: the ten PRD keys exist, and every PRD route names a registered type.
--   - Only for rows still MODIFIEDBY this script: the seeded type, ENABLED and
--     ONPAUSE. A row an operator has changed (for example the delete route
--     enabled in the Routes console) is left as the operator set it, so a re-run
--     against a restored journal succeeds.
-------------------------------------------------------------
DECLARE @seed TABLE (
    SOURCESYSTEM      NVARCHAR(50)  NOT NULL,
    SOURCEENTITY      NVARCHAR(50)  NOT NULL,
    ORCHESTRATIONTYPE NVARCHAR(100) NOT NULL,
    ENABLED           BIT           NOT NULL,
    PRIMARY KEY (SOURCESYSTEM, SOURCEENTITY));

INSERT @seed (SOURCESYSTEM, SOURCEENTITY, ORCHESTRATIONTYPE, ENABLED) VALUES
    (N'CAPTURE', N'Customer',                                   N'Customer_To_D365FSC',               1),
    (N'CAPTURE', N'Contact',                                    N'Contact_To_D365FSC',                1),
    (N'CAPTURE', N'PartyContact',                               N'PartyContact_To_D365FSC',           1),
    (N'CAPTURE', N'CustomerAddress',                            N'CustomerAddress_To_D365FSC',        1),
    (N'CAPTURE', N'SalesOrderQueue',                            N'SalesOrder_To_D365FSC',             1),
    (N'D365FSC', N'mserp_custcustomerv3entity',                 N'Customer_To_Capture',               1),
    (N'D365FSC', N'mserp_smmcontactpersonv2entity',             N'Contact_To_Capture',                1),
    (N'D365FSC', N'mserp_wtc_custpostaladdressv2entity',        N'CustomerAddress_To_Capture',        1),
    (N'D365FSC', N'mserp_dirpartycontactv3entity',              N'PartyContact_To_Capture',           1),
    (N'D365FSC', N'mserp_wtc_custpostaladdressdeletedv2entity', N'CustomerAddressDelete_To_Capture',  0);

DECLARE @missing INT = (
    SELECT COUNT(*) FROM @seed AS s
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.ROUTETABLE AS t
        WHERE t.ENVIRONMENT  = N'PRD'
          AND t.SOURCESYSTEM = s.SOURCESYSTEM
          AND t.SOURCEENTITY = s.SOURCEENTITY));

IF @missing > 0
BEGIN
    DECLARE @msg NVARCHAR(200) = CONCAT(N'PRD route parity failed: ', @missing, N' of the 10 PRD routes are missing.');
    THROW 50015, @msg, 1;
END

-- A type absent from OrchestrationRegistry would fail host startup (RouteRegistryValidator).
-- The ten seeded types are the registered ones.
IF EXISTS (
    SELECT 1 FROM dbo.ROUTETABLE AS t
    WHERE t.ENVIRONMENT = N'PRD'
      AND t.ORCHESTRATIONTYPE NOT IN (SELECT s.ORCHESTRATIONTYPE FROM @seed AS s))
    THROW 50015, N'PRD route parity failed: an orchestration type is not in the registry.', 1;

-- Rows this script inserted and nobody has changed since: nine enabled, the address
-- delete route disabled, all with ONPAUSE = Hold.
IF EXISTS (
    SELECT 1
    FROM dbo.ROUTETABLE AS t
    JOIN @seed AS s
      ON  t.SOURCESYSTEM = s.SOURCESYSTEM
      AND t.SOURCEENTITY = s.SOURCEENTITY
    WHERE t.ENVIRONMENT = N'PRD'
      AND t.MODIFIEDBY  = N'Migrations.015_seed_prd_routes.sql'
      AND (t.ORCHESTRATIONTYPE <> s.ORCHESTRATIONTYPE
           OR t.ENABLED <> s.ENABLED
           OR t.ONPAUSE <> N'Hold'))
    THROW 50015, N'PRD route parity failed: a seeded PRD route does not match its seed (type, ENABLED, or ONPAUSE = Hold).', 1;
GO
