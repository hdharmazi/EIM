-------------------------------------------------------------
-- 016 Replace the CustomersV3 customer route with the WTC customer route
--
-- The lean WTC customer entity has no address data sources, so editing a postal
-- address cannot emit a false customer Create. Insert-only seeding preserves any
-- operator changes made to an existing WTC route. CustomersV3 routes are removed
-- from every environment because this integration change replaces that entity.
--
-- The new orchestration is already registered. DbMigrator embeds versioned SQL
-- files through the Migrations/*.sql project glob.
-------------------------------------------------------------

MERGE dbo.ROUTETABLE AS target
USING (
    SELECT e.ENVIRONMENT,
           N'D365FSC' AS SOURCESYSTEM,
           N'mserp_wtc_custcustomerentity' AS SOURCEENTITY,
           N'Customer_To_Capture' AS ORCHESTRATIONTYPE
    FROM (VALUES (N'BAT'), (N'DEV'), (N'DEV2'), (N'TEST'), (N'PRD')) AS e(ENVIRONMENT)
) AS source
ON  target.ENVIRONMENT  = source.ENVIRONMENT
AND target.SOURCESYSTEM = source.SOURCESYSTEM
AND target.SOURCEENTITY = source.SOURCEENTITY
WHEN NOT MATCHED BY TARGET THEN
    INSERT (ENVIRONMENT, SOURCESYSTEM, SOURCEENTITY, ORCHESTRATIONTYPE, ENABLED, ONPAUSE, MODIFIEDBY)
    VALUES (source.ENVIRONMENT, source.SOURCESYSTEM, source.SOURCEENTITY, source.ORCHESTRATIONTYPE,
            1, N'Hold', N'Migrations.016_replace_customer_route.sql');
GO

DELETE FROM dbo.ROUTETABLE
WHERE SOURCESYSTEM = N'D365FSC'
  AND SOURCEENTITY = N'mserp_custcustomerv3entity';
GO

-------------------------------------------------------------
-- Parity check. Existing operator-modified WTC rows are preserved; only rows
-- still marked as seeded by this migration must match the initial values.
-------------------------------------------------------------
DECLARE @seed TABLE (
    ENVIRONMENT       NVARCHAR(50)  NOT NULL PRIMARY KEY,
    SOURCESYSTEM      NVARCHAR(50)  NOT NULL,
    SOURCEENTITY      NVARCHAR(50)  NOT NULL,
    ORCHESTRATIONTYPE NVARCHAR(100) NOT NULL);

INSERT @seed (ENVIRONMENT, SOURCESYSTEM, SOURCEENTITY, ORCHESTRATIONTYPE) VALUES
    (N'BAT',  N'D365FSC', N'mserp_wtc_custcustomerentity', N'Customer_To_Capture'),
    (N'DEV',  N'D365FSC', N'mserp_wtc_custcustomerentity', N'Customer_To_Capture'),
    (N'DEV2', N'D365FSC', N'mserp_wtc_custcustomerentity', N'Customer_To_Capture'),
    (N'TEST', N'D365FSC', N'mserp_wtc_custcustomerentity', N'Customer_To_Capture'),
    (N'PRD',  N'D365FSC', N'mserp_wtc_custcustomerentity', N'Customer_To_Capture');

IF EXISTS (
    SELECT 1 FROM @seed AS s
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.ROUTETABLE AS t
        WHERE t.ENVIRONMENT       = s.ENVIRONMENT
          AND t.SOURCESYSTEM      = s.SOURCESYSTEM
          AND t.SOURCEENTITY      = s.SOURCEENTITY))
    THROW 50016, N'Customer route parity failed: one or more WTC customer routes are missing.', 1;

IF EXISTS (
    SELECT 1
    FROM dbo.ROUTETABLE AS t
    JOIN @seed AS s
      ON  t.ENVIRONMENT  = s.ENVIRONMENT
      AND t.SOURCESYSTEM = s.SOURCESYSTEM
      AND t.SOURCEENTITY = s.SOURCEENTITY
    WHERE t.MODIFIEDBY = N'Migrations.016_replace_customer_route.sql'
      AND (t.ORCHESTRATIONTYPE <> s.ORCHESTRATIONTYPE
           OR t.ENABLED <> 1
           OR t.ONPAUSE <> N'Hold'))
    THROW 50016, N'Customer route parity failed: a seeded WTC route differs from its initial values.', 1;

IF EXISTS (
    SELECT 1 FROM dbo.ROUTETABLE
    WHERE SOURCESYSTEM = N'D365FSC'
      AND SOURCEENTITY = N'mserp_custcustomerv3entity')
    THROW 50016, N'Customer route parity failed: one or more CustomersV3 routes remain.', 1;
GO
