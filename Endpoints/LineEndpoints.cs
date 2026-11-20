namespace MeetingCheckin;

internal static class LineEndpoints
{
    public static void MapLineEndpoints(this WebApplication app)
    {
        // 前端初始化 LIFF 需要 LIFF ID。它會出現在網頁原始碼中，本來就不是機密；
        // Channel secret 不在此提供，也不應放進前端。
        app.MapGet("/api/liff/config", (IConfiguration configuration) =>
        {
            var liffId = configuration["LineLiff:LiffId"];
            return string.IsNullOrWhiteSpace(liffId)
                ? Results.Json(new { success = false, code = "LIFF_NOT_CONFIGURED", message = "尚未設定 LineLiff:LiffId。" }, statusCode: 503)
                : Results.Ok(new { success = true, liffId = liffId.Trim() });
        }).AllowAnonymous();

        // 首次使用 LINE 簽到時，以院內帳號密碼將 LINE 身分綁定到員工編號。
        // 僅憑員工編號即可綁定會讓任何人冒用他人身分，因此這裡要求通過登入驗證。
        app.MapPost("/api/liff/bind", async (
            LiffBindRequest request,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration) =>
        {
            var verification = await VerifyLineIdToken(httpClientFactory, configuration, request.IdToken);
            if (!verification.Ok)
                return Results.Json(new { success = false, code = "INVALID_ID_TOKEN", message = verification.Error }, statusCode: 401);

            var account = request.Account?.Trim().ToUpperInvariant() ?? "";
            var password = request.Password ?? "";
            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(password))
                return Results.BadRequest(new { success = false, message = "請輸入院內帳號與密碼。" });
            if (LoginThrottle.IsLocked(account))
                return LoginThrottle.LockedResult();

            await using var connection = new SqlConnection(GetConnection(configuration, "LineDb"));
            await connection.OpenAsync();

            string employeeNo;
            await using (var loginCommand = new SqlCommand("dbo.Login_Check", connection) { CommandType = CommandType.StoredProcedure })
            {
                loginCommand.Parameters.Add("@LogAccount", SqlDbType.NVarChar, 100).Value = account;
                loginCommand.Parameters.Add("@LogPASS", SqlDbType.NVarChar, 200).Value = password;
                var messageParameter = new SqlParameter("@RetuenMsg", SqlDbType.NVarChar, 500) { Direction = ParameterDirection.Output };
                var nameParameter = new SqlParameter("@cnm", SqlDbType.NVarChar, 100) { Direction = ParameterDirection.Output };
                var departmentParameter = new SqlParameter("@dpid", SqlDbType.NVarChar, 50) { Direction = ParameterDirection.Output };
                var idnoParameter = new SqlParameter("@idno", SqlDbType.NVarChar, 50) { Direction = ParameterDirection.Output };
                var operatorParameter = new SqlParameter("@opid", SqlDbType.NVarChar, 50) { Direction = ParameterDirection.Output };
                loginCommand.Parameters.AddRange([messageParameter, nameParameter, departmentParameter, idnoParameter, operatorParameter]);
                await loginCommand.ExecuteNonQueryAsync();

                if (!Output(messageParameter).StartsWith("True", StringComparison.OrdinalIgnoreCase))
                {
                    LoginThrottle.RecordFailure(account);
                    return Results.Json(new { success = false, message = "帳號或密碼輸入錯誤。" }, statusCode: 401);
                }
                LoginThrottle.Reset(account);
                employeeNo = Output(operatorParameter);
            }

            if (string.IsNullOrWhiteSpace(employeeNo))
                return Results.Json(new { success = false, message = "登入成功，但未取得員工編號。" }, statusCode: 500);

            // 一個 LINE 帳號只能綁定一位員工，改綁與清除舊對應都在 dbo.BindLineUser 內以交易完成。
            // 應用程式帳號對 dbo.UserInfo 只有 SELECT，寫入一律經由預存程序。
            try
            {
                await using var bindCommand = new SqlCommand("dbo.BindLineUser", connection) { CommandType = CommandType.StoredProcedure };
                bindCommand.Parameters.Add("@LineUserID", SqlDbType.NVarChar, 100).Value = verification.LineUserId;
                bindCommand.Parameters.Add("@EmployeeNo", SqlDbType.VarChar, 20).Value = employeeNo;
                await bindCommand.ExecuteNonQueryAsync();
            }
            catch (SqlException exception)
            {
                app.Logger.LogError(exception, "綁定 LINE 帳號失敗，員工編號 {EmployeeNo}", employeeNo);
                return Results.Json(new { success = false, message = "綁定失敗，請稍後再試或聯絡系統管理員。" }, statusCode: 500);
            }

            return Results.Ok(new { success = true, employeeNo, message = "已完成綁定。" });
        }).AllowAnonymous();

