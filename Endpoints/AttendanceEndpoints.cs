namespace MeetingCheckin;

internal static class AttendanceEndpoints
{
    public static void MapAttendanceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/meetings/{meetingNo}/attendance", async (string meetingNo, ClaimsPrincipal principal, IConfiguration configuration) =>
        {
            var userId = GetUserId(principal);
            await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
            await connection.OpenAsync();

            const string permissionSql = """
                SELECT COUNT(*)
                FROM dbo.Meeting
                WHERE MeetingNo = @MeetingNo
                  AND ISNULL(IsDeleted, 0) = 0
                  AND (@User IN (Host1, Host2, Host3) OR CreateUser = @User);
                """;
            await using (var permission = new SqlCommand(permissionSql, connection))
            {
                permission.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
                permission.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = userId.Trim();
                if (Convert.ToInt32(await permission.ExecuteScalarAsync()) == 0)
                    return Results.Json(new { success = false, message = "只有主持人可以查看簽到名單。" }, statusCode: 403);
            }

            object meeting;
            const string basicSql = """
                SELECT m.MeetingNo, m.MeetingName, m.MeetingSubName, m.StartTime, m.EndTime,
                       m.LocationType, m.RoomID, m.CustomLocation,
                       ISNULL(m.AllowWalkIn, 0) AS AllowWalkIn,
                       ISNULL(m.ExpectedCount, 0) AS ExpectedCount,
                       ISNULL(NULLIF(LTRIM(RTRIM(u1.CNM)), N''), m.Host1) AS Host1Name,
                       CASE WHEN NULLIF(LTRIM(RTRIM(m.Host2)), '') IS NULL THEN N''
                            ELSE ISNULL(NULLIF(LTRIM(RTRIM(u2.CNM)), N''), m.Host2) END AS Host2Name,
                       CASE WHEN NULLIF(LTRIM(RTRIM(m.Host3)), '') IS NULL THEN N''
                            ELSE ISNULL(NULLIF(LTRIM(RTRIM(u3.CNM)), N''), m.Host3) END AS Host3Name
                FROM dbo.Meeting m
                LEFT JOIN LineDB.dbo.UserInfo u1 ON u1.SNTOPID = m.Host1
                LEFT JOIN LineDB.dbo.UserInfo u2 ON u2.SNTOPID = m.Host2
                LEFT JOIN LineDB.dbo.UserInfo u3 ON u3.SNTOPID = m.Host3
                WHERE m.MeetingNo = @MeetingNo
                  AND ISNULL(m.IsDeleted, 0) = 0;
                """;
            await using (var command = new SqlCommand(basicSql, connection))
            {
                command.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) return Results.NotFound(new { success = false, message = "找不到會議。" });
                var start = Convert.ToDateTime(reader["StartTime"]);
                var end = Convert.ToDateTime(reader["EndTime"]);
                var locationType = Convert.ToInt32(reader["LocationType"]);
                var roomId = reader["RoomID"] is DBNull ? "" : reader["RoomID"].ToString();
                var customLocation = reader["CustomLocation"] is DBNull ? "" : reader["CustomLocation"].ToString();
                var meetingId = reader["MeetingNo"].ToString();
                var meetingName = reader["MeetingName"].ToString();
                var meetingSubName = reader["MeetingSubName"] is DBNull ? "" : reader["MeetingSubName"].ToString();
                var allowWalkIn = Convert.ToBoolean(reader["AllowWalkIn"]);
                var expectedCount = Convert.ToInt32(reader["ExpectedCount"]);
                var hostNames = new[]
                {
                    reader["Host1Name"] is DBNull ? "" : reader["Host1Name"].ToString()?.Trim() ?? "",
                    reader["Host2Name"] is DBNull ? "" : reader["Host2Name"].ToString()?.Trim() ?? "",
                    reader["Host3Name"] is DBNull ? "" : reader["Host3Name"].ToString()?.Trim() ?? ""
                }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                await reader.CloseAsync();
                var locationDisplay = await GetLocationName(connection, locationType, roomId, customLocation);
                meeting = new
                {
                    id = meetingId,
                    name = meetingName,
                    subName = meetingSubName,
                    date = start.ToString("yyyy-MM-dd"),
                    start = start.ToString("HH:mm"),
                    end = end.ToString("HH:mm"),
                    location = locationDisplay,
                    hosts = hostNames,
                    host = string.Join("、", hostNames),
                    allowWalkIn,
                    expectedCount
                };
            }

