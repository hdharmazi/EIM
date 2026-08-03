-------------------------------------------------------------
-- Migration: add CONFIGURATIONTABLE (generic named-configuration store)
-- Safe to run against an existing database. Idempotent.
--------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CONFIGURATIONTABLE')
BEGIN
	CREATE TABLE [dbo].[CONFIGURATIONTABLE](
		[CATEGORY] [nvarchar](50) NOT NULL,
		[NAME] [nvarchar](50) NOT NULL,
		[DISPLAYNAME] [nvarchar](100) NULL,
		[VALUE] [nvarchar](max) NOT NULL,
		[ISACTIVE] [bit] NOT NULL CONSTRAINT [DF_CONFIGURATIONTABLE_ISACTIVE] DEFAULT (1),
		[CREATEDDATETIME] [datetime] NOT NULL CONSTRAINT [DF_CONFIGURATIONTABLE_CREATED] DEFAULT (SYSDATETIME()),
	 CONSTRAINT [PK_CONFIGURATIONTABLE] PRIMARY KEY CLUSTERED
	(
		[CATEGORY] ASC,
		[NAME] ASC
	)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
	) ON [PRIMARY]
END
GO

-- Seed simulation environments if absent
IF NOT EXISTS (SELECT 1 FROM CONFIGURATIONTABLE WHERE CATEGORY = 'SimulationEnvironment' AND NAME = 'BAT')
	INSERT INTO CONFIGURATIONTABLE (CATEGORY, NAME, DISPLAYNAME, VALUE) VALUES
		('SimulationEnvironment', 'BAT', 'Business Acceptance Test', '{"D365Queue":"bat-d365fsc-dataevent","CaptureToD365Queue":"bat-capture-to-d365fsc","CaptureToD365ResponseQueue":"bat-capture-to-d365fsc-response","D365ToCaptureQueue":"bat-d365fsc-to-capture"}');
GO

IF NOT EXISTS (SELECT 1 FROM CONFIGURATIONTABLE WHERE CATEGORY = 'SimulationEnvironment' AND NAME = 'DEV')
	INSERT INTO CONFIGURATIONTABLE (CATEGORY, NAME, DISPLAYNAME, VALUE) VALUES
		('SimulationEnvironment', 'DEV', 'Development', '{"D365Queue":"dev-d365fsc-dataevent","CaptureToD365Queue":"dev-capture-to-d365fsc","CaptureToD365ResponseQueue":"dev-capture-to-d365fsc-response","D365ToCaptureQueue":"dev-d365fsc-to-capture"}');
GO