        // 身分一律由後端向 LINE 驗證 ID Token 取得，網址不再夾帶使用者代號。
        // 取得的 LINE 使用者代號對應 LineDB.dbo.UserInfo.UserID，不是員工編號 SNTOPID。
        app.MapPost("/api/liff/meeting/authorize", async (
            LiffMeetingAuthorizeRequest request,
            IDataProtectionProvider dataProtectionProvider,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration) =>
        {
            if (!string.Equals(request.Type?.Trim(), "meeting", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { success = false, code = "INVALID_TYPE", message = "不支援的會議入口類型。" });
            if (string.IsNullOrWhiteSpace(request.MeetingToken))
                return Results.BadRequest(new { success = false, code = "MISSING_MEETING_TOKEN", message = "缺少會議 Token。" });
            var verification = await VerifyLineIdToken(httpClientFactory, configuration, request.IdToken);
            if (!verification.Ok)
                return Results.Json(new { success = false, code = "INVALID_ID_TOKEN", message = verification.Error }, statusCode: 401);

            var effectiveUserId = verification.LineUserId;

            string employeeNo = "";
            string employeeName = "";
            string departmentId = "";
            string departmentName = "";
            string dutyName = "";
            var linkedInternalUser = false;

            await using (var lineConnection = new SqlConnection(GetConnection(configuration, "LineDb")))
            {
                await lineConnection.OpenAsync();
                const string userSql = """
                    SELECT TOP (1)
                        LTRIM(RTRIM(SNTOPID)) AS EmployeeNo,
                        LTRIM(RTRIM(CNM)) AS EmployeeName,
                        ISNULL(LTRIM(RTRIM(DPID)), N'') AS DepartmentID,
                        ISNULL(LTRIM(RTRIM(DPCNM)), N'') AS DepartmentName,
                        ISNULL(LTRIM(RTRIM(DUTNM)), N'') AS DutyName
                    FROM dbo.UserInfo
                    WHERE UserID = @LineUserID;
                    """;
                await using var userCommand = new SqlCommand(userSql, lineConnection);
                userCommand.Parameters.Add("@LineUserID", SqlDbType.NVarChar, 100).Value = effectiveUserId;
                await using var userReader = await userCommand.ExecuteReaderAsync();
                if (await userReader.ReadAsync())
                {
                    linkedInternalUser = true;
                    employeeNo = userReader["EmployeeNo"].ToString()?.Trim() ?? "";
                    employeeName = userReader["EmployeeName"].ToString()?.Trim() ?? "";
                    departmentId = userReader["DepartmentID"].ToString()?.Trim() ?? "";
                    departmentName = userReader["DepartmentName"].ToString()?.Trim() ?? "";
                    dutyName = userReader["DutyName"].ToString()?.Trim() ?? "";
                }
            }

            await using var meetingConnection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
            await meetingConnection.OpenAsync();
            const string meetingSql = """
                SELECT TOP (1)
                    m.MeetingNo, m.MeetingName, m.MeetingSubName,
                    m.StartDateTime, m.EndDateTime,
                    m.LocationType, m.RoomID, m.CustomLocation,
                    m.Host1EmployeeNo, m.Host2EmployeeNo, m.Host3EmployeeNo,
                    m.CreatedByEmployeeNo, m.Description,
                    ISNULL(m.AllowUnlistedParticipants, 0) AS AllowUnlistedParticipants,
                    ISNULL(m.ExpectedParticipantCount, 0) AS ExpectedParticipantCount,
                    CASE
                        WHEN NULLIF(@EmployeeNo, '') IS NOT NULL AND
                             (@EmployeeNo = m.CreatedByEmployeeNo
                              OR @EmployeeNo IN (m.Host1EmployeeNo, m.Host2EmployeeNo, m.Host3EmployeeNo)) THEN N'host'
                        WHEN NULLIF(@EmployeeNo, '') IS NOT NULL AND EXISTS (
                            SELECT 1 FROM dbo.MeetingParticipant p
                            WHERE p.MeetingNo = m.MeetingNo
                              AND p.ParticipantType = 1
                              AND p.EmployeeNo = @EmployeeNo
                        ) THEN N'participant'
                        ELSE N'none'
                    END AS Relation
                FROM dbo.Meeting m
                WHERE m.QRCode = @Token
                  AND ISNULL(m.IsDeleted, 0) = 0;
                """;
            await using var meetingCommand = new SqlCommand(meetingSql, meetingConnection);
            meetingCommand.Parameters.Add("@Token", SqlDbType.VarChar, 100).Value = request.MeetingToken.Trim();
            meetingCommand.Parameters.Add("@EmployeeNo", SqlDbType.VarChar, 20).Value = linkedInternalUser ? employeeNo : "";
            await using var meetingReader = await meetingCommand.ExecuteReaderAsync();
            if (!await meetingReader.ReadAsync())
                return Results.NotFound(new { success = false, code = "MEETING_NOT_FOUND", message = "此 QR Code 無效或找不到會議資料。" });

            var relation = meetingReader["Relation"].ToString() ?? "none";
            var allowUnlistedParticipants = Convert.ToBoolean(meetingReader["AllowUnlistedParticipants"]);
            var expectedParticipantCount = Convert.ToInt32(meetingReader["ExpectedParticipantCount"]);

            if (!linkedInternalUser && !allowUnlistedParticipants)
            {
                return Results.Json(new
                {
                    success = false,
                    code = "ACCOUNT_NOT_LINKED",
                    message = "本會議不允許非指定名單人員加入。"
                }, statusCode: 403);
            }

            if (linkedInternalUser && relation == "none" && !allowUnlistedParticipants)
                return Results.Json(new { success = false, code = "NOT_INVITED", message = "本會議不允許非指定名單人員加入。" }, statusCode: 403);
            if (linkedInternalUser && relation == "none" && allowUnlistedParticipants)
                relation = "guest";
            if (!linkedInternalUser)
                relation = "externalGuest";

            var start = Convert.ToDateTime(meetingReader["StartDateTime"]);
            var end = Convert.ToDateTime(meetingReader["EndDateTime"]);
            var locationType = Convert.ToInt32(meetingReader["LocationType"]);
            var roomId = meetingReader["RoomID"] is DBNull ? "" : meetingReader["RoomID"].ToString();
            var customLocation = meetingReader["CustomLocation"] is DBNull ? "" : meetingReader["CustomLocation"].ToString();
            var meetingNo = meetingReader["MeetingNo"].ToString() ?? "";
            var meetingName = meetingReader["MeetingName"].ToString() ?? "";
            var meetingSubName = meetingReader["MeetingSubName"] is DBNull ? "" : meetingReader["MeetingSubName"].ToString() ?? "";
            var note = meetingReader["Description"] is DBNull ? "" : meetingReader["Description"].ToString() ?? "";
            await meetingReader.CloseAsync();
            var location = await ResolveLocationDisplay(meetingConnection, locationType, roomId, customLocation);

            if (AppClock.Now > end.AddMinutes(CheckInRules.CloseMinutesAfterEnd))
            {
                return Results.Json(new
                {
                    success = false,
                    code = "MEETING_CLOSED",
                    message = $"此會議已結束。{meetingName}｜{start:yyyy/MM/dd HH:mm}～{end:HH:mm}"
                }, statusCode: StatusCodes.Status410Gone);
            }

            var sessionEmployeeNo = linkedInternalUser ? employeeNo : CreateGuestKey(effectiveUserId);

            // 訪客再次掃碼（例如回來簽退）時帶入上次填寫的資料，不必重填。
            // 以前每次都要重填，只要和第一次打得不一樣，就會找不到原本的簽到紀錄。
            var guestProfile = linkedInternalUser ? null : await FindGuestProfile(meetingConnection, meetingNo, sessionEmployeeNo);
            var guestName = guestProfile?.Name;
            var guestOrganization = guestProfile?.Organization;
            var guestTitle = guestProfile?.Title;

            var requiresProfile = !linkedInternalUser && guestProfile is null;
            var mobileSession = CreateMobileMeetingSession(
                dataProtectionProvider,
                linkedInternalUser ? 1 : 2,
                sessionEmployeeNo,
                meetingNo,
                request.MeetingToken.Trim(),
                end,
                externalName: guestName,
                externalOrganization: guestOrganization,
                externalTitle: guestTitle);

            return Results.Ok(new
            {
                success = true,
                requiresProfile,
                mobileSession,
                user = new
                {
                    lineUserId = effectiveUserId,
                    lineDisplayName = verification.DisplayName,
                    employeeNo = linkedInternalUser ? employeeNo : sessionEmployeeNo,
                    employeeId = linkedInternalUser ? employeeNo : sessionEmployeeNo,
                    sntopid = linkedInternalUser ? employeeNo : "",
                    userID = effectiveUserId,
                    name = linkedInternalUser ? employeeName : guestName ?? "",
                    departmentId = linkedInternalUser ? departmentId : "",
                    dpid = linkedInternalUser ? departmentId : "",
                    department = linkedInternalUser ? departmentName : guestOrganization ?? "",
                    dpcnm = linkedInternalUser ? departmentName : "",
                    title = linkedInternalUser ? dutyName : guestTitle ?? "",
                    dutnm = linkedInternalUser ? dutyName : "",
                    participantType = linkedInternalUser ? "Internal" : "External",
                    requiresProfile
                },
                meeting = new
                {
                    meetingNo,
                    id = meetingNo,
                    name = meetingName,
                    subName = meetingSubName,
                    date = start.ToString("yyyy-MM-dd"),
                    start = start.ToString("HH:mm"),
                    end = end.ToString("HH:mm"),
                    checkInOpenAt = start.AddMinutes(-CheckInRules.OpenMinutesBeforeStart).ToString("yyyy-MM-ddTHH:mm:ss"),
                    startDateTime = start.ToString("yyyy-MM-ddTHH:mm:ss"),
                    endDateTime = end.ToString("yyyy-MM-ddTHH:mm:ss"),
                    location,
                    note,
                    relation,
                    allowUnlistedParticipants,
                    expectedParticipantCount,
                    requiresProfile
                }
            });
        }).AllowAnonymous();

        // 未綁定 UserInfo 的 LINE 使用者，在開放式會議中自行填寫基本資料。
        app.MapPost("/api/meetings/{meetingNo}/guest-profile", async (
            string meetingNo,
            GuestProfileRequest request,
            HttpRequest httpRequest,
            IDataProtectionProvider dataProtectionProvider,
            IConfiguration configuration) =>
        {
            if (!TryReadMobileMeetingSession(httpRequest, dataProtectionProvider, out var mobileSession))
                return MobileSessionUnauthorized();
            if (!string.Equals(mobileSession!.MeetingNo, meetingNo.Trim(), StringComparison.OrdinalIgnoreCase)
                || mobileSession.ParticipantType != 2)
                return MobileSessionUnauthorized();

            var name = request.Name?.Trim() ?? "";
            var organization = request.Organization?.Trim() ?? "";
            var title = request.Title?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(organization) || string.IsNullOrWhiteSpace(title))
                return Results.BadRequest(new { success = false, message = "請完整輸入單位、姓名及職稱後再繼續。" });

            // 遇到資料庫死結時整筆交易重新執行（最多 3 次），使用者不會看到錯誤。
            return await RetryOnDeadlock(async () =>
            {
                await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
                await connection.OpenAsync();
                await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
                try
                {
                    DateTime endTime;
                    bool allowOpen;
                    await using (var meetingCommand = new SqlCommand("""
                        SELECT EndDateTime, ISNULL(AllowUnlistedParticipants, 0)
                        FROM dbo.Meeting WITH (UPDLOCK, HOLDLOCK)
                        WHERE MeetingNo = @MeetingNo AND QRCode = @Token AND ISNULL(IsDeleted,0)=0;
                        """, connection, tx))
                    {
                        meetingCommand.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
                        meetingCommand.Parameters.Add("@Token", SqlDbType.VarChar, 100).Value = mobileSession.MeetingToken;
                        await using var reader = await meetingCommand.ExecuteReaderAsync();
                        if (!await reader.ReadAsync()) throw new InvalidOperationException("找不到會議或 QR Token 已失效。");
                        endTime = Convert.ToDateTime(reader[0]);
                        allowOpen = Convert.ToBoolean(reader[1]);
                    }
                    if (!allowOpen)
                        throw new InvalidOperationException("此會議不允許非名單人員加入。");
                    if (AppClock.Now > endTime.AddMinutes(CheckInRules.CloseMinutesAfterEnd))
                        throw new InvalidOperationException("此會議已結束，無法再建立簽到資料。");

                    // 訪客以 GuestKey（LINE 使用者代號的單向雜湊）識別，每場會議一位訪客只有一列。
                    // 修改姓名或單位只會更新這一列，簽到紀錄是以 ParticipantID 連結，不會因此對不上。
                    // UX_MeetingParticipant_Guest 唯一索引確保連點兩次也不會建立兩筆。
                    const string upsertSql = """
                        UPDATE dbo.MeetingParticipant WITH (UPDLOCK, HOLDLOCK)
                        SET ExternalName = @Name,
                            ExternalOrganization = @Organization,
                            ExternalTitle = @Title,
                            UpdatedTime = dbo.AppNow()
                        WHERE MeetingNo = @MeetingNo AND GuestKey = @GuestKey;

                        IF @@ROWCOUNT = 0
                            INSERT dbo.MeetingParticipant
                                (MeetingNo, ParticipantType, Source, GuestKey,
                                 ExternalName, ExternalOrganization, ExternalTitle, CreatedTime)
                            VALUES
                                (@MeetingNo, 2, 3, @GuestKey, @Name, @Organization, @Title, dbo.AppNow());
                        """;
                    await using var upsert = new SqlCommand(upsertSql, connection, tx);
                    upsert.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
                    upsert.Parameters.Add("@GuestKey", SqlDbType.VarChar, 20).Value = mobileSession.EmployeeNo;
                    upsert.Parameters.Add("@Name", SqlDbType.NVarChar, 100).Value = name;
                    upsert.Parameters.Add("@Organization", SqlDbType.NVarChar, 200).Value = organization;
                    upsert.Parameters.Add("@Title", SqlDbType.NVarChar, 100).Value = title;
                    await upsert.ExecuteNonQueryAsync();
                    await tx.CommitAsync();

                    var refreshedSession = CreateMobileMeetingSession(
                        dataProtectionProvider, 2, mobileSession.EmployeeNo, meetingNo.Trim(), mobileSession.MeetingToken,
                        endTime, name, organization, title);

                    return Results.Ok(new
                    {
                        success = true,
                        mobileSession = refreshedSession,
                        user = new
                        {
                            employeeNo = mobileSession.EmployeeNo,
                            employeeId = mobileSession.EmployeeNo,
                            sntopid = "",
                            userID = mobileSession.EmployeeNo,
                            name,
                            department = organization,
                            dpcnm = organization,
                            title,
                            dutnm = title,
                            participantType = "External",
                            requiresProfile = false
                        }
                    });
                }
                catch (Exception ex) when (!IsDeadlock(ex))
                {
                    await tx.RollbackAsync();
                    return Results.Json(new { success = false, message = CleanDatabaseMessage(ex) }, statusCode: 400);
                }
            });
        }).AllowAnonymous();

