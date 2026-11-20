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

-- 將 LINE 使用者代號綁定到員工編號。一個 LINE 帳號只能對應一位員工，
-- 因此改綁時先清除舊的對應。應用程式帳號對 dbo.UserInfo 只有 SELECT，
-- 所有寫入都經由這個預存程序（依 SQL Server 所有權鏈結取得權限）。
CREATE OR ALTER PROCEDURE dbo.BindLineUser
    @LineUserID NVARCHAR(100),
    @EmployeeNo VARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;
    -- 任何錯誤都讓整個交易回復，不會留下未結束的交易。
    SET XACT_ABORT ON;

    SET @LineUserID = NULLIF(LTRIM(RTRIM(@LineUserID)), N'');
    SET @EmployeeNo = NULLIF(LTRIM(RTRIM(@EmployeeNo)), '');

    IF @LineUserID IS NULL OR @EmployeeNo IS NULL
    BEGIN
        RAISERROR(N'BindLineUser: 缺少 LINE 使用者代號或員工編號。', 16, 1);
        RETURN;
    END

    DECLARE @matched INT;

    BEGIN TRANSACTION;

    UPDATE dbo.UserInfo
    SET UserID = NULL
    WHERE UserID = @LineUserID
      AND SNTOPID <> @EmployeeNo;

    UPDATE dbo.UserInfo
    SET UserID = @LineUserID
    WHERE SNTOPID = @EmployeeNo;

    SET @matched = @@ROWCOUNT;

    IF @matched = 0
    BEGIN
        ROLLBACK TRANSACTION;
        RAISERROR(N'BindLineUser: 查無此員工編號。', 16, 1);
        RETURN;
    END

    COMMIT TRANSACTION;
END
GO

/* ===================================================================== */
/*  LineMeeting                                                          */
/* ===================================================================== */
USE LineMeeting;
GO

-- This script only creates tables that do not exist yet; it never alters existing ones.
-- A database created by an older version (sign-ins matched by name, no ParticipantID)
-- would therefore stay unchanged and the app would fail later with "Invalid column name".
-- Stop here instead, with a message that says how to rebuild.
IF OBJECT_ID(N'dbo.Meeting', N'U') IS NOT NULL
   AND (COL_LENGTH(N'dbo.Meeting', N'RowVer') IS NULL OR OBJECT_ID(N'dbo.AppNow', N'FN') IS NULL)
    RAISERROR(N'LineMeeting uses an older table structure. Rebuild it with: docker compose down -v && docker compose up -d', 16, 1);
GO

-- 系統時間一律用台北時間，不受主機時區影響（部署到 UTC 的雲端主機也一樣）。
-- 程式端的 AppClock 用同一個時區，判斷簽到時間窗與寫入時間才不會差 8 小時。
-- 只在不存在時建立：資料表的預設值會參照這個函式，之後無法再 ALTER，
-- 所以不能用 CREATE OR ALTER，否則重複執行腳本時會失敗。
IF OBJECT_ID(N'dbo.AppNow', N'FN') IS NULL
    EXEC(N'CREATE FUNCTION dbo.AppNow()
RETURNS DATETIME2(0)
WITH SCHEMABINDING
AS
BEGIN
    RETURN CONVERT(DATETIME2(0), SYSDATETIMEOFFSET() AT TIME ZONE N''Taipei Standard Time'');
END');
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
-- ExpectedParticipantCount: number of invited employees (Source = 1) when the meeting was
--   last saved; 0 for open meetings, where no attendance rate is calculated.
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
        RoomID                    NVARCHAR(20)   NULL CONSTRAINT FK_Meeting_RoomInfo REFERENCES dbo.RoomInfo (RoomID),
        CustomLocation            NVARCHAR(200)  NULL,
        Host1EmployeeNo           VARCHAR(20)    NOT NULL,
        Host2EmployeeNo           VARCHAR(20)    NULL,
        Host3EmployeeNo           VARCHAR(20)    NULL,
        QRCode                    VARCHAR(100)   NOT NULL,
        Description               NVARCHAR(1000) NULL,
        AllowUnlistedParticipants BIT            NOT NULL CONSTRAINT DF_Meeting_AllowUnlisted DEFAULT (0),
        ExpectedParticipantCount  INT            NOT NULL CONSTRAINT DF_Meeting_Expected DEFAULT (0),
        CreatedByEmployeeNo       VARCHAR(20)    NOT NULL,
        CreatedTime               DATETIME2(0)   NOT NULL CONSTRAINT DF_Meeting_CreatedTime DEFAULT (dbo.AppNow()),
        UpdatedByEmployeeNo       VARCHAR(20)    NULL,
        UpdatedTime               DATETIME2(0)   NULL,
        IsDeleted                 BIT            NOT NULL CONSTRAINT DF_Meeting_IsDeleted DEFAULT (0),
        DeletedTime               DATETIME2(0)   NULL,
        DeletedByEmployeeNo       VARCHAR(20)    NULL,
        -- Changes automatically on every update. The edit page sends back the value it loaded;
        -- a different value means someone else saved in between, and the save is rejected.
        RowVer                    ROWVERSION     NOT NULL,
        CONSTRAINT CK_Meeting_Time CHECK (EndDateTime > StartDateTime),
        -- Each location type fills exactly the column it needs. A CHECK passes when it
        -- evaluates to UNKNOWN, so NULLs are excluded explicitly rather than via LEN().
        CONSTRAINT CK_Meeting_Location CHECK (
               (LocationType = 0 AND RoomID IS NULL     AND CustomLocation IS NULL)
            OR (LocationType = 1 AND RoomID IS NOT NULL AND CustomLocation IS NULL)
            OR (LocationType = 2 AND RoomID IS NULL     AND CustomLocation IS NOT NULL AND LEN(CustomLocation) > 0)),
        CONSTRAINT CK_Meeting_Expected CHECK (
            ExpectedParticipantCount >= 0
            AND (AllowUnlistedParticipants = 0 OR ExpectedParticipantCount = 0))
    );

    CREATE UNIQUE INDEX UX_Meeting_QRCode ON dbo.Meeting (QRCode);
    CREATE INDEX IX_Meeting_StartDateTime ON dbo.Meeting (StartDateTime);
