-------------------------------------------------------------
-- 012 Record what a consumer's key was verified against, and when.
--
-- Until now the dashboard hashed whatever string it was given and reported the
-- consumer as verified. A typo, a stale key, or a key belonging to a different
-- function app all looked identical to success, and the mistake only surfaced
-- later as a 403 on the consumer's first real request.
--
-- The dashboard now probes the key against the gateway before storing it. These
-- two columns record the result so a registration says what was checked rather
-- than merely that a hash exists.
--
-- Both are nullable, and null means "not known" rather than "no application".
-- Rows registered before verification existed keep working: the runtime
-- authorization path reads KEYHASH and nothing else, and is deliberately unaware
-- these columns exist. Backfilling would need the key values, which are
-- unrecoverable by design.
-------------------------------------------------------------

IF COL_LENGTH('dbo.GATEWAYCONSUMER', 'VERIFIEDAPP') IS NULL
BEGIN
    ALTER TABLE [dbo].[GATEWAYCONSUMER]
        ADD [VERIFIEDAPP] NVARCHAR(100) NULL;
END
GO

IF COL_LENGTH('dbo.GATEWAYCONSUMER', 'VERIFIEDON') IS NULL
BEGIN
    ALTER TABLE [dbo].[GATEWAYCONSUMER]
        ADD [VERIFIEDON] DATETIME2(3) NULL;
END
GO
