-------------------------------------------------------------
-- Widen MESSAGEQUEUETABLE.SOURCEID.
--
-- SOURCEID was NVARCHAR(30) while the source systems key on GUIDs, which are 36
-- characters. 77,700 rows therefore hold a GUID with its final group cut from
-- twelve characters to six, e.g. 00023284-ea7b-f111-ab0e-3833c5. Collisions are
-- not a practical risk at that width, but a truncated id cannot be used to find
-- the record back in the source system, so reconciliation and support lookups
-- were broken.
--
-- The earlier truncation audit did not catch this. That audit compared stored
-- procedure parameters against their columns, and here they agreed at 30: the
-- column itself was too narrow, so nothing looked inconsistent.
--
-- 150 to match DESTINATIONID rather than 36, since SOURCEID also carries
-- business keys from other systems and a width chosen for exactly one format is
-- how this happened in the first place.
--
-- This migration only widens. Repairing the existing rows is a separate,
-- chunked, resumable operation - see Script/Repair-TruncatedSourceId.ps1. The
-- first attempt did the repair here and exceeded a 1800 second timeout, because
-- recovering each value means scanning an NVARCHAR(MAX) payload and there are
-- 77,700 of them. It rolled back cleanly, but it had held a transaction against
-- a table that BAT and TEST write to for half an hour. A schema change belongs
-- in a migration; a long data repair on a shared live table does not.
-------------------------------------------------------------

-- The index leads on SOURCESYSTEM, ENVIRONMENT, SOURCEID and includes STATUS.
-- It has to go before the column can be altered, and comes back identically.
DROP INDEX IX_MESSAGEQUEUETABLE_SOURCEID ON dbo.MESSAGEQUEUETABLE;
GO

ALTER TABLE dbo.MESSAGEQUEUETABLE ALTER COLUMN SOURCEID NVARCHAR(150) NULL;
GO

CREATE NONCLUSTERED INDEX IX_MESSAGEQUEUETABLE_SOURCEID
    ON dbo.MESSAGEQUEUETABLE (SOURCESYSTEM, ENVIRONMENT, SOURCEID)
    INCLUDE (STATUS);
GO
