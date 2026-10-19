-------------------------------------------------------------
-- 014 Seed the customer address delete route
--
-- D365 F&SC surfaces address deletions as updates on a separate soft-delete entity,
-- mserp_wtc_custpostaladdressdeletedv2entity, so it needs its own route alongside
-- the New/Update address route seeded by 006.
--
-- Deploy the Functions host carrying CustomerAddressDelete_To_Capture BEFORE running
-- this migration: RouteRegistryValidator fails host startup when a route names an
-- unregistered orchestration.
-------------------------------------------------------------

MERGE dbo.ROUTETABLE AS target
USING (
    SELECT e.ENVIRONMENT,
           N'D365FSC'                                      AS SOURCESYSTEM,
           N'mserp_wtc_custpostaladdressdeletedv2entity'   AS SOURCEENTITY,
           N'CustomerAddressDelete_To_Capture'             AS ORCHESTRATIONTYPE
    FROM (VALUES (N'BAT'), (N'DEV'), (N'DEV2'), (N'TEST')) AS e(ENVIRONMENT)
) AS source
ON  target.ENVIRONMENT  = source.ENVIRONMENT
AND target.SOURCESYSTEM = source.SOURCESYSTEM
AND target.SOURCEENTITY = source.SOURCEENTITY
WHEN NOT MATCHED BY TARGET THEN
    INSERT (ENVIRONMENT, SOURCESYSTEM, SOURCEENTITY, ORCHESTRATIONTYPE, ENABLED, ONPAUSE, MODIFIEDBY)
    VALUES (source.ENVIRONMENT, source.SOURCESYSTEM, source.SOURCEENTITY, source.ORCHESTRATIONTYPE,
            1, N'Hold', N'Migrations.014_seed_customer_address_delete_route.sql');
GO

-------------------------------------------------------------
-- Parity check. Under-seeding dead-letters deletes in one environment only.
-------------------------------------------------------------
DECLARE @expected INT = 4;
DECLARE @actual   INT = (
    SELECT COUNT(*) FROM dbo.ROUTETABLE
    WHERE SOURCESYSTEM      = N'D365FSC'
      AND SOURCEENTITY      = N'mserp_wtc_custpostaladdressdeletedv2entity'
      AND ORCHESTRATIONTYPE = N'CustomerAddressDelete_To_Capture'
      AND ENVIRONMENT IN (N'BAT', N'DEV', N'DEV2', N'TEST'));

IF @actual <> @expected
BEGIN
    DECLARE @msg NVARCHAR(200) =
        CONCAT(N'Customer address delete route parity failed: expected ', @expected, N' routes, found ', @actual, N'.');
    THROW 50014, @msg, 1;
END
GO