        if (app.Environment.IsDevelopment())
        {
            app.MapPost("/api/dev/mobile-guest-session", async (
                DevMobileGuestSessionRequest request,
                IDataProtectionProvider dataProtectionProvider,
                IConfiguration configuration) =>
            {
                var meetingToken = request.MeetingToken?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(meetingToken))
                    return Results.BadRequest(new { success = false, message = "測試模式需要 Meeting Token。" });

                await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
                await connection.OpenAsync();
                const string sql = """
                    SELECT TOP (1) MeetingNo, MeetingName, MeetingSubName, StartDateTime, EndDateTime,
                           LocationType, RoomID, CustomLocation, Description,
                           ISNULL(AllowUnlistedParticipants,0) AS AllowUnlistedParticipants,
                           ISNULL(ExpectedParticipantCount,0) AS ExpectedParticipantCount
                    FROM dbo.Meeting
                    WHERE QRCode=@Token AND ISNULL(IsDeleted,0)=0;
                    """;
                await using var command = new SqlCommand(sql, connection);
                command.Parameters.Add("@Token", SqlDbType.VarChar, 100).Value = meetingToken;
                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                    return Results.NotFound(new { success = false, message = "找不到此 Meeting Token 對應的會議。" });
                if (!Convert.ToBoolean(reader["AllowUnlistedParticipants"]))
                    return Results.BadRequest(new { success = false, message = "此會議未開放非名單人員加入，無法測試未綁定 UserID 流程。" });

                var meetingNo = reader["MeetingNo"].ToString() ?? "";
                var meetingName = reader["MeetingName"].ToString() ?? "";
                var meetingSubName = reader["MeetingSubName"] is DBNull ? "" : reader["MeetingSubName"].ToString() ?? "";
                var start = Convert.ToDateTime(reader["StartDateTime"]);
                var end = Convert.ToDateTime(reader["EndDateTime"]);
                var locationType = Convert.ToInt32(reader["LocationType"]);
                var roomId = reader["RoomID"] is DBNull ? "" : reader["RoomID"].ToString();
                var customLocation = reader["CustomLocation"] is DBNull ? "" : reader["CustomLocation"].ToString();
                var note = reader["Description"] is DBNull ? "" : reader["Description"].ToString() ?? "";
                var expectedParticipantCount = Convert.ToInt32(reader["ExpectedParticipantCount"]);
                await reader.CloseAsync();
                var location = await ResolveLocationDisplay(connection, locationType, roomId, customLocation);

                var devLineUserId = "UDEVUNLINKED000000000000000000001";
                var guestKey = CreateGuestKey(devLineUserId);
                var guestProfile = await FindGuestProfile(connection, meetingNo, guestKey);
                var requiresProfile = guestProfile is null;
                var session = CreateMobileMeetingSession(dataProtectionProvider, 2, guestKey, meetingNo, meetingToken, end,
                    guestProfile?.Name, guestProfile?.Organization, guestProfile?.Title);
                return Results.Ok(new
                {
                    success = true, requiresProfile, mobileSession = session,
                    user = new { lineUserId = devLineUserId, employeeNo = guestKey, employeeId = guestKey, userID = devLineUserId, name = guestProfile?.Name ?? "", department = guestProfile?.Organization ?? "", title = guestProfile?.Title ?? "", participantType = "External", requiresProfile },
                    meeting = new { meetingNo, id = meetingNo, name = meetingName, subName = meetingSubName, date = start.ToString("yyyy-MM-dd"), start = start.ToString("HH:mm"), end = end.ToString("HH:mm"), checkInOpenAt = start.AddMinutes(-CheckInRules.OpenMinutesBeforeStart).ToString("yyyy-MM-ddTHH:mm:ss"), startDateTime = start.ToString("yyyy-MM-ddTHH:mm:ss"), endDateTime = end.ToString("yyyy-MM-ddTHH:mm:ss"), location, note, relation = "externalGuest", allowUnlistedParticipants = true, expectedParticipantCount, requiresProfile }
                });
            }).AllowAnonymous();

