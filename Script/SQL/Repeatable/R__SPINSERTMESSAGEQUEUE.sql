-------------------------------------------------------------
-- REPEATABLE: SPINSERTMESSAGEQUEUE
-- Re-applied on every deployment so the deployed definition always
-- matches this file. Do not journal this script.
--
-- Parameters are NVARCHAR and sized to match their columns exactly. SQL Server
-- truncates stored-procedure parameters silently rather than raising, so a
-- parameter narrower than its column loses data with no error: @SOURCEENTITY at
-- VARCHAR(20) against an NVARCHAR(50) column truncated 77,993 rows before
-- Migrations/003 backfilled them.
--
-- Matching the column is necessary but not sufficient. @SOURCEID and its column
-- agreed at NVARCHAR(30) and both were too narrow for the 36-character GUIDs the
-- sources key on, so 77,700 rows were truncated with nothing looking
-- inconsistent. Migrations/009 widened both to 150.
--
-- ENVIRONMENT is upper-cased here so every writer stores one canonical form,
-- matching ROUTETABLE and ENVIRONMENTTABLE. History is deliberately left as it
-- is; see task 2.4.
-------------------------------------------------------------

-- EXEC SPINSERTMESSAGEQUEUE 'TEST', 'SOURCID', 'SOURCESYS', 'DEST','TYPE', 'PAY'
GO

CREATE  OR ALTER PROCEDURE [dbo].[SPINSERTMESSAGEQUEUE]
(    
   @ENVIRONMENT			NVARCHAR(10),
   @SOURCEID			NVARCHAR(150),
   @SOURCESYSTEM		NVARCHAR(50),  
   @DESTINATIONSYSTEM	NVARCHAR(50),
   @TYPE				NVARCHAR(50),
   @PAYLOAD				NVARCHAR(MAX),   
   @SOURCEENTITY		NVARCHAR(50) = '',
   @DESTINATIONID		NVARCHAR(150) = '',
   @DESTINATIONENTITY	NVARCHAR(50) = '',
   @QUEUE				NVARCHAR(50) = '',
   @RESPONSEQUEUE		NVARCHAR(50) = '',
   @STATUS				INT = 0,
   @DEDUPEKEY			NVARCHAR(200) = NULL

)
AS
BEGIN
    -- SET NOCOUNT ON added to prevent extra result sets from
    -- interfering with SELECT statements.
    SET NOCOUNT ON

    INSERT INTO MESSAGEQUEUETABLE (ENVIRONMENT,SOURCEID,SOURCESYSTEM, DESTINATIONID, DESTINATIONSYSTEM,
								   TYPE	, PAYLOAD, SOURCEENTITY, DESTINATIONENTITY, STATUS, QUEUE, RESPONSEQUEUE,
								   MODIFIEDDATETIME, DEDUPEKEY)  
	OUTPUT INSERTED.ID
							VALUES (UPPER(@ENVIRONMENT),@SOURCEID,@SOURCESYSTEM,@DESTINATIONID, @DESTINATIONSYSTEM,
								   @TYPE, @PAYLOAD, @SOURCEENTITY, @DESTINATIONENTITY, @STATUS, @QUEUE, @RESPONSEQUEUE,
								   SYSUTCDATETIME(), NULLIF(@DEDUPEKEY, ''))

						
END
GO