END
GO

-- One row per person who belongs to a meeting, however they joined.
--   ParticipantType 1 = employee, identified by EmployeeNo
--   ParticipantType 2 = guest,    identified by GuestKey (a one-way hash of their LINE user ID)
--   Source 1 = invited by the organiser
--   Source 2 = employee who joined an open meeting on the spot
--   Source 3 = guest who registered themselves in an open meeting
-- Only Source = 1 counts toward the expected head count and the attendance rate.
IF OBJECT_ID(N'dbo.MeetingParticipant', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MeetingParticipant
    (
        ParticipantID        INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_MeetingParticipant PRIMARY KEY,
        MeetingNo            VARCHAR(20)    NOT NULL CONSTRAINT FK_MeetingParticipant_Meeting REFERENCES dbo.Meeting (MeetingNo),
        ParticipantType      TINYINT        NOT NULL,
        Source               TINYINT        NOT NULL,
        EmployeeNo           VARCHAR(20)    NULL,
        GuestKey             VARCHAR(20)    NULL,
        ExternalName         NVARCHAR(100)  NULL,
        ExternalOrganization NVARCHAR(200)  NULL,
        ExternalTitle        NVARCHAR(100)  NULL,
        -- Why the person did not attend. Kept here rather than on the sign-in record, because
        -- someone who did not attend has no sign-in record. Cleared when they sign in.
        AbsenceReason        NVARCHAR(500)  NULL,
        CreatedByEmployeeNo  VARCHAR(20)    NULL,   -- NULL when a guest registered themselves
        CreatedTime          DATETIME2(0)   NOT NULL CONSTRAINT DF_MeetingParticipant_CreatedTime DEFAULT (dbo.AppNow()),
        UpdatedByEmployeeNo  VARCHAR(20)    NULL,
        UpdatedTime          DATETIME2(0)   NULL,
        -- ParticipantType follows from Source; the check keeps the two consistent and makes
        -- sure each kind of row carries exactly the identity columns it needs.
        CONSTRAINT CK_MeetingParticipant_Kind CHECK (
               (ParticipantType = 1 AND Source IN (1, 2)
                AND EmployeeNo IS NOT NULL AND GuestKey IS NULL
                AND ExternalName IS NULL AND ExternalOrganization IS NULL AND ExternalTitle IS NULL)
            OR (ParticipantType = 2 AND Source = 3
                AND EmployeeNo IS NULL AND GuestKey IS NOT NULL
                AND ExternalName IS NOT NULL AND LEN(ExternalName) > 0
                AND ExternalOrganization IS NOT NULL AND LEN(ExternalOrganization) > 0))
    );

    -- An employee appears at most once per meeting; so does a guest.
    CREATE UNIQUE INDEX UX_MeetingParticipant_Employee
        ON dbo.MeetingParticipant (MeetingNo, EmployeeNo) WHERE EmployeeNo IS NOT NULL;
    CREATE UNIQUE INDEX UX_MeetingParticipant_Guest
        ON dbo.MeetingParticipant (MeetingNo, GuestKey) WHERE GuestKey IS NOT NULL;
    CREATE INDEX IX_MeetingParticipant_Meeting ON dbo.MeetingParticipant (MeetingNo, ParticipantType);
END
GO

-- Sign-in record of a participant: at most one per participant, and only for someone
-- who is in MeetingParticipant. Name, department, meeting etc. come from that row.
-- ProxySignEmployeeNo is set when a colleague signed in on someone's behalf.
IF OBJECT_ID(N'dbo.MeetingAttendance', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MeetingAttendance
    (
        ParticipantID        INT            NOT NULL
            CONSTRAINT PK_MeetingAttendance PRIMARY KEY
            CONSTRAINT FK_MeetingAttendance_Participant REFERENCES dbo.MeetingParticipant (ParticipantID),
        SignInTime           DATETIME2(0)   NOT NULL,
        SignOutTime          DATETIME2(0)   NULL,
        ProxySignEmployeeNo  VARCHAR(20)    NULL,
        UpdatedByEmployeeNo  VARCHAR(20)    NULL,   -- NULL when a guest signed in or out themselves
        UpdatedTime          DATETIME2(0)   NULL,
        CONSTRAINT CK_MeetingAttendance_Time CHECK (SignOutTime IS NULL OR SignOutTime >= SignInTime)
    );
END
GO

-- History of every change to MeetingAttendance: sign-in, sign-out, proxy sign-in, and edits or
-- deletions made on the attendance sheet. Written only by the trigger below, so no code path can
-- forget to log, and the app login is denied direct writes so the history cannot be altered.
-- ParticipantID has no foreign key on purpose: the history outlives the rows it describes.
IF OBJECT_ID(N'dbo.MeetingAttendanceLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MeetingAttendanceLog
    (
        LogID                   BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_MeetingAttendanceLog PRIMARY KEY,
        ParticipantID           INT            NOT NULL,
        Action                  CHAR(1)        NOT NULL,   -- I = sign-in created, U = changed, D = removed
        OldSignInTime           DATETIME2(0)   NULL,
        OldSignOutTime          DATETIME2(0)   NULL,
        OldProxySignEmployeeNo  VARCHAR(20)    NULL,
        NewSignInTime           DATETIME2(0)   NULL,
        NewSignOutTime          DATETIME2(0)   NULL,
        NewProxySignEmployeeNo  VARCHAR(20)    NULL,
        ChangedByEmployeeNo     VARCHAR(20)    NULL,       -- NULL when a guest acted on their own record
        ChangeSource            NVARCHAR(20)   NULL,       -- mobile / proxy / sheet, set by the app
        ChangedTime             DATETIME2(0)   NOT NULL CONSTRAINT DF_MeetingAttendanceLog_ChangedTime DEFAULT (dbo.AppNow()),
        CONSTRAINT CK_MeetingAttendanceLog_Action CHECK (Action IN ('I', 'U', 'D'))
    );

    CREATE INDEX IX_MeetingAttendanceLog_Participant ON dbo.MeetingAttendanceLog (ParticipantID, ChangedTime);
END
GO

-- Who made the change and through which screen come from SESSION_CONTEXT, which the app sets at
-- the start of each transaction that writes sign-in records (sp_set_session_context).
CREATE OR ALTER TRIGGER dbo.TR_MeetingAttendance_Log
ON dbo.MeetingAttendance
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ChangedBy VARCHAR(20)  = CONVERT(VARCHAR(20),  SESSION_CONTEXT(N'EmployeeNo'));
    DECLARE @Source    NVARCHAR(20) = CONVERT(NVARCHAR(20), SESSION_CONTEXT(N'ChangeSource'));

    INSERT dbo.MeetingAttendanceLog
        (ParticipantID, Action,
         OldSignInTime, OldSignOutTime, OldProxySignEmployeeNo,
         NewSignInTime, NewSignOutTime, NewProxySignEmployeeNo,
         ChangedByEmployeeNo, ChangeSource)
    SELECT
        COALESCE(i.ParticipantID, d.ParticipantID),
        CASE WHEN d.ParticipantID IS NULL THEN 'I' WHEN i.ParticipantID IS NULL THEN 'D' ELSE 'U' END,
        d.SignInTime, d.SignOutTime, d.ProxySignEmployeeNo,
        i.SignInTime, i.SignOutTime, i.ProxySignEmployeeNo,
        @ChangedBy, @Source
    FROM inserted AS i
    FULL OUTER JOIN deleted AS d ON d.ParticipantID = i.ParticipantID;   -- the primary key never changes
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
GRANT EXECUTE ON dbo.BindLineUser TO meeting_app;
GO

USE LineMeeting;
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'meeting_app')
    CREATE USER meeting_app FOR LOGIN meeting_app;
ALTER ROLE db_datareader ADD MEMBER meeting_app;
ALTER ROLE db_datawriter ADD MEMBER meeting_app;
-- Scalar functions need EXECUTE separately; the app uses dbo.AppNow() for every timestamp.
GRANT EXECUTE ON dbo.AppNow TO meeting_app;
-- The history is written only by the trigger (ownership chaining skips this check), so the app
-- login itself cannot insert, change or delete history rows.
DENY INSERT, UPDATE, DELETE ON dbo.MeetingAttendanceLog TO meeting_app;
GO