            app.MapPost("/api/dev/mobile-session", async (
                DevMobileSessionRequest request,
                IDataProtectionProvider dataProtectionProvider,
                IConfiguration configuration) =>
            {
                var employeeNoInput = request.EmployeeNo?.Trim() ?? "";
                var meetingToken = request.MeetingToken?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(employeeNoInput) || string.IsNullOrWhiteSpace(meetingToken))
                    return Results.BadRequest(new { success = false, message = "測試模式需要員工編號與 Meeting Token。" });

                string employeeNo;
                string employeeName;
                string departmentId;
                string departmentName;
                string dutyName;
                await using (var lineConnection = new SqlConnection(GetConnection(configuration, "LineDb")))
                {
                    await lineConnection.OpenAsync();
                    const string userSql = """
                        SELECT TOP (1)
                            LTRIM(RTRIM(SNTOPID)) AS EmployeeNo,
                            LTRIM(RTRIM(CNM)) AS EmployeeName,
                            ISNULL(LTRIM(RTRIM(DPID)), N'') AS DepartmentID,
                            ISNULL(LTRIM(RTRIM(DPCNM)), N'') AS DepartmentName,
                            ISNULL(LTRIM(RTRIM(DUTNM)), N'') AS DutyName
                        FROM dbo.UserInfo
                        WHERE SNTOPID = @EmployeeNo;
                        """;
                    await using var userCommand = new SqlCommand(userSql, lineConnection);
                    userCommand.Parameters.Add("@EmployeeNo", SqlDbType.NVarChar, 100).Value = employeeNoInput;
                    await using var userReader = await userCommand.ExecuteReaderAsync();
                    if (!await userReader.ReadAsync())
                        return Results.NotFound(new { success = false, message = "測試員工編號找不到院內人員資料。" });

                    employeeNo = userReader["EmployeeNo"].ToString()?.Trim() ?? employeeNoInput;
                    employeeName = userReader["EmployeeName"].ToString()?.Trim() ?? "";
                    departmentId = userReader["DepartmentID"].ToString()?.Trim() ?? "";
                    departmentName = userReader["DepartmentName"].ToString()?.Trim() ?? "";
                    dutyName = userReader["DutyName"].ToString()?.Trim() ?? "";
                }

                await using var meetingConnection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
                await meetingConnection.OpenAsync();
                const string meetingSql = """
                    SELECT TOP (1)
                        m.MeetingNo, m.MeetingName, m.MeetingSubName,
                        m.StartDateTime, m.EndDateTime,
                        m.LocationType, m.RoomID, m.CustomLocation,
                        m.Host1EmployeeNo, m.Host2EmployeeNo, m.Host3EmployeeNo,
                        m.CreatedByEmployeeNo, m.Description,
                        ISNULL(m.AllowUnlistedParticipants, 0) AS AllowUnlistedParticipants,
                        ISNULL(m.ExpectedParticipantCount, 0) AS ExpectedParticipantCount,
                        CASE
                            WHEN @EmployeeNo = m.CreatedByEmployeeNo
                              OR @EmployeeNo IN (m.Host1EmployeeNo, m.Host2EmployeeNo, m.Host3EmployeeNo) THEN N'host'
                            WHEN EXISTS (
                                SELECT 1 FROM dbo.MeetingParticipant p
                                WHERE p.MeetingNo = m.MeetingNo
                                  AND p.ParticipantType = 1
                                  AND p.EmployeeNo = @EmployeeNo
                            ) THEN N'participant'
                            ELSE N'none'
                        END AS Relation
                    FROM dbo.Meeting m
                    WHERE m.QRCode = @Token
                      AND ISNULL(m.IsDeleted, 0) = 0;
                    """;
                await using var meetingCommand = new SqlCommand(meetingSql, meetingConnection);
                meetingCommand.Parameters.Add("@Token", SqlDbType.VarChar, 100).Value = meetingToken;
                meetingCommand.Parameters.Add("@EmployeeNo", SqlDbType.VarChar, 20).Value = employeeNo;
                await using var meetingReader = await meetingCommand.ExecuteReaderAsync();
                if (!await meetingReader.ReadAsync())
                    return Results.NotFound(new { success = false, message = "找不到此 Meeting Token 對應的會議。" });

                var relation = meetingReader["Relation"].ToString() ?? "none";
                var allowUnlistedParticipants = Convert.ToBoolean(meetingReader["AllowUnlistedParticipants"]);
                if (relation == "none" && !allowUnlistedParticipants)
                    return Results.Json(new { success = false, message = "此測試員工不在本次會議名單中。" }, statusCode: 403);
                if (relation == "none") relation = "guest";

                var start = Convert.ToDateTime(meetingReader["StartDateTime"]);
                var end = Convert.ToDateTime(meetingReader["EndDateTime"]);
                var locationType = Convert.ToInt32(meetingReader["LocationType"]);
                var roomId = meetingReader["RoomID"] is DBNull ? "" : meetingReader["RoomID"].ToString();
                var customLocation = meetingReader["CustomLocation"] is DBNull ? "" : meetingReader["CustomLocation"].ToString();
                var meetingNo = meetingReader["MeetingNo"].ToString() ?? "";
                var meetingName = meetingReader["MeetingName"].ToString() ?? "";
                var meetingSubName = meetingReader["MeetingSubName"] is DBNull ? "" : meetingReader["MeetingSubName"].ToString() ?? "";
                var note = meetingReader["Description"] is DBNull ? "" : meetingReader["Description"].ToString() ?? "";
                var expectedParticipantCount = Convert.ToInt32(meetingReader["ExpectedParticipantCount"]);
                await meetingReader.CloseAsync();
                var location = await ResolveLocationDisplay(meetingConnection, locationType, roomId, customLocation);

                var mobileSession = CreateMobileMeetingSession(dataProtectionProvider, 1, employeeNo, meetingNo, meetingToken, end);
                return Results.Ok(new
                {
                    success = true,
                    mobileSession,
                    user = new
                    {
                        employeeNo,
                        employeeId = employeeNo,
                        sntopid = employeeNo,
                        userID = employeeNo,
                        name = employeeName,
                        departmentId,
                        dpid = departmentId,
                        department = departmentName,
                        dpcnm = departmentName,
                        title = dutyName,
                        dutnm = dutyName
                    },
                    meeting = new
                    {
                        meetingNo,
                        id = meetingNo,
                        name = meetingName,
                        subName = meetingSubName,
                        date = start.ToString("yyyy-MM-dd"),
                        start = start.ToString("HH:mm"),
                        end = end.ToString("HH:mm"),
                        checkInOpenAt = start.AddMinutes(-CheckInRules.OpenMinutesBeforeStart).ToString("yyyy-MM-ddTHH:mm:ss"),
                        startDateTime = start.ToString("yyyy-MM-ddTHH:mm:ss"),
                        endDateTime = end.ToString("yyyy-MM-ddTHH:mm:ss"),
                        location,
                        note,
                        relation,
                        allowUnlistedParticipants,
                        expectedParticipantCount,
                        meetingToken
                    }
                });
            }).AllowAnonymous();
        }
    }

    // 向 LINE 驗證前端送來的 ID Token，確認簽章與來源 channel，並取回 LINE 使用者代號（sub）。
    // 不信任前端或網址宣稱的身分，這是 LINE 官方建議的做法。
    internal static async Task<(bool Ok, string LineUserId, string DisplayName, string Error)> VerifyLineIdToken(
        IHttpClientFactory httpClientFactory, IConfiguration configuration, string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
            return (false, "", "", "缺少 LINE 身分驗證資料，請重新由 LINE 進入會議。");

        var channelId = configuration["LineLiff:ChannelId"];
        if (string.IsNullOrWhiteSpace(channelId))
            return (false, "", "", "尚未設定 LineLiff:ChannelId，無法驗證 LINE 身分。");

        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync("https://api.line.me/oauth2/v2.1/verify",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["id_token"] = idToken.Trim(),
                    ["client_id"] = channelId.Trim()
                }));
        }
        catch (Exception)
        {
            return (false, "", "", "無法連線至 LINE 驗證服務，請稍後再試。");
        }

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            return (false, "", "", "LINE 身分驗證失敗，請重新由 LINE 進入會議。");

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            // aud 必須等於本系統的 channel，避免接受其他 channel 簽發的 token。
            if (!root.TryGetProperty("aud", out var audience)
                || !string.Equals(audience.GetString()?.Trim(), channelId.Trim(), StringComparison.Ordinal))
                return (false, "", "", "LINE 身分驗證失敗，請重新由 LINE 進入會議。");

            var subject = root.TryGetProperty("sub", out var sub) ? sub.GetString()?.Trim() ?? "" : "";
            if (string.IsNullOrWhiteSpace(subject))
                return (false, "", "", "LINE 身分驗證失敗，請重新由 LINE 進入會議。");

            var displayName = root.TryGetProperty("name", out var name) ? name.GetString()?.Trim() ?? "" : "";
            return (true, subject, displayName, "");
        }
        catch (JsonException)
        {
            return (false, "", "", "LINE 身分驗證失敗，請重新由 LINE 進入會議。");
        }
    }

}
