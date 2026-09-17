-------------------------------------------------------------
-- 007 Rename the simulation queue field to D365FscDataEventQueue
--
-- CONFIGURATIONTABLE holds one SimulationEnvironment row per environment, whose
-- VALUE is a JSON object of queue names. The field was named "D365Queue"
-- everywhere except DEV2, which had already been renamed to
-- "D365FscDataEventQueue" without the code following - which is why the
-- simulation page reported the queue as not configured for DEV2.
--
-- Standardising on D365FscDataEventQueue matches the name the deployment
-- scripts already use for the equivalent app setting.
--
-- This must be applied together with the code change that reads the new field.
-- The runner applies it before the application deploys, so the window where the
-- code and the data disagree is the deployment itself.
-------------------------------------------------------------

UPDATE dbo.CONFIGURATIONTABLE
SET [VALUE] = JSON_MODIFY(
                  JSON_MODIFY(CAST([VALUE] AS nvarchar(max)), '$.D365Queue', NULL),
                  '$.D365FscDataEventQueue',
                  JSON_VALUE(CAST([VALUE] AS nvarchar(max)), '$.D365Queue'))
WHERE JSON_VALUE(CAST([VALUE] AS nvarchar(max)), '$.D365Queue') IS NOT NULL;
GO

-------------------------------------------------------------
-- No row may still carry the old field, and every row that had it must now
-- carry the new one. A silently half-renamed row would fail only when someone
-- opened the simulation page for that environment.
-------------------------------------------------------------
IF EXISTS (
    SELECT 1 FROM dbo.CONFIGURATIONTABLE
    WHERE JSON_VALUE(CAST([VALUE] AS nvarchar(max)), '$.D365Queue') IS NOT NULL
)
    THROW 50010, N'CONFIGURATIONTABLE still has rows carrying the old D365Queue field.', 1;

IF EXISTS (
    SELECT 1 FROM dbo.CONFIGURATIONTABLE
    WHERE CATEGORY = N'SimulationEnvironment'
      AND JSON_VALUE(CAST([VALUE] AS nvarchar(max)), '$.D365FscDataEventQueue') IS NULL
)
    THROW 50011, N'A SimulationEnvironment row has no D365FscDataEventQueue field.', 1;
GO
