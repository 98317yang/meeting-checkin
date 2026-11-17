/*
  Meeting check-in system: demo data

  All people, departments and rooms below are fictional.
  Employees and rooms are added only if missing; demo meetings are created only when
  the meeting table is empty. Re-running the script is safe and keeps your own data.

  Demo logins (account / password):
    DEMO01 / Demo@1234   王小明, Information Office (use this one to create meetings)
    DEMO02 / Demo@1234   林雅婷, Nursing Department
    DEMO03 / Demo@1234   陳志豪, Internal Medicine
*/

SET NOCOUNT ON;
-- Required for filtered indexes; sqlcmd turns QUOTED_IDENTIFIER off by default.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

USE LineDB;
GO

-- Employees: rows that already exist (by SNTOPID) are skipped, so re-running only adds new ones.
INSERT dbo.UserInfo (SNTOPID, CNM, DPID, DPCNM, DUTNM)
SELECT v.SNTOPID, v.CNM, v.DPID, v.DPCNM, v.DUTNM
FROM (VALUES
        ('10001', N'王小明', N'IT01', N'資訊室', N'工程師'),
        ('10002', N'李佳穎', N'IT01', N'資訊室', N'主任'),
        ('10003', N'張家豪', N'IT01', N'資訊室', N'系統分析師'),
        ('20001', N'林雅婷', N'NR01', N'護理部', N'護理長'),
        ('20002', N'黃怡君', N'NR01', N'護理部', N'護理師'),
        ('20003', N'吳淑芬', N'NR01', N'護理部', N'護理師'),
        ('30001', N'陳志豪', N'IM01', N'內科部', N'主治醫師'),
        ('30002', N'劉建宏', N'IM01', N'內科部', N'住院醫師'),
        ('40001', N'蔡美玲', N'AD01', N'行政室', N'專員'),
        ('40002', N'鄭宇翔', N'AD01', N'行政室', N'組長'),
        ('50001', N'許靜宜', N'PH01', N'藥劑部', N'藥師'),
        ('50002', N'周冠廷', N'PH01', N'藥劑部', N'藥師'),
        ('60001', N'趙建宏', N'IT01', N'資訊室', N'工程師'),
        ('60002', N'吳冠廷', N'IT01', N'資訊室', N'網管工程師'),
        ('60003', N'許筱涵', N'IT01', N'資訊室', N'網管工程師'),
        ('60004', N'鄭國華', N'IT01', N'資訊室', N'工程師'),
        ('60005', N'陳哲維', N'NR01', N'護理部', N'護理師'),
        ('60006', N'蔡正雄', N'NR01', N'護理部', N'護理師'),
        ('60007', N'魏偉倫', N'NR01', N'護理部', N'專科護理師'),
        ('60008', N'徐明哲', N'NR01', N'護理部', N'護理師'),
        ('60009', N'楊政諺', N'NR01', N'護理部', N'副護理長'),
        ('60010', N'翁育誠', N'NR01', N'護理部', N'護理師'),
        ('60011', N'羅靜怡', N'NR01', N'護理部', N'副護理長'),
        ('60012', N'李心怡', N'NR01', N'護理部', N'副護理長'),
        ('60013', N'鍾彥廷', N'NR01', N'護理部', N'副護理長'),
        ('60014', N'黃佩珊', N'IM01', N'內科部', N'部主任'),
        ('60015', N'林麗娟', N'IM01', N'內科部', N'主治醫師'),
        ('60016', N'林郁婷', N'IM01', N'內科部', N'主治醫師'),
        ('60017', N'葉明哲', N'IM01', N'內科部', N'主治醫師'),
        ('60018', N'江宜蓁', N'IM01', N'內科部', N'護理師'),
        ('60019', N'胡欣妤', N'IM01', N'內科部', N'護理師'),
        ('60020', N'李筱涵', N'IM01', N'內科部', N'住院醫師'),
        ('60021', N'林柏翰', N'SG01', N'外科部', N'部主任'),
        ('60022', N'江文彬', N'SG01', N'外科部', N'住院醫師'),
        ('60023', N'高承翰', N'SG01', N'外科部', N'主治醫師'),
        ('60024', N'劉建志', N'SG01', N'外科部', N'住院醫師'),
        ('60025', N'黃文彬', N'SG01', N'外科部', N'住院醫師'),
        ('60026', N'蕭偉倫', N'SG01', N'外科部', N'護理師'),
        ('60027', N'楊建志', N'SG01', N'外科部', N'主治醫師'),
        ('60028', N'許欣妤', N'SG01', N'外科部', N'專科護理師'),
        ('60029', N'陳依婷', N'ER01', N'急診醫學科', N'科主任'),
        ('60030', N'謝彥廷', N'ER01', N'急診醫學科', N'住院醫師'),
        ('60031', N'鄭晏如', N'ER01', N'急診醫學科', N'住院醫師'),
        ('60032', N'詹怡君', N'ER01', N'急診醫學科', N'主治醫師'),
        ('60033', N'梁麗華', N'ER01', N'急診醫學科', N'住院醫師'),
        ('60034', N'鍾明哲', N'ER01', N'急診醫學科', N'專科護理師'),
        ('60035', N'趙信宏', N'ER01', N'急診醫學科', N'主治醫師'),
        ('60036', N'盧心怡', N'PD01', N'小兒科', N'科主任'),
        ('60037', N'黃偉倫', N'PD01', N'小兒科', N'住院醫師'),
        ('60038', N'沈明哲', N'PD01', N'小兒科', N'住院醫師'),
        ('60039', N'林宥均', N'PD01', N'小兒科', N'護理師'),
        ('60040', N'彭宗翰', N'PD01', N'小兒科', N'主治醫師'),
        ('60041', N'鄭麗華', N'PD01', N'小兒科', N'專科護理師'),
        ('60042', N'林若涵', N'OB01', N'婦產科', N'科主任'),
        ('60043', N'謝俊宏', N'OB01', N'婦產科', N'護理師'),
        ('60044', N'羅子豪', N'OB01', N'婦產科', N'住院醫師'),
        ('60045', N'趙美君', N'OB01', N'婦產科', N'住院醫師'),
        ('60046', N'林文雄', N'OB01', N'婦產科', N'專科護理師'),
        ('60047', N'王建宏', N'OB01', N'婦產科', N'主治醫師'),
        ('60048', N'趙柏翰', N'RD01', N'放射科', N'科主任'),
        ('60049', N'高文雄', N'RD01', N'放射科', N'放射師'),
        ('60050', N'趙怡安', N'RD01', N'放射科', N'放射師'),
        ('60051', N'廖育誠', N'RD01', N'放射科', N'放射師'),
        ('60052', N'趙俊傑', N'RD01', N'放射科', N'放射師'),
        ('60053', N'楊芷瑄', N'RD01', N'放射科', N'主治醫師'),
        ('60054', N'簡冠廷', N'LB01', N'檢驗科', N'組長'),
        ('60055', N'梁品妍', N'LB01', N'檢驗科', N'醫檢師'),
        ('60056', N'陳志明', N'LB01', N'檢驗科', N'醫檢師'),
        ('60057', N'楊國華', N'LB01', N'檢驗科', N'醫檢師'),
        ('60058', N'趙淑芬', N'LB01', N'檢驗科', N'醫檢師'),
        ('60059', N'洪惠雯', N'LB01', N'檢驗科', N'醫檢師'),
        ('60060', N'洪品睿', N'PH01', N'藥劑部', N'部主任'),
        ('60061', N'洪麗娟', N'PH01', N'藥劑部', N'藥師'),
        ('60062', N'曾宗翰', N'PH01', N'藥劑部', N'臨床藥師'),
        ('60063', N'胡冠廷', N'PH01', N'藥劑部', N'臨床藥師'),
        ('60064', N'呂皓宇', N'PH01', N'藥劑部', N'藥師'),
        ('60065', N'黃家豪', N'RH01', N'復健科', N'科主任'),
        ('60066', N'楊珮瑜', N'RH01', N'復健科', N'職能治療師'),
        ('60067', N'劉文彬', N'RH01', N'復健科', N'語言治療師'),
        ('60068', N'江信宏', N'RH01', N'復健科', N'職能治療師'),
        ('60069', N'許美君', N'RH01', N'復健科', N'物理治療師'),
        ('60070', N'陳美慧', N'RH01', N'復健科', N'主治醫師'),
        ('60071', N'邱詩涵', N'NU01', N'營養科', N'組長'),
        ('60072', N'葉嘉偉', N'NU01', N'營養科', N'營養師'),
        ('60073', N'何文彬', N'NU01', N'營養科', N'營養師'),
        ('60074', N'楊凱翔', N'NU01', N'營養科', N'營養師'),
        ('60075', N'王昱辰', N'SW01', N'社工室', N'主任'),
        ('60076', N'劉宗翰', N'SW01', N'社工室', N'社工師'),
        ('60077', N'黃明哲', N'SW01', N'社工室', N'社工師'),
        ('60078', N'余麗華', N'SW01', N'社工室', N'社工師'),
        ('60079', N'蘇昱辰', N'HR01', N'人力資源室', N'主任'),
        ('60080', N'王偉倫', N'HR01', N'人力資源室', N'專員'),
        ('60081', N'蘇淑貞', N'HR01', N'人力資源室', N'組長'),
        ('60082', N'林家豪', N'HR01', N'人力資源室', N'專員'),
        ('60083', N'羅曉雯', N'HR01', N'人力資源室', N'專員'),
        ('60084', N'曾雅琪', N'AC01', N'會計室', N'主任'),
        ('60085', N'吳以琳', N'AC01', N'會計室', N'專員'),
        ('60086', N'王嘉偉', N'AC01', N'會計室', N'管理師'),
        ('60087', N'楊玉婷', N'AC01', N'會計室', N'管理師'),
        ('60088', N'楊柏宇', N'GA01', N'總務室', N'主任'),
        ('60089', N'劉美慧', N'GA01', N'總務室', N'管理師'),
        ('60090', N'莊振宇', N'GA01', N'總務室', N'管理師'),
        ('60091', N'詹建志', N'GA01', N'總務室', N'管理師'),
        ('60092', N'魏靜怡', N'GA01', N'總務室', N'管理師'),
        ('60093', N'徐秀英', N'AD01', N'行政室', N'室主任'),
        ('60094', N'蔡品睿', N'AD01', N'行政室', N'管理師'),
        ('60095', N'沈家豪', N'AD01', N'行政室', N'組長'),
        ('60096', N'廖孟璇', N'AD01', N'行政室', N'專員'),
        ('60097', N'游信宏', N'QM01', N'品質管理中心', N'主任'),
        ('60098', N'黃書豪', N'QM01', N'品質管理中心', N'專員'),
        ('60099', N'游雅琪', N'QM01', N'品質管理中心', N'專員'),
        ('60100', N'陳晏如', N'QM01', N'品質管理中心', N'專員'),
        ('60101', N'盧子豪', N'IC01', N'感染管制中心', N'主任'),
        ('60102', N'黃欣怡', N'IC01', N'感染管制中心', N'感管師'),
        ('60103', N'鍾翊婷', N'IC01', N'感染管制中心', N'感管護理師'),
        ('60104', N'翁雅雯', N'IC01', N'感染管制中心', N'感管師'),
        ('60105', N'陳信宏', N'ED01', N'教學研究部', N'主任'),
        ('60106', N'郭欣怡', N'ED01', N'教學研究部', N'研究助理'),
        ('60107', N'羅美慧', N'ED01', N'教學研究部', N'研究助理'),
        ('60108', N'莊嘉偉', N'ED01', N'教學研究部', N'專員')
     ) AS v (SNTOPID, CNM, DPID, DPCNM, DUTNM)
