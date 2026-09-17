-------------------------------------------------------------
-- 003 Backfill SOURCEENTITY values truncated by SPINSERTMESSAGEQUEUE
--
-- SPINSERTMESSAGEQUEUE declared @SOURCEENTITY as VARCHAR(20) against an
-- NVARCHAR(50) column. SQL Server truncates stored-procedure parameters
-- silently, so every value longer than 20 characters was stored cut off.
--
-- 77,993 of 78,090 rows were affected. The true values were recovered from the
-- "LogicalName" field of each row's own PAYLOAD, and each truncated value maps
-- to exactly one true value:
--
--   mserp_wtc_custpostal -> mserp_wtc_custpostaladdressv2entity   (77,699 rows)
--   mserp_dirpartycontac -> mserp_dirpartycontactv3entity         (   141 rows)
--   mserp_custcustomerv3 -> mserp_custcustomerv3entity            (    95 rows)
--   mserp_smmcontactpers -> mserp_smmcontactpersonv2entity        (    58 rows)
--
-- This has to happen before routing keys on SOURCEENTITY, otherwise historical
-- rows would carry keys that no route matches.
--
-- The procedure is widened in Repeatable/R__SPINSERTMESSAGEQUEUE.sql, which the
-- runner applies after this script. Rows inserted in that short window may still
-- be truncated; verify afterwards with the query at the foot of this file.
-------------------------------------------------------------

-- The MODIFIEDDATETIME trigger is suppressed for the duration. Correcting a value
-- the database itself corrupted is not a business modification, and letting it stamp
-- 78k rows would make every historical row look freshly changed - which is exactly
-- the column the staleness and age reporting depends on. It also fires the old
-- single-row trigger once per statement, which is pure cost here.
ALTER TABLE dbo.MESSAGEQUEUETABLE DISABLE TRIGGER [MODIFIEDDATETIME];
GO

UPDATE dbo.MESSAGEQUEUETABLE
SET SOURCEENTITY = 'mserp_wtc_custpostaladdressv2entity'
WHERE SOURCEENTITY = 'mserp_wtc_custpostal';

UPDATE dbo.MESSAGEQUEUETABLE
SET SOURCEENTITY = 'mserp_dirpartycontactv3entity'
WHERE SOURCEENTITY = 'mserp_dirpartycontac';

UPDATE dbo.MESSAGEQUEUETABLE
SET SOURCEENTITY = 'mserp_custcustomerv3entity'
WHERE SOURCEENTITY = 'mserp_custcustomerv3';

UPDATE dbo.MESSAGEQUEUETABLE
SET SOURCEENTITY = 'mserp_smmcontactpersonv2entity'
WHERE SOURCEENTITY = 'mserp_smmcontactpers';
GO

ALTER TABLE dbo.MESSAGEQUEUETABLE ENABLE TRIGGER [MODIFIEDDATETIME];
GO

-- Post-deployment check. Any row still exactly 20 characters long was inserted
-- after the backfill and before the widened procedure took effect:
--
--   SELECT SOURCEENTITY, COUNT(*)
--   FROM dbo.MESSAGEQUEUETABLE
--   WHERE LEN(SOURCEENTITY) = 20
--   GROUP BY SOURCEENTITY;
