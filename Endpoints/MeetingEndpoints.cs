namespace MeetingCheckin;

internal static class MeetingEndpoints
{
    public static void MapMeetingEndpoints(this WebApplication app)
    {
        // 建立者一律是目前登入的人，不採用前端送來的 CurrentUserID。
        app.MapPost("/api/meetings", async (MeetingSaveRequest request, ClaimsPrincipal principal, IConfiguration configuration) =>
            await SaveMeeting(request with { CurrentUserID = CurrentEmployeeNo(principal) }, null, configuration));

        app.MapPut("/api/meetings/{meetingNo}", async (string meetingNo, MeetingSaveRequest request, ClaimsPrincipal principal, IConfiguration configuration) =>
            await SaveMeeting(request with { CurrentUserID = CurrentEmployeeNo(principal) }, meetingNo, configuration));

        app.MapGet("/api/meetings", async (
            ClaimsPrincipal principal,
            string? keyword,
            DateTime? startDate,
            DateTime? endDate,
            IConfiguration configuration) =>
        {
            var userId = CurrentEmployeeNo(principal);
            var list = new List<object>();
            await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
            await connection.OpenAsync();

            var roomColumns = await GetRoomInfoColumns(connection);
            var sql = $"""
                SELECT
                    (SELECT TOP (1) LTRIM(RTRIM(CONVERT(NVARCHAR(200), ri.{QuoteSqlName(roomColumns.NameColumn)})))
                     FROM dbo.RoomInfo ri
                     WHERE CONVERT(NVARCHAR(100), ri.{QuoteSqlName(roomColumns.IdColumn)}) = m.RoomID) AS RoomName,
                    m.MeetingNo, m.MeetingName, m.MeetingSubName,
                    m.StartDateTime, m.EndDateTime,
                    m.LocationType, m.RoomID, m.CustomLocation,
                    m.Host1EmployeeNo, m.Host2EmployeeNo, m.Host3EmployeeNo,
                    m.QRCode, m.Description,
                    ISNULL(m.AllowUnlistedParticipants, 0) AS AllowUnlistedParticipants,
                    ISNULL(m.ExpectedParticipantCount, 0) AS ExpectedParticipantCount,
                    m.CreatedByEmployeeNo, m.CreatedTime, m.UpdatedByEmployeeNo, m.UpdatedTime,
                    ISNULL(NULLIF(LTRIM(RTRIM(u1.CNM)), N''), m.Host1EmployeeNo) AS Host1Name,
                    CASE WHEN NULLIF(LTRIM(RTRIM(m.Host2EmployeeNo)), '') IS NULL THEN N''
                         ELSE ISNULL(NULLIF(LTRIM(RTRIM(u2.CNM)), N''), m.Host2EmployeeNo) END AS Host2Name,
                    CASE WHEN NULLIF(LTRIM(RTRIM(m.Host3EmployeeNo)), '') IS NULL THEN N''
                         ELSE ISNULL(NULLIF(LTRIM(RTRIM(u3.CNM)), N''), m.Host3EmployeeNo) END AS Host3Name,
                    'host' AS Relation,
                    (SELECT COUNT(*) FROM dbo.MeetingParticipant p WHERE p.MeetingNo = m.MeetingNo) AS ParticipantCount,
                    CAST(CASE WHEN m.CreatedByEmployeeNo = @User THEN 1 ELSE 0 END AS BIT) AS IsCreator,
                    CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.MeetingParticipant hp JOIN dbo.MeetingAttendance ha ON ha.ParticipantID = hp.ParticipantID WHERE hp.MeetingNo = m.MeetingNo) THEN 1 ELSE 0 END AS BIT) AS HasAttendance
                FROM dbo.Meeting m
                LEFT JOIN LineDB.dbo.UserInfo u1 ON u1.SNTOPID = m.Host1EmployeeNo
                LEFT JOIN LineDB.dbo.UserInfo u2 ON u2.SNTOPID = m.Host2EmployeeNo
                LEFT JOIN LineDB.dbo.UserInfo u3 ON u3.SNTOPID = m.Host3EmployeeNo
                WHERE
                    ISNULL(m.IsDeleted, 0) = 0
                    AND
                    (
                        @User IN (m.Host1EmployeeNo, m.Host2EmployeeNo, m.Host3EmployeeNo)
                        OR m.CreatedByEmployeeNo = @User
                    )
                    AND (@Keyword IS NULL OR m.MeetingName LIKE N'%' + @Keyword + N'%' OR m.MeetingNo LIKE '%' + @Keyword + '%')
                    AND (@StartDate IS NULL OR m.StartDateTime >= @StartDate)
                    AND (@EndDate IS NULL OR m.StartDateTime < DATEADD(DAY, 1, @EndDate))
                ORDER BY m.StartDateTime DESC;
                """;

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = userId.Trim();
            command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 100).Value = DbValue(keyword);
            command.Parameters.Add("@StartDate", SqlDbType.DateTime2).Value = (object?)startDate ?? DBNull.Value;
            command.Parameters.Add("@EndDate", SqlDbType.DateTime2).Value = (object?)endDate ?? DBNull.Value;

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(MapMeeting(reader));

            return Results.Ok(list);
        });