WHERE NOT EXISTS (SELECT 1 FROM dbo.UserInfo AS u WHERE u.SNTOPID = v.SNTOPID);

PRINT CONCAT('LineDB: ', @@ROWCOUNT, ' employee(s) added.');

IF NOT EXISTS (SELECT 1 FROM dbo.LoginAccount WHERE Account = N'DEMO01')
    EXEC dbo.SetLoginPassword @Account = N'DEMO01', @SNTOPID = '10001', @Password = N'Demo@1234';
IF NOT EXISTS (SELECT 1 FROM dbo.LoginAccount WHERE Account = N'DEMO02')
    EXEC dbo.SetLoginPassword @Account = N'DEMO02', @SNTOPID = '20001', @Password = N'Demo@1234';
IF NOT EXISTS (SELECT 1 FROM dbo.LoginAccount WHERE Account = N'DEMO03')
    EXEC dbo.SetLoginPassword @Account = N'DEMO03', @SNTOPID = '30001', @Password = N'Demo@1234';
GO

USE LineMeeting;
GO

INSERT dbo.RoomInfo (RoomID, RoomName)
SELECT v.RoomID, v.RoomName
FROM (VALUES
        (N'R101', N'第一會議室（行政大樓 1F）'),
        (N'R201', N'第二會議室（行政大樓 2F）'),
        (N'R305', N'教學研究室（醫療大樓 3F）'),
        (N'R801', N'國際會議廳（醫療大樓 8F）'),
        (N'R202', N'第三會議室（行政大樓 2F）'),
        (N'R310', N'護理部討論室（醫療大樓 3F）'),
        (N'R502', N'內科部討論室（醫療大樓 5F）'),
        (N'R601', N'外科部討論室（醫療大樓 6F）'),
        (N'B101', N'視訊會議室（第二大樓 1F）'),
        (N'B305', N'教育訓練教室（第二大樓 3F）')
     ) AS v (RoomID, RoomName)
