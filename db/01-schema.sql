/*
  Meeting check-in system: local mock database (schema)

  The original system ran against a hospital's internal databases, which are not
  available outside that environment. This script recreates only the tables and
  stored procedures the application uses, so the project can run locally.

    LineDB       employee directory + login (mock of the hospital's HR/LINE data)
    LineMeeting  meetings, participants, attendance, meeting rooms

  The script is idempotent: running it again does not drop existing data.
  Run with sqlcmd and pass the app login password as a variable:
    sqlcmd ... -v APP_DB_PASSWORD="..." -i 01-schema.sql
*/

SET NOCOUNT ON;
-- Required for filtered indexes; sqlcmd turns QUOTED_IDENTIFIER off by default.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF DB_ID(N'LineDB') IS NULL CREATE DATABASE LineDB;
GO
IF DB_ID(N'LineMeeting') IS NULL CREATE DATABASE LineMeeting;
GO

/* ===================================================================== */
/*  LineDB                                                               */
/* ===================================================================== */
USE LineDB;
GO

-- Employee directory. Column names follow the original HR view
-- (SNTOPID = employee no., CNM = name, DPID/DPCNM = department, DUTNM = job title).
IF OBJECT_ID(N'dbo.UserInfo', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.UserInfo
    (
        SNTOPID  VARCHAR(20)   NOT NULL CONSTRAINT PK_UserInfo PRIMARY KEY,
        CNM      NVARCHAR(50)  NOT NULL,
        DPID     NVARCHAR(20)  NOT NULL,
        DPCNM    NVARCHAR(50)  NOT NULL,
        DUTNM    NVARCHAR(50)  NULL,
        UserID   NVARCHAR(100) NULL   -- LINE user ID, filled after the employee links their LINE account
    );

    CREATE UNIQUE INDEX UX_UserInfo_UserID ON dbo.UserInfo (UserID) WHERE UserID IS NOT NULL;
    CREATE INDEX IX_UserInfo_DPID ON dbo.UserInfo (DPID);
END
GO

-- Login accounts. Passwords are stored as salted SHA-256 hashes, never as plain text.
IF OBJECT_ID(N'dbo.LoginAccount', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LoginAccount
    (
        Account      NVARCHAR(50)   NOT NULL CONSTRAINT PK_LoginAccount PRIMARY KEY,
        SNTOPID      VARCHAR(20)    NOT NULL CONSTRAINT FK_LoginAccount_UserInfo REFERENCES dbo.UserInfo (SNTOPID),
        PasswordSalt VARBINARY(16)  NOT NULL,
        PasswordHash VARBINARY(32)  NOT NULL,
        IsActive     BIT            NOT NULL CONSTRAINT DF_LoginAccount_IsActive DEFAULT (1)
    );
END
GO

-- Helper used by the seed script (and by you) to set or reset a password.
CREATE OR ALTER PROCEDURE dbo.SetLoginPassword
    @Account  NVARCHAR(50),
    @SNTOPID  VARCHAR(20),
    @Password NVARCHAR(200)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Salt VARBINARY(16) = CRYPT_GEN_RANDOM(16);
    DECLARE @Hash VARBINARY(32) = HASHBYTES('SHA2_256', @Salt + CONVERT(VARBINARY(400), @Password));
    DECLARE @NormalizedAccount NVARCHAR(50) = UPPER(LTRIM(RTRIM(@Account)));

    IF EXISTS (SELECT 1 FROM dbo.LoginAccount WHERE Account = @NormalizedAccount)
        UPDATE dbo.LoginAccount
        SET SNTOPID = @SNTOPID, PasswordSalt = @Salt, PasswordHash = @Hash, IsActive = 1
        WHERE Account = @NormalizedAccount;
    ELSE
        INSERT dbo.LoginAccount (Account, SNTOPID, PasswordSalt, PasswordHash)
        VALUES (@NormalizedAccount, @SNTOPID, @Salt, @Hash);
END
GO

-- Same signature as the hospital's procedure the app calls.
-- Output: @RetuenMsg starts with 'True' or 'False' (spelling kept from the original interface).
CREATE OR ALTER PROCEDURE dbo.Login_Check
    @LogAccount NVARCHAR(100),
    @LogPASS    NVARCHAR(200),
    @RetuenMsg  NVARCHAR(500) OUTPUT,
    @cnm        NVARCHAR(100) OUTPUT,
    @dpid       NVARCHAR(50)  OUTPUT,
    @idno       NVARCHAR(50)  OUTPUT,
    @opid       NVARCHAR(50)  OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT @RetuenMsg = N'False', @cnm = NULL, @dpid = NULL, @idno = NULL, @opid = NULL;

    DECLARE @Salt VARBINARY(16), @Hash VARBINARY(32), @EmployeeNo VARCHAR(20);

    SELECT @Salt = a.PasswordSalt, @Hash = a.PasswordHash, @EmployeeNo = a.SNTOPID
    FROM dbo.LoginAccount AS a
    WHERE a.Account = UPPER(LTRIM(RTRIM(@LogAccount))) AND a.IsActive = 1;

    IF @EmployeeNo IS NULL
       OR HASHBYTES('SHA2_256', @Salt + CONVERT(VARBINARY(400), @LogPASS)) <> @Hash
        RETURN;

    SELECT @RetuenMsg = N'True',
           @cnm  = u.CNM,
           @dpid = u.DPID,
           @opid = u.SNTOPID
    FROM dbo.UserInfo AS u
    WHERE u.SNTOPID = @EmployeeNo;
    -- @idno (national ID) is intentionally never returned by this mock.
END
GO

-- @TYPE = 'DP'  : department list
-- @TYPE = 'EMP' : employees, optionally filtered by @DPID
CREATE OR ALTER PROCEDURE dbo.GetEmployee
    @TYPE NVARCHAR(10),
    @DPID NVARCHAR(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF UPPER(@TYPE) = N'DP'
        SELECT DPID, MAX(DPCNM) AS DPCNM
        FROM dbo.UserInfo
        GROUP BY DPID
        ORDER BY DPID;
    ELSE IF UPPER(@TYPE) = N'EMP'
        SELECT SNTOPID, CNM, DPID, DPCNM, DUTNM
        FROM dbo.UserInfo
        WHERE @DPID IS NULL OR DPID = @DPID
        ORDER BY DPID, SNTOPID;
END
GO

/* ===================================================================== */
/*  LineMeeting                                                          */
/* ===================================================================== */
USE LineMeeting;
GO

IF OBJECT_ID(N'dbo.RoomInfo', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RoomInfo
    (
        RoomID   NVARCHAR(20)  NOT NULL CONSTRAINT PK_RoomInfo PRIMARY KEY,
        RoomName NVARCHAR(100) NOT NULL
    );
END
GO

-- MeetingNo format: yyyyMM + 4-digit serial, e.g. 2026110001 (generated by the app).
-- LocationType: 0 = none, 1 = room from RoomInfo, 2 = custom text.
-- QRCode: random token embedded in the meeting's QR code / LIFF URL.
IF OBJECT_ID(N'dbo.Meeting', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Meeting
    (
        MeetingNo                 VARCHAR(20)    NOT NULL CONSTRAINT PK_Meeting PRIMARY KEY,
        MeetingName               NVARCHAR(100)  NOT NULL,
        MeetingSubName            NVARCHAR(100)  NULL,
        StartDateTime             DATETIME2(0)   NOT NULL,
        EndDateTime               DATETIME2(0)   NOT NULL,
        LocationType              TINYINT        NOT NULL CONSTRAINT DF_Meeting_LocationType DEFAULT (0),
        RoomID                    NVARCHAR(100)  NULL,
        CustomLocation            NVARCHAR(200)  NULL,
        Host1EmployeeNo           VARCHAR(20)    NOT NULL,
        Host2EmployeeNo           VARCHAR(20)    NULL,
        Host3EmployeeNo           VARCHAR(20)    NULL,
        QRCode                    VARCHAR(100)   NOT NULL,
        Description               NVARCHAR(1000) NULL,
        AllowUnlistedParticipants BIT            NOT NULL CONSTRAINT DF_Meeting_AllowUnlisted DEFAULT (0),
        ExpectedParticipantCount  INT            NOT NULL CONSTRAINT DF_Meeting_Expected DEFAULT (0),
        CreatedByEmployeeNo       VARCHAR(20)    NOT NULL,
        CreatedTime               DATETIME2(0)   NOT NULL CONSTRAINT DF_Meeting_CreatedTime DEFAULT (SYSDATETIME()),
        UpdatedByEmployeeNo       VARCHAR(20)    NULL,
        UpdatedTime               DATETIME2(0)   NULL,
        IsDeleted                 BIT            NOT NULL CONSTRAINT DF_Meeting_IsDeleted DEFAULT (0),
        DeletedTime               DATETIME2(0)   NULL,
        DeletedByEmployeeNo       VARCHAR(20)    NULL,
        CONSTRAINT CK_Meeting_Time CHECK (EndDateTime > StartDateTime),
        CONSTRAINT CK_Meeting_LocationType CHECK (LocationType IN (0, 1, 2))
    );

    CREATE UNIQUE INDEX UX_Meeting_QRCode ON dbo.Meeting (QRCode);
    CREATE INDEX IX_Meeting_StartDateTime ON dbo.Meeting (StartDateTime);
END
GO

-- ParticipantType: 1 = employee (EmployeeNo), 2 = guest (External* columns).
-- For guests, CreatedByEmployeeNo holds an anonymous key derived from their LINE user ID.
IF OBJECT_ID(N'dbo.MeetingParticipant', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MeetingParticipant
    (
        ParticipantID        INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_MeetingParticipant PRIMARY KEY,
        MeetingNo            VARCHAR(20)    NOT NULL CONSTRAINT FK_MeetingParticipant_Meeting REFERENCES dbo.Meeting (MeetingNo),
        ParticipantType      TINYINT        NOT NULL,
        EmployeeNo           VARCHAR(20)    NULL,
        ExternalName         NVARCHAR(100)  NULL,
        ExternalOrganization NVARCHAR(200)  NULL,
        ExternalTitle        NVARCHAR(100)  NULL,
        CreatedByEmployeeNo  VARCHAR(20)    NOT NULL,
        CreatedTime          DATETIME2(0)   NOT NULL CONSTRAINT DF_MeetingParticipant_CreatedTime DEFAULT (SYSDATETIME()),
        UpdatedByEmployeeNo  VARCHAR(20)    NULL,
        UpdatedTime          DATETIME2(0)   NULL,
        CONSTRAINT CK_MeetingParticipant_Type CHECK (
            (ParticipantType = 1 AND EmployeeNo IS NOT NULL)
            OR (ParticipantType = 2 AND EmployeeNo IS NULL))
    );

    CREATE UNIQUE INDEX UX_MeetingParticipant_Employee
        ON dbo.MeetingParticipant (MeetingNo, EmployeeNo) WHERE ParticipantType = 1;
    CREATE INDEX IX_MeetingParticipant_Meeting ON dbo.MeetingParticipant (MeetingNo, ParticipantType);
END
GO

-- One row per participant who has signed in. A row is matched to its participant by
-- (MeetingNo, ParticipantType, EmployeeNo, ExternalName, ExternalOrganization).
-- ProxySignEmployeeNo is set when a colleague signed in on someone's behalf.
IF OBJECT_ID(N'dbo.MeetingAttendance', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MeetingAttendance
    (
        AttendanceID         INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_MeetingAttendance PRIMARY KEY,
        MeetingNo            VARCHAR(20)    NOT NULL CONSTRAINT FK_MeetingAttendance_Meeting REFERENCES dbo.Meeting (MeetingNo),
        ParticipantType      TINYINT        NOT NULL,
        EmployeeNo           VARCHAR(20)    NULL,
        ExternalName         NVARCHAR(100)  NULL,
        ExternalOrganization NVARCHAR(200)  NULL,
        SignInTime           DATETIME2(0)   NULL,
        SignOutTime          DATETIME2(0)   NULL,
        ProxySignEmployeeNo  VARCHAR(20)    NULL,
        Remark               NVARCHAR(500)  NULL,
        UpdatedByEmployeeNo  VARCHAR(20)    NULL,
        UpdatedTime          DATETIME2(0)   NULL,
        CONSTRAINT CK_MeetingAttendance_Time CHECK (SignOutTime IS NULL OR SignInTime IS NOT NULL)
    );

    CREATE INDEX IX_MeetingAttendance_Meeting ON dbo.MeetingAttendance (MeetingNo, ParticipantType, EmployeeNo);
END
GO

/* ===================================================================== */
/*  Application login (least privilege, instead of using sa)             */
/* ===================================================================== */
USE master;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'meeting_app')
    CREATE LOGIN meeting_app WITH PASSWORD = N'$(APP_DB_PASSWORD)', CHECK_POLICY = ON;
ELSE
    ALTER LOGIN meeting_app WITH PASSWORD = N'$(APP_DB_PASSWORD)';
GO

USE LineDB;
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'meeting_app')
    CREATE USER meeting_app FOR LOGIN meeting_app;
-- Read the directory and run the two procedures; no direct access to password hashes.
GRANT SELECT ON dbo.UserInfo TO meeting_app;
GRANT EXECUTE ON dbo.Login_Check TO meeting_app;
GRANT EXECUTE ON dbo.GetEmployee TO meeting_app;
GO

USE LineMeeting;
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'meeting_app')
    CREATE USER meeting_app FOR LOGIN meeting_app;
ALTER ROLE db_datareader ADD MEMBER meeting_app;
ALTER ROLE db_datawriter ADD MEMBER meeting_app;
GO