        app.MapGet("/api/meetings/by-token/{token}", async (
            string token,
            HttpRequest httpRequest,
            IDataProtectionProvider dataProtectionProvider,
            IConfiguration configuration) =>
        {
            if (!TryReadMobileMeetingSession(httpRequest, dataProtectionProvider, out var mobileSession))
                return MobileSessionUnauthorized();

            if (!string.Equals(mobileSession!.MeetingToken, token.Trim(), StringComparison.Ordinal))
                return MobileSessionUnauthorized();
            if (await CheckMobileSessionAsync(mobileSession, configuration) is { } sessionProblem)
                return sessionProblem;

            await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
            await connection.OpenAsync();
            await using var find = new SqlCommand("SELECT MeetingNo FROM dbo.Meeting WHERE QRCode = @Token AND ISNULL(IsDeleted, 0) = 0;", connection);
            find.Parameters.Add("@Token", SqlDbType.VarChar, 100).Value = token.Trim();
            var meetingNo = Convert.ToString(await find.ExecuteScalarAsync());
            if (string.IsNullOrWhiteSpace(meetingNo))
                return Results.NotFound(new { success = false, message = "QR Code 無效或找不到會議。" });

            if (!string.Equals(mobileSession.MeetingNo, meetingNo, StringComparison.OrdinalIgnoreCase))
                return MobileSessionUnauthorized();

            return await GetMeetingDetail(meetingNo, mobileSession.EmployeeNo, configuration);
        }).AllowAnonymous();

        app.MapGet("/api/meetings/{meetingNo}", async (string meetingNo, ClaimsPrincipal principal, IConfiguration configuration) =>
            await GetMeetingDetail(meetingNo, CurrentEmployeeNo(principal), configuration));