            var rows = new List<object>();
            const string sql = """
                SELECT
                    p.ParticipantID, p.ParticipantType, p.Source,
                    p.EmployeeNo, p.GuestName, p.GuestOrg, p.GuestTitle,
                    CASE WHEN p.ParticipantType = 1 THEN ISNULL(NULLIF(LTRIM(RTRIM(u.CNM)), N''), p.EmployeeNo) ELSE p.GuestName END AS OriginalName,
                    CASE WHEN p.ParticipantType = 1 THEN ISNULL(LTRIM(RTRIM(u.DPCNM)), N'') ELSE p.GuestOrg END AS OriginalDepartment,
                    CASE WHEN p.ParticipantType = 1 THEN ISNULL(LTRIM(RTRIM(u.DUTNM)), N'') ELSE ISNULL(p.GuestTitle, N'') END AS OriginalTitle,
                    a.SignInTime, a.SignOutTime, a.ProxyUser, p.AbsenceReason AS Remark,
                    ISNULL(NULLIF(LTRIM(RTRIM(proxyUser.CNM)), N''), a.ProxyUser) AS ProxyName,
                    ISNULL(LTRIM(RTRIM(proxyUser.DPCNM)), N'') AS ProxyDepartment,
                    ISNULL(LTRIM(RTRIM(proxyUser.DUTNM)), N'') AS ProxyTitle
                FROM dbo.MeetingParticipant p
                LEFT JOIN LineDB.dbo.UserInfo u ON u.SNTOPID = p.EmployeeNo
                LEFT JOIN dbo.MeetingAttendance a ON a.ParticipantID = p.ParticipantID
                LEFT JOIN LineDB.dbo.UserInfo proxyUser ON proxyUser.SNTOPID = a.ProxyUser
                WHERE p.MeetingNo = @MeetingNo
                ORDER BY p.ParticipantType, OriginalName;
                """;
            await using var listCommand = new SqlCommand(sql, connection);
            listCommand.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
            await using var listReader = await listCommand.ExecuteReaderAsync();
            while (await listReader.ReadAsync())
            {
                var participantType = Convert.ToInt32(listReader["ParticipantType"]);
                var isInternal = participantType == 1;
                rows.Add(new
                {
                    userID = GetRowKey(participantType, listReader["EmployeeNo"], Convert.ToInt32(listReader["ParticipantID"])),
                    employeeId = isInternal ? listReader["EmployeeNo"].ToString() : "",
                    name = listReader["OriginalName"].ToString(),
                    department = listReader["OriginalDepartment"].ToString(),
                    title = listReader["OriginalTitle"].ToString(),
                    participantType = isInternal ? "Internal" : "External",
                    source = SourceName(Convert.ToInt32(listReader["Source"])),
                    guestName = isInternal ? null : listReader["GuestName"].ToString(),
                    guestOrg = isInternal ? null : listReader["GuestOrg"].ToString(),
                    @in = listReader["SignInTime"] is DBNull ? "" : Convert.ToDateTime(listReader["SignInTime"]).ToString("HH:mm"),
                    @out = listReader["SignOutTime"] is DBNull ? "" : Convert.ToDateTime(listReader["SignOutTime"]).ToString("HH:mm"),
                    attendanceStatus = listReader["SignInTime"] is not DBNull ? "出席" : "未出席",
                    proxyEmployeeNo = listReader["ProxyUser"] is DBNull ? "" : listReader["ProxyUser"].ToString(),
                    proxyDepartment = listReader["ProxyUser"] is DBNull ? "" : listReader["ProxyDepartment"].ToString(),
                    proxyName = listReader["ProxyUser"] is DBNull ? "" : listReader["ProxyName"].ToString(),
                    note = listReader["Remark"] is DBNull ? "" : listReader["Remark"].ToString()
                });
            }

