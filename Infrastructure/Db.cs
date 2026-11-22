namespace MeetingCheckin;

internal static class Db
{
    internal static string GetConnection(IConfiguration configuration, string name)
    {
        var value = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"尚未設定 {name} 連線字串，請以環境變數 ConnectionStrings__{name} 或 user-secrets 設定。");
        return value;
    }

    internal static string Output(SqlParameter parameter) =>
        parameter.Value is null or DBNull ? "" : Convert.ToString(parameter.Value)?.Trim() ?? "";

    internal static object DbValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    // 記錄「誰、從哪個畫面」修改簽到紀錄。資料庫的 TR_MeetingAttendance_Log 觸發程序
    // 會讀這兩個值寫進修改歷程。每個會寫入簽到紀錄的交易一開始都要呼叫一次；
    // 連線會被連線池重複使用，所以每次都明確設定（訪客自行操作時 employeeNo 為 null）。
    internal static async Task SetAuditContext(SqlConnection connection, SqlTransaction tx, string? employeeNo, string source)
    {
        await using var command = new SqlCommand("""
            EXEC sys.sp_set_session_context @key = N'EmployeeNo', @value = @EmployeeNo;
            EXEC sys.sp_set_session_context @key = N'ChangeSource', @value = @Source;
            """, connection, tx);
        command.Parameters.Add("@EmployeeNo", SqlDbType.VarChar, 20).Value = string.IsNullOrWhiteSpace(employeeNo) ? DBNull.Value : employeeNo.Trim();
        command.Parameters.Add("@Source", SqlDbType.NVarChar, 20).Value = source;
        await command.ExecuteNonQueryAsync();
    }

    internal static string ReaderText(SqlDataReader reader, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (!reader.GetName(i).Equals(candidate, StringComparison.OrdinalIgnoreCase)) continue;
                return reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i))?.Trim() ?? "";
            }
        }

        return "";
    }

    internal static string CleanDatabaseMessage(Exception exception)
    {
        if (exception is SqlException sqlException)
        {
            if (sqlException.Number >= 50000)
                return sqlException.Message;
            // 2601 / 2627：違反唯一索引，通常是同一筆資料連續送出兩次。
            if (sqlException.Number is 2601 or 2627)
                return "這筆資料已經存在，可能剛剛已送出過，請重新整理後再試。";
            // 547：違反外來鍵或 CHECK 約束，代表資料已被別人修改，或送來的內容不符合規則。
            if (sqlException.Number == 547)
                return "資料不符合系統規則，可能已被其他人修改，請重新整理後再試。";

            // 其他資料庫錯誤（連線失敗、逾時等）的原文含有伺服器資訊，不回傳給使用者。
            return "系統暫時無法處理，請稍後再試。";
        }

        // 程式自己拋出的業務規則訊息（例如「已有人簽到，不能修改會議日期」）可以直接顯示；
        // 其他例外的原文可能含有內部資訊，一律改成通用訊息。
        return exception is InvalidOperationException or ArgumentException
            ? exception.Message
            : "系統暫時無法處理，請稍後再試。";
    }

    internal static (string IdColumn, string NameColumn)? cachedRoomInfoColumns = null;

    internal static readonly SemaphoreSlim roomInfoColumnsLock = new SemaphoreSlim(1, 1);

    internal static async Task<(string IdColumn, string NameColumn)> GetRoomInfoColumns(SqlConnection connection)
    {
        if (cachedRoomInfoColumns.HasValue)
            return cachedRoomInfoColumns.Value;

        await roomInfoColumnsLock.WaitAsync();
        try
        {
            if (cachedRoomInfoColumns.HasValue)
                return cachedRoomInfoColumns.Value;
        const string columnSql = """
            SELECT
                c.name,
                TYPE_NAME(c.system_type_id) AS SystemTypeName,
                c.column_id
            FROM sys.columns AS c
            INNER JOIN sys.tables AS tb ON tb.object_id = c.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = tb.schema_id
            WHERE s.name = N'dbo' AND tb.name = N'RoomInfo'
            ORDER BY c.column_id;
            """;

        var columns = new List<(string Name, string TypeName, int Order)>();
        await using var command = new SqlCommand(columnSql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.GetInt32(2)));
        }

        if (columns.Count == 0)
        {
            throw new InvalidOperationException("找不到 LineMeeting.dbo.RoomInfo 資料表。");
        }

        static string Normalize(string value) =>
            new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        string? FindExact(params string[] candidates)
        {
            var normalizedCandidates = candidates.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return columns.FirstOrDefault(column => normalizedCandidates.Contains(Normalize(column.Name))).Name;
        }

        bool IsNumeric(string typeName) => typeName.ToLowerInvariant() is
            "int" or "bigint" or "smallint" or "tinyint" or "decimal" or "numeric";

        bool IsText(string typeName) => typeName.ToLowerInvariant() is
            "nvarchar" or "varchar" or "nchar" or "char" or "text" or "ntext";

        var idColumn = FindExact(
            "RoomID", "RoomNo", "RoomNumber", "RoomCode", "RMID", "RMNO", "RMCODE",
            "LocationID", "LocationNo", "LocationCode", "PlaceID", "PlaceNo", "PlaceCode",
            "ID", "Code", "No")
            ?? columns.FirstOrDefault(column => IsNumeric(column.TypeName)).Name
            ?? columns.First().Name;

        var nameColumn = FindExact(
            "RoomName", "RoomNM", "RoomCNM", "RMName", "RMNM", "RMCNM",
            "LocationName", "LocationNM", "PlaceName", "PlaceNM",
            "Name", "NM", "CNM", "Description", "Desc")
            ?? columns.FirstOrDefault(column =>
                !column.Name.Equals(idColumn, StringComparison.OrdinalIgnoreCase) && IsText(column.TypeName)).Name
            ?? columns.FirstOrDefault(column =>
                !column.Name.Equals(idColumn, StringComparison.OrdinalIgnoreCase)).Name
            ?? idColumn;


            cachedRoomInfoColumns = (idColumn, nameColumn);
            return cachedRoomInfoColumns.Value;
        }
        finally
        {
            roomInfoColumnsLock.Release();
        }
    }

    internal static async Task<string> ResolveLocationDisplay(SqlConnection connection, int locationType, string? roomId, string? customLocation)
    {
        if (locationType == 0) return "無";
        if (locationType == 2)
            return string.IsNullOrWhiteSpace(customLocation) ? "無" : customLocation.Trim();
        if (string.IsNullOrWhiteSpace(roomId)) return "無";

        var roomColumns = await GetRoomInfoColumns(connection);
        var sql = $"""
            SELECT TOP (1)
                LTRIM(RTRIM(CONVERT(NVARCHAR(200), {QuoteSqlName(roomColumns.NameColumn)})))
            FROM dbo.RoomInfo
            WHERE CONVERT(NVARCHAR(100), {QuoteSqlName(roomColumns.IdColumn)}) = @RoomID;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@RoomID", SqlDbType.NVarChar, 100).Value = roomId.Trim();
        var value = Convert.ToString(await command.ExecuteScalarAsync())?.Trim();
        return string.IsNullOrWhiteSpace(value) ? "無" : value;
    }

    internal static string QuoteSqlName(string name) =>
        $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    internal static bool HasColumn(SqlDataReader reader, string name) =>
        Enumerable.Range(0, reader.FieldCount).Any(i => reader.GetName(i).Equals(name, StringComparison.OrdinalIgnoreCase));


    // SQL Server 選為死結犧牲者時回傳錯誤 1205，該交易已被資料庫回復，重新執行即可。
    internal static bool IsDeadlock(Exception exception) =>
        exception is SqlException sql && sql.Number == 1205;

    // 交易遇到死結時重新執行整個操作。每次重試都會重新開連線與交易，
    // 並稍微隨機等待，避免兩邊同時重試又撞在一起。
    internal static async Task<IResult> RetryOnDeadlock(Func<Task<IResult>> operation, int maxAttempts = 3)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (IsDeadlock(ex) && attempt < maxAttempts)
            {
                await Task.Delay(Random.Shared.Next(50, 150) * attempt);
            }
            catch (Exception ex) when (IsDeadlock(ex))
            {
                return Results.Json(new { success = false, message = "系統忙碌中，請稍後再試。" }, statusCode: 503);
            }
        }
    }
}
