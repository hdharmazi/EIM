-------------------------------------------------------------
-- EIM Install DB
-- Installing main component of the tables
-------------------------------------------------------------

SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

-------------------------------------------------------------
-- [MESSAGEQUEUETABLE]
-- Main table for the messaging queue
-- SELECT * FROM MESSAGEQUEUETABLE
-- DROP TABLE MESSAGEQUEUETABLE
/*
STATUS value:
0 - New
1 - Processing
2 - Completed
3 - Failed
4 - Dead Letter
5 - Submitted / Awaiting D365 (async: submitted to a D365 message queue, awaiting business-event confirmation)
*/
--------------------------------------------------------------
	

CREATE TABLE [dbo].[MESSAGEQUEUETABLE](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ENVIRONMENT] [nvarchar](10) NULL,
	[SOURCEID] [nvarchar](30) NULL,
	[SOURCESYSTEM] [nvarchar](50) NULL,
	[SOURCEENTITY] [nvarchar](50) NULL,
	[DESTINATIONID] [nvarchar](150) NULL,
	[DESTINATIONENTITY] [nvarchar](50) NULL,
	[DESTINATIONSYSTEM] [nvarchar](50) NULL,
	[PAYLOAD] [nvarchar](max) NULL,
	[PAYLOADREF] [nvarchar](255) NULL,
	[RESPONSEQUEUE] [nvarchar](50) NULL,
	[QUEUE] [nvarchar](50) NULL,
	[STATUS] [int], 
	[TYPE] [nvarchar](50) NULL,
	[MESSAGELOG] [nvarchar](max) NULL,
	[CREATEDDATETIME] [datetime] NULL,
	[MODIFIEDDATETIME] [datetime] NULL
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
ALTER TABLE [dbo].[MESSAGEQUEUETABLE] ADD  CONSTRAINT [PK__MESSAGEQ__3214EC278B29A593] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO
ALTER TABLE [dbo].[MESSAGEQUEUETABLE] ADD  CONSTRAINT [DF_MESSAGEQUEUETABLE_STATUS]  DEFAULT ((0)) FOR [STATUS]
GO
ALTER TABLE [dbo].[MESSAGEQUEUETABLE] ADD  CONSTRAINT [DF_MESSAGEQUEUETABLE_CREATED]  DEFAULT (sysdatetime()) FOR [CREATEDDATETIME]
GO

-- IX_MESSAGEQUEUETABLE_DESTINATIONID is created once, at the foot of this script.

-------------------------------------------------------------
-- TRIGGER
-- Moved to Repeatable/R__TR_MESSAGEQUEUETABLE_MODIFIEDDATETIME.sql
-- (CREATE OR ALTER objects are repeatable, not versioned).
-------------------------------------------------------------