            return Results.Ok(new { success = true, meeting, rows });
        });

        app.MapPut("/api/meetings/{meetingNo}/attendance", async (
            string meetingNo, ClaimsPrincipal principal, AttendanceSaveRequest request, IConfiguration configuration) =>
        {
            var userId = GetUserId(principal);
            // 遇到資料庫死結時整筆交易重新執行（最多 3 次），使用者不會看到錯誤。
            return await RetryOnDeadlock(async () =>
            {
                await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
                await connection.OpenAsync();
                await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

                try
                {
                    const string meetingSql = """
                        SELECT StartTime
                        FROM dbo.Meeting
                        WHERE MeetingNo = @MeetingNo
                          AND ISNULL(IsDeleted, 0) = 0
                          AND (@User IN (Host1, Host2, Host3) OR CreateUser = @User);
                        """;
                    DateTime meetingDate;
                    await using (var meetingCommand = new SqlCommand(meetingSql, connection, tx))
                    {
                        meetingCommand.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
                        meetingCommand.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = userId.Trim();
                        var value = await meetingCommand.ExecuteScalarAsync();
                        if (value is null or DBNull)
                            throw new InvalidOperationException("找不到會議，或您沒有修改簽到名單的權限。");
                        meetingDate = Convert.ToDateTime(value).Date;
                    }

                    await SetLogUser(connection, tx, userId, "sheet");

                    foreach (var row in request.Rows ?? [])
                    {
                        // 只有名單上的人可以有簽到紀錄；找不到就代表前端送來的資料與名單不符。
                        var participantId = await FindParticipantId(connection, tx, meetingNo, row.UserID)
                            ?? throw new InvalidOperationException("此人員不在本次會議名單中，無法修改簽到資料。");

                        DateTime? signIn = ParseAttendanceTime(meetingDate, row.In);
                        DateTime? signOut = ParseAttendanceTime(meetingDate, row.Out);
                        if (signIn.HasValue && signOut.HasValue && signOut < signIn)
                            throw new InvalidOperationException("簽退時間不可早於簽到時間。");

                        // 指定了代簽人卻沒有簽到時間：以前會被當成未簽到而直接刪掉，現在明確擋下。
                        if (!signIn.HasValue && !string.IsNullOrWhiteSpace(row.Operator))
                            throw new InvalidOperationException("已指定代簽人的人員必須填寫簽到時間。");

                        // 先鎖定並讀出資料庫目前的值，再決定要刪除、更新或新增。
                        DateTime? currentIn = null;
                        DateTime? currentOut = null;
                        string currentProxy = "";
                        string currentReason = "";
                        var exists = false;
                        // 簽到紀錄在 MeetingAttendance，未出席原因在 MeetingParticipant，兩邊一起鎖定讀出。
                        const string currentSql = """
                            SELECT a.SignInTime, a.SignOutTime, a.ProxyUser, p.AbsenceReason
                            FROM dbo.MeetingParticipant AS p WITH (UPDLOCK, HOLDLOCK)
                            LEFT JOIN dbo.MeetingAttendance AS a WITH (UPDLOCK, HOLDLOCK) ON a.ParticipantID = p.ParticipantID
                            WHERE p.ParticipantID = @ParticipantID;
                            """;
                        await using (var current = new SqlCommand(currentSql, connection, tx))
                        {
                            current.Parameters.Add("@ParticipantID", SqlDbType.Int).Value = participantId;
                            await using var reader = await current.ExecuteReaderAsync();
                            if (await reader.ReadAsync())
                            {
                                exists = reader["SignInTime"] is not DBNull;
                                currentIn = exists ? Convert.ToDateTime(reader["SignInTime"]) : null;
                                currentOut = reader["SignOutTime"] is DBNull ? null : Convert.ToDateTime(reader["SignOutTime"]);
                                currentProxy = reader["ProxyUser"] is DBNull ? "" : reader["ProxyUser"].ToString()?.Trim() ?? "";
                                currentReason = reader["AbsenceReason"] is DBNull ? "" : reader["AbsenceReason"].ToString()?.Trim() ?? "";
                            }
                        }

                        // 樂觀並行控制：前端送回主持人開啟簽到單時看到的值。若資料庫已經不同
                        // （例如本人剛用手機簽到或簽退），代表畫面上的資料過期，整批存檔取消，
                        // 避免用舊資料蓋掉新的紀錄。
                        if (row.Original is null)
                            throw new InvalidOperationException("簽到單版本過舊，請重新整理頁面後再儲存。");
                        static string Minute(DateTime? value) => value?.ToString("HH:mm") ?? "";
                        static bool Same(string? a, string? b) => (a ?? "").Trim() == (b ?? "").Trim();
                        if (!Same(Minute(currentIn), row.Original.In) || !Same(Minute(currentOut), row.Original.Out)
                            || !Same(currentProxy, row.Original.Operator) || !Same(currentReason, row.Original.Note))
                            throw new InvalidOperationException("有人員的簽到資料在您開啟簽到單後被更新（例如本人剛用手機簽到或簽退），本次未儲存。請重新整理簽到單後再修改。");

                        // 未出席：刪除簽到紀錄（修改歷程會留下這次刪除），並記錄未出席原因。
                        if (!signIn.HasValue)
                        {
                            await using var absent = new SqlCommand("""
                                DELETE FROM dbo.MeetingAttendance WHERE ParticipantID = @ParticipantID;
                                UPDATE dbo.MeetingParticipant SET AbsenceReason = @Reason WHERE ParticipantID = @ParticipantID;
                                """, connection, tx);
                            absent.Parameters.Add("@ParticipantID", SqlDbType.Int).Value = participantId;
                            absent.Parameters.Add("@Reason", SqlDbType.NVarChar, 500).Value = DbValue(row.Note);
                            await absent.ExecuteNonQueryAsync();
                            continue;
                        }

                        static bool SameMinute(DateTime? a, DateTime? b) =>
                            (!a.HasValue && !b.HasValue) ||
                            (a.HasValue && b.HasValue && a.Value.Year == b.Value.Year && a.Value.Month == b.Value.Month && a.Value.Day == b.Value.Day && a.Value.Hour == b.Value.Hour && a.Value.Minute == b.Value.Minute);

                        var timeChanged = !exists || !SameMinute(currentIn, signIn) || !SameMinute(currentOut, signOut);
                        var effectiveProxy = timeChanged
                            ? userId.Trim()
                            : (!string.IsNullOrWhiteSpace(row.Operator) ? row.Operator.Trim() : currentProxy);

                        const string upsertSql = """
                            IF EXISTS (SELECT 1 FROM dbo.MeetingAttendance WHERE ParticipantID = @ParticipantID)
                                UPDATE dbo.MeetingAttendance
                                SET SignInTime = @SignInTime,
                                    SignOutTime = @SignOutTime,
                                    ProxyUser = @Proxy,
                                    UpdateUser = @User,
                                    UpdateTime = dbo.fn_Now()
                                WHERE ParticipantID = @ParticipantID;
                            ELSE
                                INSERT dbo.MeetingAttendance
                                    (ParticipantID, SignInTime, SignOutTime, ProxyUser, UpdateUser, UpdateTime)
                                VALUES
                                    (@ParticipantID, @SignInTime, @SignOutTime, @Proxy, @User, dbo.fn_Now());

                            -- 已出席就沒有未出席原因。
                            UPDATE dbo.MeetingParticipant SET AbsenceReason = NULL WHERE ParticipantID = @ParticipantID;
                            """;
                        await using var upsert = new SqlCommand(upsertSql, connection, tx);
                        upsert.Parameters.Add("@ParticipantID", SqlDbType.Int).Value = participantId;
                        upsert.Parameters.Add("@SignInTime", SqlDbType.DateTime2).Value = signIn.Value;
                        upsert.Parameters.Add("@SignOutTime", SqlDbType.DateTime2).Value = (object?)signOut ?? DBNull.Value;
                        upsert.Parameters.Add("@Proxy", SqlDbType.VarChar, 20).Value = DbValue(effectiveProxy);
                        upsert.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = userId.Trim();
                        await upsert.ExecuteNonQueryAsync();
                    }

                    await tx.CommitAsync();
                    return Results.Ok(new { success = true });
                }
                catch (Exception ex) when (!IsDeadlock(ex))
                {
                    await tx.RollbackAsync();
                    return Results.Json(new { success = false, message = GetErrorMessage(ex) }, statusCode: 400);
                }
            });
        });

        app.MapGet("/api/meetings/{meetingNo}/my-attendance", async (
            string meetingNo,
            HttpRequest httpRequest,
            IDataProtectionProvider dataProtectionProvider,
            IConfiguration configuration) =>
        {
            if (!TryReadMobileSession(httpRequest, dataProtectionProvider, out var mobileSession))
                return SessionExpired();

            if (!string.Equals(mobileSession!.MeetingNo, meetingNo.Trim(), StringComparison.OrdinalIgnoreCase))
                return SessionExpired();
            if (await CheckMobileSessionAsync(mobileSession, configuration) is { } sessionProblem)
                return sessionProblem;

            var employeeNo = mobileSession.EmployeeNo;

            await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
            await connection.OpenAsync();
            // 院內人員以員工編號、訪客以 GuestKey 找到自己的名單列，再取得簽到紀錄。
            const string sql = """
                SELECT
                    a.SignInTime,
                    a.SignOutTime,
                    a.ProxyUser,
                    ISNULL(NULLIF(LTRIM(RTRIM(proxyUser.CNM)), N''), a.ProxyUser) AS ProxyName,
                    ISNULL(LTRIM(RTRIM(proxyUser.DPCNM)), N'') AS ProxyDepartment
                FROM dbo.MeetingParticipant p
                JOIN dbo.MeetingAttendance a ON a.ParticipantID = p.ParticipantID
                LEFT JOIN LineDB.dbo.UserInfo proxyUser ON proxyUser.SNTOPID = a.ProxyUser
                WHERE p.MeetingNo = @MeetingNo
                  AND ((@ParticipantType = 1 AND p.EmployeeNo = @Identity)
                    OR (@ParticipantType = 2 AND p.GuestKey = @Identity));
                """;
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
            command.Parameters.Add("@ParticipantType", SqlDbType.TinyInt).Value = mobileSession.ParticipantType;
            command.Parameters.Add("@Identity", SqlDbType.VarChar, 20).Value = employeeNo.Trim();
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return Results.Ok(new { signInTime = "", signOutTime = "", proxyEmployeeNo = "", proxyName = "", proxyDepartment = "" });
            return Results.Ok(new
            {
                signInTime = reader["SignInTime"] is DBNull ? "" : Convert.ToDateTime(reader["SignInTime"]).ToString("yyyy-MM-dd HH:mm"),
                signOutTime = reader["SignOutTime"] is DBNull ? "" : Convert.ToDateTime(reader["SignOutTime"]).ToString("yyyy-MM-dd HH:mm"),
                proxyEmployeeNo = reader["ProxyUser"] is DBNull ? "" : reader["ProxyUser"].ToString()?.Trim() ?? "",
                proxyName = reader["ProxyName"] is DBNull ? "" : reader["ProxyName"].ToString()?.Trim() ?? "",
                proxyDepartment = reader["ProxyDepartment"] is DBNull ? "" : reader["ProxyDepartment"].ToString()?.Trim() ?? ""
            });
        }).AllowAnonymous();

        app.MapPost("/api/meetings/{meetingNo}/attendance/{action}", async (
            string meetingNo,
            string action,
            SelfSignRequest request,
            HttpRequest httpRequest,
            IDataProtectionProvider dataProtectionProvider,
            IConfiguration configuration) =>
        {
            if (action is not ("signin" or "signout"))
                return Results.BadRequest(new { success = false, message = "簽到動作不正確。" });

            if (!TryReadMobileSession(httpRequest, dataProtectionProvider, out var mobileSession))
                return SessionExpired();

            if (!string.Equals(mobileSession!.MeetingNo, meetingNo.Trim(), StringComparison.OrdinalIgnoreCase))
                return SessionExpired();
            if (await CheckMobileSessionAsync(mobileSession, configuration) is { } sessionProblem)
                return sessionProblem;

            var employeeNo = mobileSession.EmployeeNo;
            if (mobileSession.ParticipantType == 2
                && (string.IsNullOrWhiteSpace(mobileSession.GuestName)
                    || string.IsNullOrWhiteSpace(mobileSession.GuestOrg)
                    || string.IsNullOrWhiteSpace(mobileSession.GuestTitle)))
            {
                return Results.BadRequest(new { success = false, message = "請先完成單位、姓名及職稱資料後再簽到。" });
            }

            // 院內人員以員工編號、訪客以 GuestKey 識別；訪客自行操作時不記錄員工編號。
            return await SaveSingleAttendance(
                meetingNo,
                action,
                mobileSession.ParticipantType,
                employeeNo,
                proxyEmployeeNo: null,
                updateUser: mobileSession.ParticipantType == 1 ? employeeNo : null,
                configuration);
        }).AllowAnonymous();

        app.MapPost("/api/meetings/{meetingNo}/attendance/proxy", async (
            string meetingNo,
            ProxySignRequest request,
            HttpRequest httpRequest,
            IDataProtectionProvider dataProtectionProvider,
            IConfiguration configuration) =>
        {
            if (request.Action is not ("signin" or "signout"))
                return Results.BadRequest(new { success = false, message = "代簽動作不正確。" });

            if (!TryReadMobileSession(httpRequest, dataProtectionProvider, out var mobileSession))
                return SessionExpired();

            if (!string.Equals(mobileSession!.MeetingNo, meetingNo.Trim(), StringComparison.OrdinalIgnoreCase))
                return SessionExpired();
            if (await CheckMobileSessionAsync(mobileSession, configuration) is { } sessionProblem)
                return sessionProblem;

            var operatorEmployeeNo = mobileSession.EmployeeNo;
            if (mobileSession.ParticipantType != 1)
                return Results.BadRequest(new { success = false, message = "非院內綁定使用者不可使用代簽功能。" });
            if (request.ParticipantType.Equals("external", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { success = false, message = "院外人員主持人代簽功能已移除。" });

            await using (var permissionConnection = new SqlConnection(GetConnection(configuration, "LineMeetingDb")))
            {
                await permissionConnection.OpenAsync();
                await using var permissionCommand = new SqlCommand(
                    "SELECT ISNULL(AllowWalkIn, 0) FROM dbo.Meeting WHERE MeetingNo = @MeetingNo AND ISNULL(IsDeleted,0)=0;",
                    permissionConnection);
                permissionCommand.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
                var allowOpen = Convert.ToBoolean(await permissionCommand.ExecuteScalarAsync() ?? false);
                if (allowOpen)
                    return Results.BadRequest(new { success = false, message = "此會議允許非名單人員加入，因此不可使用代簽功能。" });
            }

            return await SaveSingleAttendance(
                meetingNo,
                request.Action,
                1,
                request.ParticipantEmployeeNo,
                proxyEmployeeNo: operatorEmployeeNo,
                updateUser: operatorEmployeeNo,
                configuration);
        }).AllowAnonymous();
    }

    // identity：院內人員為員工編號，訪客為 GuestKey。
    // updateUser：操作者的員工編號；訪客自行操作時為 null。
    internal static async Task<IResult> SaveSingleAttendance(
        string meetingNo,
        string action,
        int participantType,
        string? identity,
        string? proxyEmployeeNo,
        string? updateUser,
        IConfiguration configuration)
    {
        // 遇到資料庫死結時整筆交易重新執行（最多 3 次），使用者不會看到錯誤。
        return await RetryOnDeadlock(async () =>
        {
            await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
            await connection.OpenAsync();
            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                const string meetingSql = """
                    SELECT StartTime, EndTime,
                           Host1, Host2, Host3, CreateUser,
                           ISNULL(AllowWalkIn, 0) AS AllowWalkIn
                    FROM dbo.Meeting
                    WHERE MeetingNo = @MeetingNo
                      AND ISNULL(IsDeleted, 0) = 0;
                    """;

                DateTime startTime;
                DateTime endTime;
                string host1EmployeeNo;
                string host2EmployeeNo;
                string host3EmployeeNo;
                string createdByEmployeeNo;
                bool allowWalkIn;

                await using (var meetingCommand = new SqlCommand(meetingSql, connection, tx))
                {
                    meetingCommand.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
                    await using var reader = await meetingCommand.ExecuteReaderAsync();
                    if (!await reader.ReadAsync()) throw new InvalidOperationException("找不到會議。");
                    startTime = Convert.ToDateTime(reader["StartTime"]);
                    endTime = Convert.ToDateTime(reader["EndTime"]);
                    host1EmployeeNo = reader["Host1"] is DBNull ? "" : reader["Host1"].ToString()?.Trim() ?? "";
                    host2EmployeeNo = reader["Host2"] is DBNull ? "" : reader["Host2"].ToString()?.Trim() ?? "";
                    host3EmployeeNo = reader["Host3"] is DBNull ? "" : reader["Host3"].ToString()?.Trim() ?? "";
                    createdByEmployeeNo = reader["CreateUser"] is DBNull ? "" : reader["CreateUser"].ToString()?.Trim() ?? "";
                    allowWalkIn = Convert.ToBoolean(reader["AllowWalkIn"]);
                }

                if (SignInRule.CheckTime(action, startTime, endTime, TaipeiTime.Now) is { } windowProblem)
                    throw new InvalidOperationException(windowProblem);

                var isProxyOperation = !string.IsNullOrWhiteSpace(proxyEmployeeNo);
                await SetLogUser(connection, tx, updateUser, isProxyOperation ? "proxy" : "mobile");
                if (isProxyOperation && allowWalkIn)
                    throw new InvalidOperationException("此會議允許非名單人員加入，因此不可使用代簽功能。");
                if (isProxyOperation)
                {
                    if (participantType != 1 || string.IsNullOrWhiteSpace(updateUser))
                        throw new InvalidOperationException("代簽功能僅限院內人員之間使用。");
                    var hosts = new[] { host1EmployeeNo, host2EmployeeNo, host3EmployeeNo, createdByEmployeeNo };
                    var isHostOperator = hosts.Any(x => string.Equals(x, updateUser, StringComparison.OrdinalIgnoreCase));
                    const string operatorSql = """
                        SELECT COUNT(*)
                        FROM dbo.MeetingParticipant
                        WHERE MeetingNo = @MeetingNo AND ParticipantType = 1 AND EmployeeNo = @EmployeeNo;
                        """;
                    await using var operatorCommand = new SqlCommand(operatorSql, connection, tx);
                    operatorCommand.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
                    operatorCommand.Parameters.Add("@EmployeeNo", SqlDbType.VarChar, 20).Value = updateUser.Trim();
                    var isInternalParticipant = Convert.ToInt32(await operatorCommand.ExecuteScalarAsync()) > 0;
                    if (!isHostOperator && !isInternalParticipant)
                        throw new InvalidOperationException("只有本次會議的院內參與者可以代簽院內人員。");
                }

                if (string.IsNullOrWhiteSpace(identity))
                    throw new InvalidOperationException("缺少簽到人員資料。");
                identity = identity.Trim();

                // 找出此人在本場會議的名單列：院內人員以員工編號、訪客以 GuestKey 識別。
                // UPDLOCK + HOLDLOCK 讓同一人同時送出兩次時，第二次會等第一次完成再判斷。
                int? participantId;
                const string findParticipantSql = """
                    SELECT ParticipantID
                    FROM dbo.MeetingParticipant WITH (UPDLOCK, HOLDLOCK)
                    WHERE MeetingNo = @MeetingNo
                      AND ((@ParticipantType = 1 AND EmployeeNo = @Identity)
                        OR (@ParticipantType = 2 AND GuestKey = @Identity));
                    """;
                await using (var findParticipant = new SqlCommand(findParticipantSql, connection, tx))
                {
                    findParticipant.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
                    findParticipant.Parameters.Add("@ParticipantType", SqlDbType.TinyInt).Value = participantType;
                    findParticipant.Parameters.Add("@Identity", SqlDbType.VarChar, 20).Value = identity;
                    var value = await findParticipant.ExecuteScalarAsync();
                    participantId = value is null or DBNull ? null : Convert.ToInt32(value);
                }

                if (participantId is null)
                {
                    if (participantType == 2)
                        throw new InvalidOperationException("請先填寫訪客資料後再簽到。");

                    // 開放式會議：院內人員本人第一次簽到時自動加入名單，標記為現場加入（Source = 2），
                    // 不會被算進預設人數與出席率。
                    if (!(allowWalkIn && !isProxyOperation && action == "signin"))
                        throw new InvalidOperationException("此人員不在本次會議參與名單中，無法簽到。");

                    const string addWalkInSql = """
                        INSERT dbo.MeetingParticipant (MeetingNo, ParticipantType, Source, EmployeeNo, CreateUser, CreateTime)
                        OUTPUT INSERTED.ParticipantID
                        VALUES (@MeetingNo, 1, 2, @EmployeeNo, @EmployeeNo, dbo.fn_Now());
                        """;
                    await using var addWalkIn = new SqlCommand(addWalkInSql, connection, tx);
                    addWalkIn.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
                    addWalkIn.Parameters.Add("@EmployeeNo", SqlDbType.VarChar, 20).Value = identity;
                    participantId = Convert.ToInt32(await addWalkIn.ExecuteScalarAsync());
                }

                DateTime? existingSignIn = null;
                DateTime? existingSignOut = null;
                string existingProxy = "";
                await using (var lockCommand = new SqlCommand("""
                    SELECT SignInTime, SignOutTime, ProxyUser
                    FROM dbo.MeetingAttendance WITH (UPDLOCK, HOLDLOCK)
                    WHERE ParticipantID = @ParticipantID;
                    """, connection, tx))
                {
                    lockCommand.Parameters.Add("@ParticipantID", SqlDbType.Int).Value = participantId.Value;
                    await using var reader = await lockCommand.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        existingSignIn = Convert.ToDateTime(reader["SignInTime"]);
                        existingSignOut = reader["SignOutTime"] is DBNull ? null : Convert.ToDateTime(reader["SignOutTime"]);
                        existingProxy = reader["ProxyUser"] is DBNull ? "" : reader["ProxyUser"].ToString()?.Trim() ?? "";
                    }
                }

                if (!isProxyOperation && !string.IsNullOrWhiteSpace(existingProxy))
                {
                    throw new InvalidOperationException(
                        $"此紀錄已由員工 {existingProxy} 代簽，請由原代簽人完成簽退；如需更正，請由主持人於簽到單調整。");
                }

                if (action == "signin")
                {
                    if (existingSignIn.HasValue)
                        throw new InvalidOperationException("此人員已完成簽到，請重新整理名單。");

                    await using var insert = new SqlCommand("""
                        INSERT dbo.MeetingAttendance
                            (ParticipantID, SignInTime, SignOutTime, ProxyUser, UpdateUser, UpdateTime)
                        VALUES
                            (@ParticipantID, dbo.fn_Now(), NULL, @Proxy, @UpdateUser, dbo.fn_Now());

                        -- 人到了，事前登記的未出席原因就不再適用。
                        UPDATE dbo.MeetingParticipant SET AbsenceReason = NULL WHERE ParticipantID = @ParticipantID;
                        """, connection, tx);
                    insert.Parameters.Add("@ParticipantID", SqlDbType.Int).Value = participantId.Value;
                    insert.Parameters.Add("@Proxy", SqlDbType.VarChar, 20).Value = (object?)proxyEmployeeNo ?? DBNull.Value;
                    insert.Parameters.Add("@UpdateUser", SqlDbType.VarChar, 20).Value = (object?)updateUser ?? DBNull.Value;
                    await insert.ExecuteNonQueryAsync();
                }
                else
                {
                    if (!existingSignIn.HasValue)
                        throw new InvalidOperationException("尚未簽到，不能簽退。");
                    if (existingSignOut.HasValue)
                        throw new InvalidOperationException("此人員已完成簽退，請重新整理名單。");
                    if (isProxyOperation && !string.Equals(existingProxy, updateUser, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("此人員不是由您代簽，只有原代簽人可以代為簽退。");

                    await using var update = new SqlCommand("""
                        UPDATE dbo.MeetingAttendance
                        SET SignOutTime = dbo.fn_Now(),
                            ProxyUser = @Proxy,
                            UpdateUser = @UpdateUser, UpdateTime = dbo.fn_Now()
                        WHERE ParticipantID = @ParticipantID
                          AND SignOutTime IS NULL;
                        """, connection, tx);
                    update.Parameters.Add("@ParticipantID", SqlDbType.Int).Value = participantId.Value;
                    var proxyToKeep = string.IsNullOrWhiteSpace(existingProxy) ? proxyEmployeeNo : existingProxy;
                    update.Parameters.Add("@Proxy", SqlDbType.VarChar, 20).Value = (object?)proxyToKeep ?? DBNull.Value;
                    update.Parameters.Add("@UpdateUser", SqlDbType.VarChar, 20).Value = (object?)updateUser ?? DBNull.Value;
                    if (await update.ExecuteNonQueryAsync() != 1)
                        throw new InvalidOperationException("簽退狀態已被其他人更新，請重新整理名單。");
                }

                await tx.CommitAsync();
                return Results.Ok(new { success = true });
            }
            catch (Exception ex) when (!IsDeadlock(ex))
            {
                await tx.RollbackAsync();
                return Results.Json(new { success = false, message = GetErrorMessage(ex) }, statusCode: 400);
            }
        });
    }

}