        app.MapDelete("/api/meetings/{meetingNo}", async (string meetingNo, ClaimsPrincipal principal, IConfiguration configuration) =>
        {
            var userId = CurrentEmployeeNo(principal);
            await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
            await connection.OpenAsync();

            // 只有建立者能刪除，而且已有人簽到的會議不能刪除，避免簽到紀錄跟著從畫面上消失。
            // 刪除是標記 IsDeleted，資料仍保留在資料庫。
            const string checkSql = """
                SELECT m.CreatedByEmployeeNo,
                       CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.MeetingParticipant hp JOIN dbo.MeetingAttendance ha ON ha.ParticipantID = hp.ParticipantID WHERE hp.MeetingNo = m.MeetingNo) THEN 1 ELSE 0 END AS BIT) AS HasAttendance
                FROM dbo.Meeting AS m
                WHERE m.MeetingNo = @MeetingNo AND m.IsDeleted = 0;
                """;
            await using (var check = new SqlCommand(checkSql, connection))
            {
                check.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
                await using var reader = await check.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                    return Results.NotFound(new { success = false, message = "找不到會議。" });
                if (!string.Equals(reader["CreatedByEmployeeNo"].ToString()?.Trim(), userId, StringComparison.OrdinalIgnoreCase))
                    return Results.Json(new { success = false, message = "只有會議建立者可以刪除會議。" }, statusCode: 403);
                if (Convert.ToBoolean(reader["HasAttendance"]))
                    return Results.BadRequest(new { success = false, message = "已有人簽到的會議不能刪除。" });
            }

            // 條件再寫一次在 UPDATE 裡：檢查與刪除之間若剛好有人簽到，就不會刪到。
            const string sql = """
                UPDATE m
                SET IsDeleted = 1,
                    DeletedTime = dbo.AppNow(),
                    DeletedByEmployeeNo = @User,
                    UpdatedByEmployeeNo = @User,
                    UpdatedTime = dbo.AppNow()
                FROM dbo.Meeting AS m
                WHERE m.MeetingNo = @MeetingNo
                  AND m.IsDeleted = 0
                  AND m.CreatedByEmployeeNo = @User
                  AND NOT EXISTS (SELECT 1 FROM dbo.MeetingParticipant hp
                                  JOIN dbo.MeetingAttendance ha ON ha.ParticipantID = hp.ParticipantID
                                  WHERE hp.MeetingNo = m.MeetingNo);
                """;
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
            command.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = userId;
            if (await command.ExecuteNonQueryAsync() == 0)
                return Results.BadRequest(new { success = false, message = "會議剛剛有人簽到，無法刪除。請重新整理。" });
            return Results.Ok(new { success = true });
        });
    }

    internal static async Task<IResult> SaveMeeting(MeetingSaveRequest request, string? existingMeetingNo, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.CurrentUserID)
            || string.IsNullOrWhiteSpace(request.Host1UserID))
        {
            return Results.BadRequest(new { success = false, message = "會議名稱、建立者及主持人1不可空白。" });
        }

        DateTime start;
        DateTime end;
        try
        {
            start = CombineDateTime(request.Date, request.Start);
            end = CombineDateTime(request.Date, request.End);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { success = false, message = ex.Message });
        }

        if (end <= start)
            return Results.BadRequest(new { success = false, message = "會議結束時間必須晚於開始時間。" });

        var user = request.CurrentUserID.Trim();
        var hosts = new[] { request.Host1UserID, request.Host2UserID, request.Host3UserID }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .ToList();

        if (hosts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != hosts.Count)
            return Results.BadRequest(new { success = false, message = "主持人不可重複。" });

        // 發起會議頁面只處理院內人員；主持人 1~3 會自動加入參與名單。
        var internalPeople = (request.ParticipantUserIDs ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Where(x => !x.StartsWith("EXT|", StringComparison.OrdinalIgnoreCase))
            .Concat(hosts)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var locationType = request.HasLocation
            ? (string.IsNullOrWhiteSpace(request.CustomLocation) ? 1 : 2)
            : 0;

        if (locationType == 1 && string.IsNullOrWhiteSpace(request.LocationID))
            return Results.BadRequest(new { success = false, message = "請選擇院內會議室。" });
        if (locationType == 2 && string.IsNullOrWhiteSpace(request.CustomLocation))
            return Results.BadRequest(new { success = false, message = "請輸入自訂會議地點。" });

        // 遇到資料庫死結時整筆交易重新執行（最多 3 次），使用者不會看到錯誤。
        return await RetryOnDeadlock(async () =>
        {
            await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
            await connection.OpenAsync();
            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                var meetingNo = existingMeetingNo?.Trim();
                var isNew = string.IsNullOrWhiteSpace(meetingNo);
                var meetingEnded = false;

                if (isNew)
                {
                    var prefix = start.ToString("yyyyMM");
                    const string noSql = """
                        SELECT @Prefix + RIGHT('0000' + CONVERT(VARCHAR(4),
                            ISNULL(MAX(TRY_CONVERT(INT, RIGHT(MeetingNo, 4))), 0) + 1), 4)
                        FROM dbo.Meeting WITH (UPDLOCK, HOLDLOCK)
                        WHERE MeetingNo LIKE @Prefix + '%';
                        """;
                    await using var noCommand = new SqlCommand(noSql, connection, tx);
                    noCommand.Parameters.Add("@Prefix", SqlDbType.VarChar, 6).Value = prefix;
                    meetingNo = Convert.ToString(await noCommand.ExecuteScalarAsync())!;
                }
                else
                {
                    // 讀出目前的會議狀態並鎖定，用來判斷版本與修改限制。
                    const string checkSql = """
                        SELECT m.RowVer, m.StartDateTime, m.EndDateTime, m.AllowUnlistedParticipants,
                               m.Host1EmployeeNo, m.Host2EmployeeNo, m.Host3EmployeeNo,
                               CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.MeetingParticipant hp JOIN dbo.MeetingAttendance ha ON ha.ParticipantID = hp.ParticipantID WHERE hp.MeetingNo = m.MeetingNo) THEN 1 ELSE 0 END AS BIT) AS HasAttendance
                        FROM dbo.Meeting AS m WITH (UPDLOCK, HOLDLOCK)
                        WHERE m.MeetingNo = @MeetingNo
                          AND m.IsDeleted = 0
                          AND @User IN (m.CreatedByEmployeeNo, m.Host1EmployeeNo, m.Host2EmployeeNo, m.Host3EmployeeNo);
                        """;
                    await using var check = new SqlCommand(checkSql, connection, tx);
                    check.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo!;
                    check.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = user;
                    await using (var current = await check.ExecuteReaderAsync())
                    {
                        if (!await current.ReadAsync())
                            throw new InvalidOperationException("找不到會議，或您沒有修改權限。");

                        // 樂觀並行控制：編輯頁送回開啟時的版本。不同代表這段期間有人存過，避免互相覆蓋。
                        if (MeetingEditRules.CheckVersion(request.RowVersion, Convert.ToBase64String((byte[])current["RowVer"])) is { } versionProblem)
                            throw new InvalidOperationException(versionProblem);

                        static string? Slot(object value) => value is DBNull ? null : value.ToString();
                        var originalEnd = Convert.ToDateTime(current["EndDateTime"]);
                        var fieldProblem = MeetingEditRules.CheckMeetingFields(
                            hasAttendance: Convert.ToBoolean(current["HasAttendance"]),
                            originalStart: Convert.ToDateTime(current["StartDateTime"]),
                            originalEnd: originalEnd,
                            originalHosts: [Slot(current["Host1EmployeeNo"]), Slot(current["Host2EmployeeNo"]), Slot(current["Host3EmployeeNo"])],
                            originalAllowUnlisted: Convert.ToBoolean(current["AllowUnlistedParticipants"]),
                            newStart: start,
                            newHosts: [request.Host1UserID, request.Host2UserID, request.Host3UserID],
                            newAllowUnlisted: request.AllowUnlistedParticipants,
                            now: AppClock.Now);
                        if (fieldProblem is not null)
                            throw new InvalidOperationException(fieldProblem);
                        meetingEnded = MeetingEditRules.HasEnded(originalEnd, AppClock.Now);
                    }
                }

                if (isNew)
                {
                    const string insertSql = """
                        INSERT dbo.Meeting
                        (
                            MeetingNo, MeetingName, MeetingSubName, StartDateTime, EndDateTime,
                            LocationType, RoomID, CustomLocation,
                            Host1EmployeeNo, Host2EmployeeNo, Host3EmployeeNo,
                            QRCode, CreatedByEmployeeNo, CreatedTime,
                            UpdatedByEmployeeNo, UpdatedTime, Description,
                            AllowUnlistedParticipants, ExpectedParticipantCount
                        )
                        VALUES
                        (
                            @MeetingNo, @Name, @SubName, @Start, @End,
                            @LocationType, @RoomID, @CustomLocation,
                            @Host1, @Host2, @Host3,
                            @QRCode, @User, dbo.AppNow(),
                            NULL, NULL, @Description,
                            @AllowUnlistedParticipants, @ExpectedParticipantCount
                        );
                        """;
                    var qrToken = await CreateUniqueMeetingTokenAsync(connection, tx, meetingNo!);
                    await using var command = new SqlCommand(insertSql, connection, tx);
                    AddMeetingParameters(command, meetingNo!, request, start, end, locationType, qrToken);
                    await command.ExecuteNonQueryAsync();
                }
                else
                {
                    const string updateSql = """
                        UPDATE dbo.Meeting
                        SET MeetingName = @Name,
                            MeetingSubName = @SubName,
                            StartDateTime = @Start,
                            EndDateTime = @End,
                            LocationType = @LocationType,
                            RoomID = @RoomID,
                            CustomLocation = @CustomLocation,
                            Host1EmployeeNo = @Host1,
                            Host2EmployeeNo = @Host2,
                            Host3EmployeeNo = @Host3,
                            Description = @Description,
                            AllowUnlistedParticipants = @AllowUnlistedParticipants,
                            ExpectedParticipantCount = @ExpectedParticipantCount,
                            UpdatedByEmployeeNo = @User,
                            UpdatedTime = dbo.AppNow()
                        WHERE MeetingNo = @MeetingNo;
                        """;
                    await using var command = new SqlCommand(updateSql, connection, tx);
                    AddMeetingParameters(command, meetingNo!, request, start, end, locationType, "");
                    if (await command.ExecuteNonQueryAsync() == 0)
                        throw new InvalidOperationException("會議無法修改，請確認權限或會議是否存在。");
                }

                // 同步受邀名單（Source = 1）。現場加入者（Source = 2）與訪客（Source = 3）不在此頁維護。
                var existingInvited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var existingWalkIns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                const string existingSql = """
                    SELECT EmployeeNo, Source
                    FROM dbo.MeetingParticipant WITH (UPDLOCK, HOLDLOCK)
                    WHERE MeetingNo = @MeetingNo AND ParticipantType = 1;
                    """;
                await using (var existingCommand = new SqlCommand(existingSql, connection, tx))
                {
                    existingCommand.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo!;
                    await using var reader = await existingCommand.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var employeeNo = reader["EmployeeNo"].ToString()?.Trim() ?? "";
                        if (Convert.ToInt32(reader["Source"]) == 1) existingInvited.Add(employeeNo);
                        else existingWalkIns.Add(employeeNo);
                    }
                }

                var submittedInvited = internalPeople.ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (MeetingEditRules.CheckInvitedList(meetingEnded, existingInvited, submittedInvited) is { } listProblem)
                    throw new InvalidOperationException(listProblem);
                var removedInvited = existingInvited.Where(x => !submittedInvited.Contains(x)).ToList();

                // 已經簽到的人不可從名單移除，避免修改會議時連帶刪掉簽到紀錄。
                // 資料庫的外來鍵也會擋下這種刪除，這裡先檢查是為了給出看得懂的訊息。
                if (removedInvited.Count > 0)
                {
                    var signedRemoved = new List<string>();
                    const string signedSql = """
                        SELECT p.EmployeeNo, ISNULL(NULLIF(LTRIM(RTRIM(u.CNM)), N''), p.EmployeeNo) AS DisplayName
                        FROM dbo.MeetingParticipant AS p
                        JOIN dbo.MeetingAttendance AS a ON a.ParticipantID = p.ParticipantID
                        LEFT JOIN LineDB.dbo.UserInfo AS u ON u.SNTOPID = p.EmployeeNo
                        WHERE p.MeetingNo = @MeetingNo
                          AND p.ParticipantType = 1;
                        """;
                    await using (var signedCommand = new SqlCommand(signedSql, connection, tx))
                    {
                        signedCommand.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo!;
                        await using var signedReader = await signedCommand.ExecuteReaderAsync();
                        var removedSet = removedInvited.ToHashSet(StringComparer.OrdinalIgnoreCase);
                        while (await signedReader.ReadAsync())
                        {
                            var employeeNo = signedReader["EmployeeNo"]?.ToString()?.Trim() ?? "";
                            if (removedSet.Contains(employeeNo))
                                signedRemoved.Add($"{signedReader["DisplayName"]}（{employeeNo}）");
                        }
                    }

                    if (signedRemoved.Count > 0)
                        throw new InvalidOperationException(
                            $"以下人員已有簽到紀錄，無法從參與名單移除：{string.Join("、", signedRemoved)}。如需更正，請至簽到名單調整。");
                }

                foreach (var removed in removedInvited)
                {
                    await using var deleteParticipant = new SqlCommand(
                        "DELETE FROM dbo.MeetingParticipant WHERE MeetingNo=@MeetingNo AND EmployeeNo=@EmployeeNo AND Source=1;",
                        connection, tx);
                    deleteParticipant.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo!;
                    deleteParticipant.Parameters.Add("@EmployeeNo", SqlDbType.VarChar, 20).Value = removed;
                    await deleteParticipant.ExecuteNonQueryAsync();
                }

                foreach (var employeeNo in submittedInvited)
                {
                    string sql;
                    if (existingInvited.Contains(employeeNo))
                        sql = """
                            UPDATE dbo.MeetingParticipant
                            SET UpdatedByEmployeeNo=@User, UpdatedTime=dbo.AppNow()
                            WHERE MeetingNo=@MeetingNo AND EmployeeNo=@EmployeeNo;
                            """;
                    else if (existingWalkIns.Contains(employeeNo))
                        // 原本是現場加入的人，主持人這次把他正式加進名單：升級為受邀者，保留原本的簽到紀錄。
                        sql = """
                            UPDATE dbo.MeetingParticipant
                            SET Source=1, UpdatedByEmployeeNo=@User, UpdatedTime=dbo.AppNow()
                            WHERE MeetingNo=@MeetingNo AND EmployeeNo=@EmployeeNo;
                            """;
                    else
                        sql = """
                            INSERT dbo.MeetingParticipant
                                (MeetingNo, ParticipantType, Source, EmployeeNo, CreatedByEmployeeNo, CreatedTime)
                            VALUES (@MeetingNo, 1, 1, @EmployeeNo, @User, dbo.AppNow());
                            """;
                    await using var command = new SqlCommand(sql, connection, tx);
                    command.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo!;
                    command.Parameters.Add("@EmployeeNo", SqlDbType.VarChar, 20).Value = employeeNo;
                    command.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = user;
                    await command.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
                return Results.Ok(new { success = true, meetingNo, meetingID = meetingNo });
            }
            catch (Exception ex) when (!IsDeadlock(ex))
            {
                await tx.RollbackAsync();
                return Results.Json(new { success = false, message = CleanDatabaseMessage(ex) }, statusCode: 400);
            }
        });
    }

    internal static void AddMeetingParameters(
        SqlCommand command,
        string meetingNo,
        MeetingSaveRequest request,
        DateTime start,
        DateTime end,
        int locationType,
        string qrCode)
    {
        // 預設人數 = 受邀院內人員（含主持人）。開放式會議不計算出席率，因此固定為 0。
        var expectedParticipantCount = request.AllowUnlistedParticipants ? 0 : (request.ParticipantUserIDs ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith("EXT|", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Trim())
            .Concat(new[] { request.Host1UserID, request.Host2UserID, request.Host3UserID }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        command.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo;
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 100).Value = request.Name.Trim();
        command.Parameters.Add("@SubName", SqlDbType.NVarChar, 100).Value = DbValue(request.SubName);
        command.Parameters.Add("@Start", SqlDbType.DateTime2).Value = start;
        command.Parameters.Add("@End", SqlDbType.DateTime2).Value = end;
        command.Parameters.Add("@LocationType", SqlDbType.TinyInt).Value = locationType;
        command.Parameters.Add("@RoomID", SqlDbType.NVarChar, 20).Value =
            locationType == 1 && !string.IsNullOrWhiteSpace(request.LocationID) ? request.LocationID.Trim() : DBNull.Value;
        command.Parameters.Add("@CustomLocation", SqlDbType.NVarChar, 200).Value =
            locationType == 2 ? DbValue(request.CustomLocation) : DBNull.Value;
        command.Parameters.Add("@Host1", SqlDbType.VarChar, 20).Value = request.Host1UserID.Trim();
        command.Parameters.Add("@Host2", SqlDbType.VarChar, 20).Value = DbValue(request.Host2UserID);
        command.Parameters.Add("@Host3", SqlDbType.VarChar, 20).Value = DbValue(request.Host3UserID);
        command.Parameters.Add("@QRCode", SqlDbType.VarChar, 100).Value = qrCode;
        command.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = request.CurrentUserID.Trim();
        command.Parameters.Add("@Description", SqlDbType.NVarChar, 1000).Value = DbValue(request.Note);
        command.Parameters.Add("@AllowUnlistedParticipants", SqlDbType.Bit).Value = request.AllowUnlistedParticipants;
        command.Parameters.Add("@ExpectedParticipantCount", SqlDbType.Int).Value = expectedParticipantCount;
    }

    internal static async Task<IResult> GetMeetingDetail(string meetingNo, string userId, IConfiguration configuration)
    {
        await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
        await connection.OpenAsync();

        const string meetingSql = """
            SELECT
                m.MeetingNo, m.MeetingName, m.MeetingSubName,
                m.StartDateTime, m.EndDateTime,
                m.LocationType, m.RoomID, m.CustomLocation,
                m.Host1EmployeeNo, m.Host2EmployeeNo, m.Host3EmployeeNo,
                m.QRCode, m.Description,
                ISNULL(m.AllowUnlistedParticipants, 0) AS AllowUnlistedParticipants,
                ISNULL(m.ExpectedParticipantCount, 0) AS ExpectedParticipantCount,
                m.CreatedByEmployeeNo, m.CreatedTime, m.UpdatedByEmployeeNo, m.UpdatedTime,
                ISNULL(NULLIF(LTRIM(RTRIM(u1.CNM)), N''), m.Host1EmployeeNo) AS Host1Name,
                ISNULL(NULLIF(LTRIM(RTRIM(u2.CNM)), N''), m.Host2EmployeeNo) AS Host2Name,
                ISNULL(NULLIF(LTRIM(RTRIM(u3.CNM)), N''), m.Host3EmployeeNo) AS Host3Name,
                CASE
                    WHEN @User IN (m.Host1EmployeeNo, m.Host2EmployeeNo, m.Host3EmployeeNo)
                         OR m.CreatedByEmployeeNo = @User THEN 'host'
                    ELSE 'participant'
                END AS Relation,
                (SELECT COUNT(*) FROM dbo.MeetingParticipant p WHERE p.MeetingNo = m.MeetingNo) AS ParticipantCount,
                m.RowVer,
                CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.MeetingParticipant hp JOIN dbo.MeetingAttendance ha ON ha.ParticipantID = hp.ParticipantID WHERE hp.MeetingNo = m.MeetingNo) THEN 1 ELSE 0 END AS BIT) AS HasAttendance
            FROM dbo.Meeting m
            LEFT JOIN LineDB.dbo.UserInfo u1 ON u1.SNTOPID = m.Host1EmployeeNo
            LEFT JOIN LineDB.dbo.UserInfo u2 ON u2.SNTOPID = m.Host2EmployeeNo
            LEFT JOIN LineDB.dbo.UserInfo u3 ON u3.SNTOPID = m.Host3EmployeeNo
            WHERE m.MeetingNo = @MeetingNo
              AND ISNULL(m.IsDeleted, 0) = 0;
            """;

        object? meeting;
        await using (var command = new SqlCommand(meetingSql, connection))
        {
            command.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
            command.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = userId.Trim();
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return Results.NotFound(new { success = false, message = "找不到會議。" });

            var start = Convert.ToDateTime(reader["StartDateTime"]);
            var end = Convert.ToDateTime(reader["EndDateTime"]);
            var locationType = Convert.ToInt32(reader["LocationType"]);
            var roomId = reader["RoomID"] is DBNull ? "" : reader["RoomID"].ToString();
            var customLocation = reader["CustomLocation"] is DBNull ? "" : reader["CustomLocation"].ToString();
            var hostNames = new[]
            {
                reader["Host1Name"] is DBNull ? "" : reader["Host1Name"].ToString(),
                reader["Host2Name"] is DBNull ? "" : reader["Host2Name"].ToString(),
                reader["Host3Name"] is DBNull ? "" : reader["Host3Name"].ToString()
            }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();

            var detailData = new
            {
                id = reader["MeetingNo"].ToString(),
                meetingID = reader["MeetingNo"].ToString(),
                meetingNo = reader["MeetingNo"].ToString(),
                name = reader["MeetingName"].ToString(),
                subName = reader["MeetingSubName"] is DBNull ? "" : reader["MeetingSubName"].ToString(),
                date = start.ToString("yyyy-MM-dd"),
                start = start.ToString("HH:mm"),
                end = end.ToString("HH:mm"),
                locationType,
                roomId,
                customLocation,
                hostNames,
                host1UserID = reader["Host1EmployeeNo"] is DBNull ? "" : reader["Host1EmployeeNo"].ToString(),
                host2UserID = reader["Host2EmployeeNo"] is DBNull ? "" : reader["Host2EmployeeNo"].ToString(),
                host3UserID = reader["Host3EmployeeNo"] is DBNull ? "" : reader["Host3EmployeeNo"].ToString(),
                relation = reader["Relation"].ToString(),
                qrCode = reader["QRCode"] is DBNull ? "" : reader["QRCode"].ToString(),
                checkInOpenAt = start.AddMinutes(-CheckInRules.OpenMinutesBeforeStart).ToString("yyyy-MM-ddTHH:mm:ss"),
                startDateTime = start.ToString("yyyy-MM-ddTHH:mm:ss"),
                endDateTime = end.ToString("yyyy-MM-ddTHH:mm:ss"),
                note = reader["Description"] is DBNull ? "" : reader["Description"].ToString(),
                allowUnlistedParticipants = Convert.ToBoolean(reader["AllowUnlistedParticipants"]),
                expectedParticipantCount = Convert.ToInt32(reader["ExpectedParticipantCount"]),
                participantCount = Convert.ToInt32(reader["ParticipantCount"]),
                lastSavedAt = reader["UpdatedTime"] is DBNull ? reader["CreatedTime"] : reader["UpdatedTime"],
                rowVersion = Convert.ToBase64String((byte[])reader["RowVer"]),
                hasAttendance = Convert.ToBoolean(reader["HasAttendance"]),
                isCreator = string.Equals(reader["CreatedByEmployeeNo"].ToString()?.Trim(), userId.Trim(), StringComparison.OrdinalIgnoreCase)
            };
            await reader.CloseAsync();

            var locationDisplay = await ResolveLocationDisplay(connection, detailData.locationType, detailData.roomId, detailData.customLocation);
            meeting = new
            {
                detailData.id,
                detailData.meetingID,
                detailData.meetingNo,
                detailData.name,
                detailData.subName,
                detailData.date,
                detailData.start,
                detailData.end,
                location = locationDisplay,
                hosts = detailData.hostNames,
                host = detailData.hostNames.FirstOrDefault() ?? "",
                detailData.host1UserID,
                detailData.host2UserID,
                detailData.host3UserID,
                detailData.relation,
                detailData.qrCode,
                detailData.checkInOpenAt,
                detailData.startDateTime,
                detailData.endDateTime,
                detailData.note,
                detailData.allowUnlistedParticipants,
                detailData.expectedParticipantCount,
                detailData.participantCount,
                detailData.lastSavedAt,
                detailData.rowVersion,
                detailData.hasAttendance,
                detailData.isCreator,
                canDelete = MeetingEditRules.CanDelete(detailData.isCreator, detailData.hasAttendance)
            };
        }

        var participants = new List<object>();
        const string participantSql = """
            SELECT
                p.ParticipantID, p.ParticipantType, p.Source,
                p.EmployeeNo, p.ExternalName, p.ExternalOrganization, p.ExternalTitle,
                CASE WHEN p.ParticipantType = 1 THEN ISNULL(NULLIF(LTRIM(RTRIM(u.CNM)), N''), p.EmployeeNo) ELSE p.ExternalName END AS Name,
                CASE WHEN p.ParticipantType = 1 THEN ISNULL(LTRIM(RTRIM(u.DPCNM)), N'') ELSE p.ExternalOrganization END AS Organization,
                CASE WHEN p.ParticipantType = 1 THEN ISNULL(LTRIM(RTRIM(u.DPID)), N'') ELSE N'' END AS DepartmentID,
                CASE WHEN p.ParticipantType = 1 THEN ISNULL(LTRIM(RTRIM(u.DUTNM)), N'') ELSE ISNULL(p.ExternalTitle, N'') END AS Title,
                a.SignInTime,
                a.SignOutTime,
                a.ProxySignEmployeeNo
            FROM dbo.MeetingParticipant p
            LEFT JOIN LineDB.dbo.UserInfo u ON u.SNTOPID = p.EmployeeNo
            LEFT JOIN dbo.MeetingAttendance a ON a.ParticipantID = p.ParticipantID
            WHERE p.MeetingNo = @MeetingNo
            ORDER BY p.ParticipantType, Name;
            """;
        await using (var command = new SqlCommand(participantSql, connection))
        {
            command.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var participantType = Convert.ToInt32(reader["ParticipantType"]);
                var isInternal = participantType == 1;
                participants.Add(new
                {
                    userID = ParticipantRowId(participantType, reader["EmployeeNo"], Convert.ToInt32(reader["ParticipantID"])),
                    employeeId = isInternal ? reader["EmployeeNo"].ToString() : null,
                    sntopid = isInternal ? reader["EmployeeNo"].ToString() : null,
                    name = reader["Name"].ToString(),
                    organization = reader["Organization"].ToString(),
                    department = isInternal ? reader["Organization"].ToString() : "",
                    dept = isInternal ? reader["Organization"].ToString() : "",
                    departmentId = isInternal ? reader["DepartmentID"].ToString() : "",
                    dpid = isInternal ? reader["DepartmentID"].ToString() : "",
                    title = reader["Title"].ToString(),
                    participantType = isInternal ? "Internal" : "External",
                    source = SourceName(Convert.ToInt32(reader["Source"])),
                    externalName = isInternal ? null : reader["ExternalName"].ToString(),
                    externalOrganization = isInternal ? null : reader["ExternalOrganization"].ToString(),
                    signInTime = reader["SignInTime"] is DBNull ? "" : Convert.ToDateTime(reader["SignInTime"]).ToString("yyyy-MM-dd HH:mm"),
                    signOutTime = reader["SignOutTime"] is DBNull ? "" : Convert.ToDateTime(reader["SignOutTime"]).ToString("yyyy-MM-dd HH:mm"),
                    proxyEmployeeNo = reader["ProxySignEmployeeNo"] is DBNull ? "" : reader["ProxySignEmployeeNo"].ToString()
                });
            }
        }

        return Results.Ok(new { success = true, meeting, participants });
    }

    internal static object MapMeeting(SqlDataReader reader, bool detail = false)
    {
        var start = Convert.ToDateTime(reader["StartDateTime"]);
        var end = Convert.ToDateTime(reader["EndDateTime"]);
        var locationType = Convert.ToInt32(reader["LocationType"]);
        var custom = reader["CustomLocation"] is DBNull ? "" : reader["CustomLocation"].ToString() ?? "";
        var room = reader["RoomID"] is DBNull ? "" : reader["RoomID"].ToString() ?? "";
        var roomName = Enumerable.Range(0, reader.FieldCount).Any(i => reader.GetName(i) == "RoomName")
                       && reader["RoomName"] is not DBNull
            ? reader["RoomName"].ToString() ?? ""
            : "";

        return new
        {
            id = reader["MeetingNo"].ToString(),
            meetingID = reader["MeetingNo"].ToString(),
            meetingNo = reader["MeetingNo"].ToString(),
            name = reader["MeetingName"].ToString(),
            subName = reader["MeetingSubName"] is DBNull ? "" : reader["MeetingSubName"].ToString(),
            date = start.ToString("yyyy-MM-dd"),
            start = start.ToString("HH:mm"),
            end = end.ToString("HH:mm"),
            hasLocation = locationType != 0,
            locationID = string.IsNullOrWhiteSpace(room) ? null : room,
            location = locationType == 0 ? "無" : (!string.IsNullOrWhiteSpace(custom) ? custom : (!string.IsNullOrWhiteSpace(roomName) ? roomName : room)),
            hosts = new[]
            {
                reader["Host1Name"] is DBNull ? "" : reader["Host1Name"].ToString()?.Trim() ?? "",
                reader["Host2Name"] is DBNull ? "" : reader["Host2Name"].ToString()?.Trim() ?? "",
                reader["Host3Name"] is DBNull ? "" : reader["Host3Name"].ToString()?.Trim() ?? ""
            }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray(),
            host = string.Join("、", new[]
            {
                reader["Host1Name"] is DBNull ? "" : reader["Host1Name"].ToString()?.Trim() ?? "",
                reader["Host2Name"] is DBNull ? "" : reader["Host2Name"].ToString()?.Trim() ?? "",
                reader["Host3Name"] is DBNull ? "" : reader["Host3Name"].ToString()?.Trim() ?? ""
            }.Where(x => !string.IsNullOrWhiteSpace(x))),
            host1UserID = reader["Host1EmployeeNo"] is DBNull ? "" : reader["Host1EmployeeNo"].ToString(),
            host2UserID = reader["Host2EmployeeNo"] is DBNull ? "" : reader["Host2EmployeeNo"].ToString(),
            host3UserID = reader["Host3EmployeeNo"] is DBNull ? "" : reader["Host3EmployeeNo"].ToString(),
            relation = reader["Relation"].ToString(),
            checkInOpenAt = start.AddMinutes(-CheckInRules.OpenMinutesBeforeStart).ToString("yyyy-MM-ddTHH:mm:ss"),
            startDateTime = start.ToString("yyyy-MM-ddTHH:mm:ss"),
            endDateTime = end.ToString("yyyy-MM-ddTHH:mm:ss"),
            qrCode = reader["QRCode"] is DBNull ? "" : reader["QRCode"].ToString(),
            note = reader["Description"] is DBNull ? "" : reader["Description"].ToString(),
            allowUnlistedParticipants = Convert.ToBoolean(reader["AllowUnlistedParticipants"]),
            expectedParticipantCount = Convert.ToInt32(reader["ExpectedParticipantCount"]),
            participantCount = Convert.ToInt32(reader["ParticipantCount"]),
            lastSavedAt = reader["UpdatedTime"] is DBNull ? reader["CreatedTime"] : reader["UpdatedTime"],
            // 只有建立者、且尚無人簽到時才能刪除（清單 API 才有這兩欄）。
            canDelete = HasColumn(reader, "IsCreator")
                        && MeetingEditRules.CanDelete(Convert.ToBoolean(reader["IsCreator"]), Convert.ToBoolean(reader["HasAttendance"]))
        };
    }

}
