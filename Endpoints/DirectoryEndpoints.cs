namespace MeetingCheckin;

internal static class DirectoryEndpoints
{
    public static void MapDirectoryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/meeting-locations", async (IConfiguration configuration) =>
        {
            var list = new List<object>();
            await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
            await connection.OpenAsync();
            var roomColumns = await GetRoomInfoColumns(connection);
            var sql = $"""
                SELECT
                    CONVERT(NVARCHAR(100), {QuoteSqlName(roomColumns.IdColumn)}) AS LocationID,
                    LTRIM(RTRIM(CONVERT(NVARCHAR(200), {QuoteSqlName(roomColumns.NameColumn)}))) AS LocationName
                FROM dbo.RoomInfo
                WHERE NULLIF(LTRIM(RTRIM(CONVERT(NVARCHAR(200), {QuoteSqlName(roomColumns.NameColumn)}))), N'') IS NOT NULL
                ORDER BY LocationID;
                """;
            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new { locationID = reader["LocationID"].ToString() ?? "", locationName = reader["LocationName"].ToString() ?? "" });
            }
            return Results.Ok(list);
        });


        app.MapGet("/api/users/profile/{employeeNo}", async (string employeeNo, IConfiguration configuration) =>
        {
            if (string.IsNullOrWhiteSpace(employeeNo))
                return Results.BadRequest(new { success = false, message = "缺少員工編號。" });

            await using var connection = new SqlConnection(GetConnection(configuration, "LineDb"));
            const string sql = """
                SELECT TOP (1)
                    LTRIM(RTRIM(SNTOPID)) AS EmployeeID,
                    LTRIM(RTRIM(CNM)) AS UserName,
                    LTRIM(RTRIM(DPID)) AS DepartmentID,
                    LTRIM(RTRIM(DPCNM)) AS DepartmentName,
                    LTRIM(RTRIM(DUTNM)) AS DutyName
                FROM dbo.UserInfo
                WHERE SNTOPID = @EmployeeNo;
                """;
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@EmployeeNo", SqlDbType.NVarChar, 50).Value = employeeNo.Trim();
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                // 測試帳號的 OPID 可能不在 UserInfo。這不是登入失敗，回傳空白補充資料即可。
                var fallbackId = employeeNo.Trim();
                return Results.Ok(new
                {
                    success = true,
                    user = new
                    {
                        employeeId = fallbackId,
                        sntopid = fallbackId,
                        name = "",
                        departmentId = "",
                        dpid = "",
                        department = "",
                        dpcnm = "",
                        title = "",
                        dutnm = "",
                        userID = fallbackId
                    }
                });
            }

            var id = reader["EmployeeID"] is DBNull ? employeeNo.Trim() : reader["EmployeeID"].ToString()?.Trim() ?? employeeNo.Trim();
            var departmentId = reader["DepartmentID"] is DBNull ? "" : reader["DepartmentID"].ToString()?.Trim() ?? "";
            var department = reader["DepartmentName"] is DBNull ? "" : reader["DepartmentName"].ToString()?.Trim() ?? "";
            var title = reader["DutyName"] is DBNull ? "" : reader["DutyName"].ToString()?.Trim() ?? "";
            var name = reader["UserName"] is DBNull ? "" : reader["UserName"].ToString()?.Trim() ?? "";

            return Results.Ok(new
            {
                success = true,
                user = new
                {
                    employeeId = id,
                    sntopid = id,
                    name,
                    departmentId,
                    dpid = departmentId,
                    department,
                    dpcnm = department,
                    title,
                    dutnm = title,
                    userID = id
                }
            });
        });

        app.MapGet("/api/departments", async (IConfiguration configuration) =>
        {
            const string sql = """
                SELECT
                    LTRIM(RTRIM(DPID)) AS DepartmentID,
                    MAX(LTRIM(RTRIM(DPCNM))) AS DepartmentName
                FROM dbo.UserInfo
                WHERE NULLIF(LTRIM(RTRIM(DPID)), N'') IS NOT NULL
                GROUP BY LTRIM(RTRIM(DPID))
                ORDER BY DepartmentID;
                """;

            var list = new List<object>();
            await using var connection = new SqlConnection(GetConnection(configuration, "LineDb"));
            await using var command = new SqlCommand(sql, connection);
            await connection.OpenAsync();

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new
                {
                    departmentID = reader["DepartmentID"].ToString() ?? "",
                    departmentName = reader["DepartmentName"].ToString() ?? ""
                });
            }

            return Results.Ok(list);
        });

        app.MapGet("/api/users", async (string departmentId, IConfiguration configuration) =>
        {
            const string sql = """
                SELECT
                    LTRIM(RTRIM(SNTOPID)) AS UserID,
                    LTRIM(RTRIM(SNTOPID)) AS EmployeeID,
                    LTRIM(RTRIM(CNM)) AS UserName,
                    LTRIM(RTRIM(DPID)) AS DepartmentID,
                    LTRIM(RTRIM(DPCNM)) AS DepartmentName,
                    LTRIM(RTRIM(DUTNM)) AS DutyName
                FROM dbo.UserInfo
                WHERE DPID = @DepartmentID
                ORDER BY CNM, SNTOPID;
                """;

            var list = new List<object>();
            await using var connection = new SqlConnection(GetConnection(configuration, "LineDb"));
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@DepartmentID", SqlDbType.NVarChar, 50).Value = departmentId.Trim();
            await connection.OpenAsync();

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new
                {
                    userID = reader["UserID"].ToString() ?? "",
                    employeeId = reader["EmployeeID"].ToString() ?? "",
                    name = reader["UserName"].ToString() ?? "",
                    dept = reader["DepartmentName"].ToString() ?? "",
                    title = reader["DutyName"] is DBNull ? "" : reader["DutyName"].ToString() ?? ""
                });
            }

            return Results.Ok(list);
        });

        // 跨單位以姓名或員工編號搜尋人員（發起會議時加入參與人員用）。
        app.MapGet("/api/users/search", async (string? q, IConfiguration configuration) =>
        {
            var keyword = (q ?? "").Trim();
            if (keyword.Length == 0)
                return Results.Ok(Array.Empty<object>());
            if (keyword.Length > 50)
                keyword = keyword[..50];
            var likeValue = keyword.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

            const string sql = """
                SELECT TOP (30)
                    LTRIM(RTRIM(SNTOPID)) AS UserID,
                    LTRIM(RTRIM(CNM)) AS UserName,
                    LTRIM(RTRIM(DPID)) AS DepartmentID,
                    LTRIM(RTRIM(DPCNM)) AS DepartmentName,
                    LTRIM(RTRIM(DUTNM)) AS DutyName
                FROM dbo.UserInfo
                WHERE CNM LIKE N'%' + @Keyword + N'%'
                   OR SNTOPID LIKE @Keyword + '%'
                ORDER BY CASE WHEN LTRIM(RTRIM(SNTOPID)) = @Exact OR LTRIM(RTRIM(CNM)) = @Exact THEN 0 ELSE 1 END, DPID, CNM;
                """;

            var list = new List<object>();
            await using var connection = new SqlConnection(GetConnection(configuration, "LineDb"));
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 120).Value = likeValue;
            command.Parameters.Add("@Exact", SqlDbType.NVarChar, 50).Value = keyword;
            await connection.OpenAsync();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new
                {
                    userID = reader["UserID"].ToString() ?? "",
                    employeeId = reader["UserID"].ToString() ?? "",
                    name = reader["UserName"].ToString() ?? "",
                    departmentId = reader["DepartmentID"].ToString() ?? "",
                    dept = reader["DepartmentName"].ToString() ?? "",
                    title = reader["DutyName"] is DBNull ? "" : reader["DutyName"].ToString() ?? ""
                });
            }
            return Results.Ok(list);
        });
    }

}
