-------------------------------------------------------------
-- 010 Widen ENVIRONMENTTABLE.
--
-- ENVIRONMENT was NVARCHAR(10). Existing values fit (DEV, DEV2, BAT, TEST,
-- CDB-PROD) but only just, and the pass-through gateway adds a second connection
-- profile per environment, so environment naming now has to carry more meaning
-- than it did. 30 matches SYSTEM; picking a width that exactly fits today's
-- longest value is how MESSAGEQUEUETABLE.SOURCEID ended up truncated (see 009).
--
-- ENVIRONMENT is part of the primary key, so widening it means dropping and
-- recreating the constraint. The table holds a handful of rows, so this is
-- effectively instantaneous - but note that one eimdb serves dev, dev1, bat and
-- test at once, so this runs against every environment simultaneously.
--
-- VALUE was VARCHAR(1000). It holds the D365 connection JSON (tenant, client id,
-- secret, URL, auth URL), currently around 350 characters. NVARCHAR(4000) is the
-- widest that still fits in row, so this avoids LOB semantics while leaving real
-- headroom - notably for a future Key Vault SecretRef field. Deliberately not
-- NVARCHAR(MAX): nothing here needs it, and an unbounded configuration column
-- invites someone to paste a certificate into it.
--
-- Both changes are widening, so every existing reader (EIMUtilities.GetEnvironment,
-- EnvironmentRepository) continues to work untouched.
-------------------------------------------------------------

IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'PK_ENVIRONMENTTABLE')
BEGIN
    ALTER TABLE [dbo].[ENVIRONMENTTABLE] DROP CONSTRAINT [PK_ENVIRONMENTTABLE];
END
GO

ALTER TABLE [dbo].[ENVIRONMENTTABLE] ALTER COLUMN [ENVIRONMENT] NVARCHAR(30) NOT NULL;
GO

-- VARCHAR -> NVARCHAR is a safe widening for the existing ASCII content.
ALTER TABLE [dbo].[ENVIRONMENTTABLE] ALTER COLUMN [VALUE] NVARCHAR(4000) NULL;
GO

ALTER TABLE [dbo].[ENVIRONMENTTABLE]
    ADD CONSTRAINT [PK_ENVIRONMENTTABLE] PRIMARY KEY CLUSTERED
        ([SYSTEM] ASC, [ENVIRONMENT] ASC);
GO