-------------------------------------------------------------
-- [ENVIRONMENTTABLE]
-- Environment table is to store connection string of the system
--------------------------------------------------------------
CREATE TABLE [dbo].[ENVIRONMENTTABLE](
	[SYSTEM] [nvarchar](30) NOT NULL,
	[ENVIRONMENT] [nvarchar](10) NOT NULL,
	[VALUE] [varchar](1000) NULL,
 CONSTRAINT [PK_ENVIRONMENTTABLE] PRIMARY KEY CLUSTERED 
(
	[SYSTEM] ASC,
	[ENVIRONMENT] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO

---- SAMPLE
INSERT INTO ENVIRONMENTTABLE VALUES ('SOURCESYSTEM', 'DEV', 'CONNECTIONSTRING')

-------------------------------------------------------------
-- [CONFIGURATIONTABLE]
-- Generic, reusable store of named configurations grouped by CATEGORY.
-- Editable fields are stored as a JSON object in VALUE (no fixed columns),
-- so any feature/project can define its own fields without schema changes.
--------------------------------------------------------------
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
GO

---- SAMPLE: simulation environments (queue-name sets picked in the Simulation console)
INSERT INTO CONFIGURATIONTABLE (CATEGORY, NAME, DISPLAYNAME, VALUE) VALUES
	('SimulationEnvironment', 'BAT', 'Business Acceptance Test', '{"D365Queue":"bat-d365fsc-dataevent","CaptureToD365Queue":"bat-capture-to-d365fsc","CaptureToD365ResponseQueue":"bat-capture-to-d365fsc-response","D365ToCaptureQueue":"bat-d365fsc-to-capture"}'),
	('SimulationEnvironment', 'DEV', 'Development', '{"D365Queue":"dev-d365fsc-dataevent","CaptureToD365Queue":"dev-capture-to-d365fsc","CaptureToD365ResponseQueue":"dev-capture-to-d365fsc-response","D365ToCaptureQueue":"dev-d365fsc-to-capture"}')
GO

-------------------------------------------------------------
-- [USERTABLE]
-- Authorized users and their roles for the dashboard
--------------------------------------------------------------
CREATE TABLE [dbo].[USERTABLE](
	[EMAIL] [nvarchar](256) NOT NULL,
	[DISPLAYNAME] [nvarchar](100) NULL,
	[ROLE] [nvarchar](20) NOT NULL CONSTRAINT [DF_USERTABLE_ROLE] DEFAULT ('Viewer'),
	[ISACTIVE] [bit] NOT NULL CONSTRAINT [DF_USERTABLE_ISACTIVE] DEFAULT (1),
	[CREATEDDATETIME] [datetime] NOT NULL CONSTRAINT [DF_USERTABLE_CREATED] DEFAULT (SYSDATETIME()),
 CONSTRAINT [PK_USERTABLE] PRIMARY KEY CLUSTERED 
(
	[EMAIL] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO

---- SAMPLE
INSERT INTO USERTABLE (EMAIL, DISPLAYNAME, ROLE) VALUES ('handoyo.dharmazi@armaninollp.com', 'Handoyo Dharmazi', 'Admin')
GO

/*
------------------------------------------------------------------
-- [FILECONFIGURATIONTABLE]
-- DROP TABLE [dbo].[FILECONFIGURATIONTABLE]
------------------------------------------------------------------
CREATE TABLE [dbo].[FILECONFIGURATIONTABLE](
	[ENVIRONMENT] [nchar](10) NOT NULL,
	[COMPANYID] [nchar](10) NULL,
	[ACTIVITYID] [nvarchar](60) NOT NULL,
	[FILENAMEMASK] [nchar](255) NOT NULL,
	[SYSTEM] [nchar](50) NOT NULL,
	[CONTAINER] [nchar](255) NULL,
 CONSTRAINT [PK_FILECONFIGURATION] PRIMARY KEY CLUSTERED 
(
	[ENVIRONMENT] ASC,
	[SYSTEM] ASC,
	[ACTIVITYID] ASC,
	[FILENAMEMASK] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO

------------------------------------------------------------------
-- [FILEEXECUTIONTABLE]
DROP TABLE [dbo].[FILEEXECUTIONTABLE]
------------------------------------------------------------------

CREATE TABLE [dbo].[FILEEXECUTIONTABLE](
	[MESSAGEID] [nvarchar](60) NOT NULL,
	[ID] [bigint] NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO

------------------------------------------------------------------
-- [FILESTATUSTABLE]
-- DROP TABLE [dbo].[FILESTATUSTABLE]
------------------------------------------------------------------

CREATE TABLE [dbo].[FILESTATUSTABLE](
	[MESSAGEID] [nvarchar](60) NOT NULL,
	[ID] [int] NOT NULL,
	[STATUS] [nvarchar](20) NULL,
 CONSTRAINT [PK_FILESTATUS] PRIMARY KEY CLUSTERED 
(
	[ID] ASC,
	[MESSAGEID] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
*/

-- SPINSERTMESSAGEQUEUE moved to script/SQL/Repeatable/R__SPINSERTMESSAGEQUEUE.sql
-- (CREATE OR ALTER objects are repeatable, not versioned).


CREATE TABLE [dbo].[MESSAGELOGDETAIL]
(
    [ID]              BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [MESSAGEID]       BIGINT        NOT NULL,
    [NAME]            NVARCHAR(200) NOT NULL,
    [DETAIL]          NVARCHAR(MAX) NULL,
    [CREATEDDATETIME] DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),

    INDEX IX_MESSAGELOGDETAIL_MESSAGEID NONCLUSTERED ([MESSAGEID])
);


CREATE TABLE [dbo].[WATERMARKTABLE](
    [INTEGRATION] [nvarchar](100) NOT NULL,
    [LASTRUN] [datetime] NOT NULL,
    CONSTRAINT [PK_WATERMARKTABLE] PRIMARY KEY CLUSTERED ([INTEGRATION] ASC)
)

/*
SELECT * FROM MESSAGEQUEUETABLE
SELECT * FROM MESSAGELOGDETAIL

TRUNCATE TABLE MESSAGEQUEUETABLE
DECLARE @EIMID BIGINT;
EXEC SPINSERTMESSAGEQUEUE '@ENVIRONMENT','TEST', 'SOURCESYSTEM', 'DESTINATIONSYSTEM', 'TYPE','PAYLOAD', '', '','',@EIMID
SELECT  @EIMID
*/


------------------------------------------------------------------
-- STATUSTABLE moved to Migrations/004_add_status_and_route_tables.sql,
-- which also seeds it. Declared in one place only.

--------------------------------
-- SAMPLE DATA
--------------------------------
-- SELEcT * FROM MESSAGEQUEUETABLE 
-- SELECT * FROM ENVIRONMENTTABLE 



-- Supports asynchronous reconciliation by correlation id
-- (DESTINATIONID holds the D365 CreatedTransactionId).
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_MESSAGEQUEUETABLE_DESTINATIONID'
      AND object_id = OBJECT_ID('dbo.MESSAGEQUEUETABLE')
)
CREATE NONCLUSTERED INDEX [IX_MESSAGEQUEUETABLE_DESTINATIONID]
    ON [dbo].[MESSAGEQUEUETABLE] ([DESTINATIONID] ASC);
GO