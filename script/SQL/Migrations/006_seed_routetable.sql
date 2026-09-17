-------------------------------------------------------------
-- 006 Seed ROUTETABLE
--
-- Every route is seeded ENABLED = 1 with ONPAUSE = 'Hold', which reproduces
-- current behaviour exactly: nothing is held until an operator pauses something.
--
-- Two directions, two naming vocabularies, both of which must be present:
--
--   CAPTURE  -> D365FSC   short entity names, from the envelope's "Entity" field
--                         (QueueCaptureHandler.EntityToOrchestration)
--   D365FSC  -> CAPTURE   Dataverse logical names, from PrimaryEntityName
--                         (QueueDataEventsHandler.EntityRoutes)
--
-- The reverse direction carries almost all the traffic - CustomerAddress alone is
-- 77,699 of 78,090 rows - so seeding only the forward dictionary would leave the
-- entity most likely to need pausing unroutable.
--
-- Environments come from ENVIRONMENTTABLE: BAT, DEV, DEV2, TEST. MESSAGEQUEUETABLE
-- stores these inconsistently cased ('bat' and 'BAT', 'test' and 'TEST'); the
-- database collation is case-insensitive so lookups still match, but see the note
-- at the foot of this file.
--
-- PollSourceHandler is deliberately excluded. Its SourceEntity is hardcoded to
-- "Customer" with a "customize per integration" comment and it has never produced
-- a message; it is a template, not a route.
-------------------------------------------------------------

MERGE dbo.ROUTETABLE AS target
USING (
    SELECT e.ENVIRONMENT, r.SOURCESYSTEM, r.SOURCEENTITY, r.ORCHESTRATIONTYPE
    FROM (VALUES (N'BAT'), (N'DEV'), (N'DEV2'), (N'TEST')) AS e(ENVIRONMENT)
    CROSS JOIN (VALUES
        -- Capture -> D365 FSC
        (N'CAPTURE', N'Customer',                            N'Customer_To_D365FSC'),
        (N'CAPTURE', N'Contact',                             N'Contact_To_D365FSC'),
        (N'CAPTURE', N'PartyContact',                        N'PartyContact_To_D365FSC'),
        (N'CAPTURE', N'CustomerAddress',                     N'CustomerAddress_To_D365FSC'),
        (N'CAPTURE', N'SalesOrderQueue',                     N'SalesOrder_To_D365FSC'),
        -- D365 FSC -> Capture
        (N'D365FSC', N'mserp_custcustomerv3entity',          N'Customer_To_Capture'),
        (N'D365FSC', N'mserp_smmcontactpersonv2entity',      N'Contact_To_Capture'),
        (N'D365FSC', N'mserp_wtc_custpostaladdressv2entity', N'CustomerAddress_To_Capture'),
        (N'D365FSC', N'mserp_dirpartycontactv3entity',       N'PartyContact_To_Capture')
    ) AS r(SOURCESYSTEM, SOURCEENTITY, ORCHESTRATIONTYPE)
) AS source
ON  target.ENVIRONMENT  = source.ENVIRONMENT
AND target.SOURCESYSTEM = source.SOURCESYSTEM
AND target.SOURCEENTITY = source.SOURCEENTITY
WHEN NOT MATCHED BY TARGET THEN
    INSERT (ENVIRONMENT, SOURCESYSTEM, SOURCEENTITY, ORCHESTRATIONTYPE, ENABLED, ONPAUSE, MODIFIEDBY)
    VALUES (source.ENVIRONMENT, source.SOURCESYSTEM, source.SOURCEENTITY, source.ORCHESTRATIONTYPE,
            1, N'Hold', N'Migrations.006_seed_routetable.sql');
GO

-------------------------------------------------------------
-- Parity check. Seeding a route table incorrectly drops traffic silently, so the
-- migration fails rather than leaving a half-seeded table behind.
-------------------------------------------------------------
DECLARE @environments INT = 4;
DECLARE @routes       INT = 9;
DECLARE @expected     INT = @environments * @routes;
DECLARE @actual       INT = (SELECT COUNT(*) FROM dbo.ROUTETABLE);

IF @actual <> @expected
BEGIN
    DECLARE @msg NVARCHAR(200) =
        CONCAT(N'ROUTETABLE seed parity failed: expected ', @expected, N' routes, found ', @actual, N'.');
    THROW 50001, @msg, 1;
END

IF EXISTS (SELECT 1 FROM dbo.ROUTETABLE WHERE ENABLED <> 1 OR ONPAUSE <> N'Hold')
    THROW 50002, N'ROUTETABLE seed parity failed: every seeded route must be enabled with ONPAUSE = Hold.', 1;

-- Every seeded orchestration type must be a registered orchestration. A type present
-- here and absent from OrchestrationRegistry would fail at runtime on live traffic
-- instead of here.
IF EXISTS (
    SELECT 1 FROM dbo.ROUTETABLE
    WHERE ORCHESTRATIONTYPE NOT IN (
        N'Customer_To_D365FSC', N'Contact_To_D365FSC', N'PartyContact_To_D365FSC',
        N'CustomerAddress_To_D365FSC', N'SalesOrder_To_D365FSC',
        N'Customer_To_Capture', N'Contact_To_Capture',
        N'CustomerAddress_To_Capture', N'PartyContact_To_Capture')
)
    THROW 50003, N'ROUTETABLE seed parity failed: an orchestration type is not in the registry.', 1;
GO

-- ENVIRONMENT casing is inconsistent in MESSAGEQUEUETABLE. It resolves today only
-- because the collation is case-insensitive, so IRouteResolver must not introduce a
-- case-sensitive comparison in application code. Normalising the historical values
-- is left as its own migration; it is not needed for routing to work.