WHERE NOT EXISTS (SELECT 1 FROM dbo.RoomInfo AS r WHERE r.RoomID = v.RoomID);

PRINT CONCAT('LineMeeting: ', @@ROWCOUNT, ' room(s) added.');
GO

-- Three demo meetings relative to the day you run this script:
--   1) today, now-1h to now+1h  -> sign-in is open right away
--   2) yesterday, finished      -> shows a completed attendance sheet
--   3) next week                -> upcoming, open to unlisted participants
IF NOT EXISTS (SELECT 1 FROM dbo.Meeting)
BEGIN
    DECLARE @Now      DATETIME2(0) = SYSDATETIME();
    DECLARE @Today    DATETIME2(0) = DATEADD(MINUTE, -60, @Now);
    DECLARE @Yday     DATETIME2(0) = DATEADD(HOUR, 14, CAST(CAST(DATEADD(DAY, -1, @Now) AS DATE) AS DATETIME2(0)));
    DECLARE @NextWeek DATETIME2(0) = DATEADD(HOUR, 10, CAST(CAST(DATEADD(DAY, 7, @Now) AS DATE) AS DATETIME2(0)));

    -- MeetingNo = yyyyMM of the start time + 4-digit serial, same format the app uses.
    -- The app numbers new meetings from the month's highest serial, so these do not collide.
    DECLARE @M1 VARCHAR(20) = CONVERT(CHAR(6), @Today, 112) + '0001';
    DECLARE @M2 VARCHAR(20) = CONVERT(CHAR(6), @Yday, 112) + '0002';
    DECLARE @M3 VARCHAR(20) = CONVERT(CHAR(6), @NextWeek, 112) + '0003';
    DECLARE @Yday2    DATETIME2(0) = DATEADD(HOUR, 3, @Yday);
    DECLARE @M4 VARCHAR(20) = CONVERT(CHAR(6), @Yday2, 112) + '0004';

    INSERT dbo.Meeting
        (MeetingNo, MeetingName, MeetingSubName, StartDateTime, EndDateTime,
         LocationType, RoomID, CustomLocation, Host1EmployeeNo, Host2EmployeeNo, Host3EmployeeNo,
         QRCode, Description, AllowUnlistedParticipants, ExpectedParticipantCount, CreatedByEmployeeNo)
    VALUES
        (@M1, N'資訊安全教育訓練', N'第 3 次', @Today, DATEADD(HOUR, 2, @Today),
         1, N'R201', NULL, '10001', '10002', NULL,
         'M-' + LOWER(REPLACE(CONVERT(VARCHAR(36), NEWID()), '-', '')),
         N'示範資料：固定名單會議，可測試本人簽到與代簽。', 0, 6, '10001'),
        (@M2, N'跨部門流程檢討會議', NULL, @Yday, DATEADD(MINUTE, 90, @Yday),
         2, NULL, N'線上會議', '10001', NULL, NULL,
         'M-' + LOWER(REPLACE(CONVERT(VARCHAR(36), NEWID()), '-', '')),
         N'示範資料：已結束的會議，可查看簽到單與匯出。', 0, 5, '10001'),
        (@M3, N'新進人員系統說明會', NULL, @NextWeek, DATEADD(HOUR, 1, @NextWeek),
         1, N'R801', NULL, '10002', NULL, NULL,
         'M-' + LOWER(REPLACE(CONVERT(VARCHAR(36), NEWID()), '-', '')),
         N'示範資料：開放名單外人員與訪客加入。', 1, 30, '10002'),
        (@M4, N'院內健康講座', NULL, @Yday2, DATEADD(HOUR, 1, @Yday2),
         1, N'B305', NULL, '10001', NULL, NULL,
         'M-' + LOWER(REPLACE(CONVERT(VARCHAR(36), NEWID()), '-', '')),
         N'示範資料：已結束的開放式會議，有名單外人員與訪客簽到，不計算出席率。', 1, 0, '10001');

    INSERT dbo.MeetingParticipant (MeetingNo, ParticipantType, EmployeeNo, CreatedByEmployeeNo)
    SELECT m.MeetingNo, 1, p.EmployeeNo, m.CreatedByEmployeeNo
    FROM (VALUES
            (@M1, '10001'), (@M1, '10002'), (@M1, '20001'), (@M1, '20002'), (@M1, '30001'), (@M1, '40001'),
            (@M2, '10001'), (@M2, '20001'), (@M2, '30001'), (@M2, '40002'), (@M2, '50001'),
            (@M3, '10002'), (@M3, '10003'),
            (@M4, '10001'), (@M4, '60001')
         ) AS p (MeetingNo, EmployeeNo)
    JOIN dbo.Meeting AS m ON m.MeetingNo = p.MeetingNo;

    -- Open meeting: one guest who registered with their LINE account (guests only exist in open meetings).
    INSERT dbo.MeetingParticipant
        (MeetingNo, ParticipantType, EmployeeNo, ExternalName, ExternalOrganization, ExternalTitle, CreatedByEmployeeNo)
    VALUES (@M4, 2, NULL, N'趙子琳', N'示範顧問公司', N'顧問', 'G0000000000000demo1');

    -- Yesterday: most people signed in/out; 30001 was signed in by 20001 (proxy); 40002 absent.
    INSERT dbo.MeetingAttendance
        (MeetingNo, ParticipantType, EmployeeNo, ExternalName, ExternalOrganization,
         SignInTime, SignOutTime, ProxySignEmployeeNo, UpdatedByEmployeeNo, UpdatedTime)
    VALUES
        (@M2, 1, '10001', NULL, NULL, DATEADD(MINUTE, -5, @Yday), DATEADD(MINUTE, 92, @Yday), NULL,    '10001', @Yday),
        (@M2, 1, '20001', NULL, NULL, DATEADD(MINUTE, -2, @Yday), DATEADD(MINUTE, 91, @Yday), NULL,    '20001', @Yday),
        (@M2, 1, '30001', NULL, NULL, DATEADD(MINUTE,  1, @Yday), DATEADD(MINUTE, 91, @Yday), '20001', '20001', @Yday),
        (@M2, 1, '50001', NULL, NULL, DATEADD(MINUTE,  8, @Yday), DATEADD(MINUTE, 90, @Yday), NULL,    '50001', @Yday),
        (@M4, 1, '10001', NULL, NULL, DATEADD(MINUTE, -3, @Yday2), DATEADD(MINUTE, 60, @Yday2), NULL, '10001', @Yday2),
        (@M4, 2, NULL, N'趙子琳', N'示範顧問公司', DATEADD(MINUTE, 2, @Yday2), DATEADD(MINUTE, 58, @Yday2), NULL,
         'G0000000000000demo1', @Yday2);

    -- Today: two people have already signed in.
    INSERT dbo.MeetingAttendance
        (MeetingNo, ParticipantType, EmployeeNo, SignInTime, UpdatedByEmployeeNo, UpdatedTime)
    VALUES
        (@M1, 1, '10002', DATEADD(MINUTE, 2, @Today), '10002', DATEADD(MINUTE, 2, @Today)),
        (@M1, 1, '20001', DATEADD(MINUTE, 5, @Today), '20001', DATEADD(MINUTE, 5, @Today));

    PRINT 'LineMeeting: demo meetings created.';
END
ELSE
    PRINT 'LineMeeting: meetings already present, skipped.';
GO
