using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.DataProtection;
using QRCoder;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDataProtection();
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
var app = builder.Build();

// 開始前幾分鐘開放簽到（CheckIn:OpenMinutesBeforeStart，0 表示開始時間才開放）。
CheckInRules.OpenMinutesBeforeStart = Math.Clamp(app.Configuration.GetValue("CheckIn:OpenMinutesBeforeStart", 0), 0, 240);

app.UseResponseCompression();

// 同一台院內電腦的 DNS 判定結果短暫快取，避免每個頁面 / API 請求都重做反向 DNS。
var hospitalDeviceCache = new ConcurrentDictionary<string, (bool IsHospitalComputer, string? ComputerName, DateTimeOffset ExpiresAt)>();

app.Use(async (context, next) =>
{
    var path = context.Request.Path;

    // LINE / LIFF 行動流程在外網可使用。
    if (IsExternalAllowedPath(path))
    {
        await next();
        return;
    }

    var device = await DetectHospitalComputerAsync(context, app.Environment);

    if (device.IsHospitalComputer)
    {
        await next();
        return;
    }

    // API 一律由後端拒絕，不能只靠前端隱藏按鈕。
    if (path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new
        {
            success = false,
            code = "INTRANET_ONLY",
            message = "此管理功能僅限院內網路使用。"
        });
        return;
    }

    // 外網直接開首頁或其他管理頁時，不提供電腦端登入 / 管理介面。
    context.Response.StatusCode = StatusCodes.Status403Forbidden;
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync("""
<!doctype html>
<html lang="zh-Hant">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>會議簽到系統</title>
  <style>
    body{margin:0;font-family:system-ui,-apple-system,"Segoe UI","Noto Sans TC",sans-serif;background:#f7f4f2;color:#332b29}
    main{min-height:100vh;display:grid;place-items:center;padding:24px}
    section{max-width:520px;background:#fff;border:1px solid #eadfda;border-radius:18px;padding:32px;box-shadow:0 12px 32px rgba(70,40,35,.08);text-align:center}
    h1{margin:0 0 12px;font-size:24px;color:#a8222c}
    p{margin:0;line-height:1.8;color:#665b57}
  </style>
</head>
<body>
  <main>
    <section>
      <h1>此管理系統僅限院內網路使用</h1>
      <p>如需進行會議簽到，請使用會議現場 QR Code 進入 LINE 簽到流程。</p>
    </section>
  </main>
</body>
</html>
""");
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        // LIFF/LINE 內建瀏覽器容易快取 HTML。HTML 一律要求重新驗證，
        // 避免已部署新版但手機仍載入舊 mobile-checkin.html / liff-entry.html。
        if (context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
            context.Context.Response.Headers.Pragma = "no-cache";
            context.Context.Response.Headers.Expires = "0";
        }
        else
        {
            var extension = Path.GetExtension(context.File.Name);
            // 開發模式不快取 JS / CSS，改完前端重新整理就會生效。
            if (app.Environment.IsDevelopment()
                && (extension.Equals(".css", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".js", StringComparison.OrdinalIgnoreCase)))
            {
                context.Context.Response.Headers.CacheControl = "no-cache";
            }
            else if (extension.Equals(".css", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                context.Context.Response.Headers.CacheControl = "public, max-age=604800";
            }
        }
    }
});

string GetConnection(IConfiguration configuration, string name)
{
    var value = configuration.GetConnectionString(name);
    if (string.IsNullOrWhiteSpace(value))
        throw new InvalidOperationException($"尚未設定 {name} 連線字串，請以環境變數 ConnectionStrings__{name} 或 user-secrets 設定。");
    return value;
}

static string Output(SqlParameter parameter) =>
    parameter.Value is null or DBNull ? "" : Convert.ToString(parameter.Value)?.Trim() ?? "";

static object DbValue(string? value) =>
    string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();


static string BuildMeetingLiffUrl(IConfiguration configuration, HttpRequest request, string token)
{
    var encodedToken = Uri.EscapeDataString(token.Trim());
    var liffId = configuration["LineLiff:LiffId"];
    if (!string.IsNullOrWhiteSpace(liffId))
        return $"https://liff.line.me/{liffId.Trim()}?type=meeting&token={encodedToken}";

    // No LIFF configured (local demo): point the QR code straight at the mobile entry page.
    var baseUrl = configuration["PublicBaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl))
        baseUrl = $"{request.Scheme}://{request.Host}";
    return $"{baseUrl.TrimEnd('/')}/liff-entry.html?type=meeting&token={encodedToken}";
}

static string CreateMeetingToken(string meetingNo)
{
    var raw = $"{meetingNo}|{Guid.NewGuid():N}";
    var hash = MD5.HashData(Encoding.UTF8.GetBytes(raw));
    return $"M-{Convert.ToHexString(hash).ToLowerInvariant()}";
}

static async Task<string> CreateUniqueMeetingTokenAsync(
    SqlConnection connection, SqlTransaction transaction, string meetingNo)
{
    for (var attempt = 0; attempt < 10; attempt++)
    {
        var token = CreateMeetingToken(meetingNo);
        const string sql = """
            SELECT COUNT(*)
            FROM dbo.Meeting WITH (UPDLOCK, HOLDLOCK)
            WHERE QRCode = @Token;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Token", SqlDbType.VarChar, 100).Value = token;
        if (Convert.ToInt32(await command.ExecuteScalarAsync()) == 0)
            return token;
    }

    throw new InvalidOperationException("無法產生唯一的 QR Code Token，請重新建立會議。");
}

static string ReaderText(SqlDataReader reader, params string[] candidates)
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

static DateTime CombineDateTime(string date, string time)
{
    if (!DateTime.TryParse($"{date} {time}", out var value))
    {
        throw new ArgumentException("日期或時間格式不正確。");
    }

    return value;
}

static string CleanDatabaseMessage(Exception exception)
{
    if (exception is SqlException sqlException && sqlException.Number >= 50000)
    {
        return sqlException.Message;
    }

    return exception.Message;
}

(string IdColumn, string NameColumn)? cachedRoomInfoColumns = null;
var roomInfoColumnsLock = new SemaphoreSlim(1, 1);

async Task<(string IdColumn, string NameColumn)> GetRoomInfoColumns(SqlConnection connection)
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

async Task<string> ResolveLocationDisplay(SqlConnection connection, int locationType, string? roomId, string? customLocation)
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

static string QuoteSqlName(string name) =>
    $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

async Task<(bool IsHospitalComputer, string IpAddress, string? ComputerName, string DetectionSource)> DetectHospitalComputerAsync(
    HttpContext context,
    IWebHostEnvironment environment)
{
    var remoteIp = context.Connection.RemoteIpAddress;
    if (remoteIp is null)
        return (false, "", null, "unknown");

    if (remoteIp.IsIPv4MappedToIPv6)
        remoteIp = remoteIp.MapToIPv4();

    var isLoopback = IPAddress.IsLoopback(remoteIp);
    if (environment.IsDevelopment() && isLoopback)
        return (true, remoteIp.ToString(), Environment.MachineName?.Trim(), "developmentLocalhost");

    var ipAddress = remoteIp.ToString();

    // 內網限制改由設定檔控制（Intranet:Enabled / AllowedIpPrefixes / ComputerNamePattern）。
    // 未啟用時不限制來源，方便本機與展示環境使用。
    var intranet = app.Configuration.GetSection("Intranet");
    if (!intranet.GetValue<bool>("Enabled"))
        return (true, ipAddress, null, "intranetCheckDisabled");

    var allowedPrefixes = intranet.GetSection("AllowedIpPrefixes").Get<string[]>() ?? [];
    if (!allowedPrefixes.Any(prefix => !string.IsNullOrWhiteSpace(prefix)
            && ipAddress.StartsWith(prefix.Trim(), StringComparison.Ordinal)))
        return (false, ipAddress, null, "remoteIpRejected");

    var computerNamePattern = intranet["ComputerNamePattern"];
    if (string.IsNullOrWhiteSpace(computerNamePattern))
        return (true, ipAddress, null, "remoteIpAllowed");

    var now = DateTimeOffset.UtcNow;
    if (hospitalDeviceCache.TryGetValue(ipAddress, out var cachedDevice)
        && cachedDevice.ExpiresAt > now)
    {
        return (cachedDevice.IsHospitalComputer, ipAddress, cachedDevice.ComputerName, "remoteConnectionCached");
    }

    string? computerName = null;
    try
    {
        var hostEntry = await Dns.GetHostEntryAsync(remoteIp).WaitAsync(TimeSpan.FromSeconds(3));
        var hostName = hostEntry.HostName?.Trim();
        computerName = string.IsNullOrWhiteSpace(hostName) ? null : hostName.Split('.')[0].Trim();
    }
    catch
    {
        computerName = null;
    }

    var computerNameMatched = !string.IsNullOrWhiteSpace(computerName)
        && Regex.IsMatch(computerName, computerNamePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    hospitalDeviceCache[ipAddress] = (computerNameMatched, computerName, computerNameMatched ? now.AddMinutes(30) : now.AddSeconds(30));
    return (computerNameMatched, ipAddress, computerName, "remoteConnection");
}

static bool IsExternalAllowedPath(PathString path)
{
    var value = path.Value ?? "";

    // 外網只提供 LINE / LIFF 手機簽到流程與必要靜態資源。
    if (value.Equals("/liff-entry.html", StringComparison.OrdinalIgnoreCase)
        || value.Equals("/mobile-checkin.html", StringComparison.OrdinalIgnoreCase)
        || value.Equals("/app.css", StringComparison.OrdinalIgnoreCase)
        || value.Equals("/app.js", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
        return true;

    if (value.Equals("/api/liff/meeting/authorize", StringComparison.OrdinalIgnoreCase))
        return true;

    if (value.StartsWith("/api/meetings/by-token/", StringComparison.OrdinalIgnoreCase))
        return true;

    // 手機本人簽到 / 簽退 / 代簽 / 讀本人簽到狀態。
    if (Regex.IsMatch(value, @"^/api/meetings/[^/]+/my-attendance$", RegexOptions.IgnoreCase))
        return true;

    if (Regex.IsMatch(value, @"^/api/meetings/[^/]+/guest-profile$", RegexOptions.IgnoreCase))
        return true;

    if (Regex.IsMatch(value, @"^/api/meetings/[^/]+/attendance/(signin|signout|proxy)$", RegexOptions.IgnoreCase))
        return true;

    return false;
}

app.MapGet("/api/client-device", async (HttpContext context, IWebHostEnvironment environment) =>
{
    var result = await DetectHospitalComputerAsync(context, environment);

    return Results.Ok(new
    {
        success = true,
        ipAddress = result.IpAddress,
        computerName = result.ComputerName,
        isHospitalComputer = result.IsHospitalComputer,
        detectionSource = result.DetectionSource
    });
});

app.MapGet("/api/health/database", async (IConfiguration configuration) =>
{
    try
    {
        await using var lineDb = new SqlConnection(GetConnection(configuration, "LineDb"));
        await using var meetingDb = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));

        await lineDb.OpenAsync();
        await meetingDb.OpenAsync();

        await using var lineDbCommand = new SqlCommand("SELECT DB_NAME();", lineDb);
        await using var meetingDbCommand = new SqlCommand("SELECT DB_NAME();", meetingDb);

        var lineDbName = Convert.ToString(await lineDbCommand.ExecuteScalarAsync());
        var meetingDbName = Convert.ToString(await meetingDbCommand.ExecuteScalarAsync());

        return Results.Ok(new
        {
            success = true,
            lineDb = lineDbName,
            lineMeetingDb = meetingDbName,
            message = "LineDB 與 LineMeeting 連線成功。"
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            success = false,
            message = "資料庫連線失敗。",
            detail = ex.Message
        }, statusCode: 500);
    }
});

app.MapPost("/api/auth/login", async (LoginRequest request, IConfiguration configuration) =>
{
    var account = request.Account?.Trim().ToUpperInvariant();
    var password = request.Password ?? "";

    if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(password))
    {
        return Results.BadRequest(new { success = false, message = "請輸入帳號與密碼。" });
    }

    try
    {
        await using var connection = new SqlConnection(GetConnection(configuration, "LineDb"));
        await using var command = new SqlCommand("dbo.Login_Check", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 30
        };

        command.Parameters.Add("@LogAccount", SqlDbType.NVarChar, 100).Value = account;
        command.Parameters.Add("@LogPASS", SqlDbType.NVarChar, 200).Value = password;

        var messageParameter = new SqlParameter("@RetuenMsg", SqlDbType.NVarChar, 500)
        {
            Direction = ParameterDirection.Output
        };
        var nameParameter = new SqlParameter("@cnm", SqlDbType.NVarChar, 100)
        {
            Direction = ParameterDirection.Output
        };
        var departmentParameter = new SqlParameter("@dpid", SqlDbType.NVarChar, 50)
        {
            Direction = ParameterDirection.Output
        };
        var employeeIdParameter = new SqlParameter("@idno", SqlDbType.NVarChar, 50)
        {
            Direction = ParameterDirection.Output
        };
        var operatorIdParameter = new SqlParameter("@opid", SqlDbType.NVarChar, 50)
        {
            Direction = ParameterDirection.Output
        };

        command.Parameters.AddRange([
            messageParameter,
            nameParameter,
            departmentParameter,
            employeeIdParameter,
            operatorIdParameter
        ]);

        await connection.OpenAsync();
        await command.ExecuteNonQueryAsync();

        var loginMessage = Output(messageParameter);
        var name = Output(nameParameter);
        var departmentId = Output(departmentParameter);
        var operatorId = Output(operatorIdParameter);

        var success = loginMessage.StartsWith("True", StringComparison.OrdinalIgnoreCase)
            || (!loginMessage.StartsWith("False", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(name));

        if (!success)
        {
            return Results.Json(
                new { success = false, message = "帳號或密碼輸入錯誤。" },
                statusCode: 401);
        }

        // Login_Check 的正確欄位對應：
        // @cnm  = 登入顯示名稱
        // @dpid = 單位代碼
        // @idno = 身分證字號（不回傳前端）
        // @opid = 員工編號／操作人員編號
        var employeeId = operatorId;
        var userId = operatorId;
        var department = "";
        var title = "";

        if (string.IsNullOrWhiteSpace(employeeId))
        {
            return Results.Json(new
            {
                success = false,
                message = "登入成功，但 Login_Check 未回傳員工編號（OPID）。"
            }, statusCode: 400);
        }

        // 科別名稱一律依醫院提供的 GetEmployee @TYPE='DP' 取得。
        try
        {
            await using var departmentCommand = new SqlCommand("dbo.GetEmployee", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 30
            };
            departmentCommand.Parameters.Add("@TYPE", SqlDbType.NVarChar, 10).Value = "DP";

            await using var departmentReader = await departmentCommand.ExecuteReaderAsync();
            while (await departmentReader.ReadAsync())
            {
                var rowDepartmentId = ReaderText(departmentReader, "DPID", "DepartmentID", "DeptID");
                if (!rowDepartmentId.Equals(departmentId, StringComparison.OrdinalIgnoreCase)) continue;
                department = ReaderText(departmentReader, "DPCNM", "DepartmentName", "DeptName", "DPNM");
                break;
            }
        }
        catch
        {
            // 若醫院 SP 欄位調整，下面仍會以 UserInfo 作為備援。
        }

        // 職稱優先依 GetEmployee @TYPE='EMP', @DPID 取得。
        try
        {
            await using var employeeCommand = new SqlCommand("dbo.GetEmployee", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 30
            };
            employeeCommand.Parameters.Add("@TYPE", SqlDbType.NVarChar, 10).Value = "EMP";
            employeeCommand.Parameters.Add("@DPID", SqlDbType.NVarChar, 50).Value =
                string.IsNullOrWhiteSpace(departmentId) ? DBNull.Value : departmentId;

            await using var employeeReader = await employeeCommand.ExecuteReaderAsync();
            while (await employeeReader.ReadAsync())
            {
                var rowEmployeeId = ReaderText(employeeReader,
                    "SNTOPID", "OPID", "EmployeeNo", "EmployeeID", "UserID");
                if (!rowEmployeeId.Equals(employeeId, StringComparison.OrdinalIgnoreCase)) continue;

                title = ReaderText(employeeReader, "DUTNM", "DutyName", "JobTitle", "Title");
                if (string.IsNullOrWhiteSpace(department))
                {
                    department = ReaderText(employeeReader, "DPCNM", "DepartmentName", "DeptName", "DPNM");
                }
                break;
            }
        }
        catch
        {
            // 由 UserInfo 備援。
        }

        // 若登入 SP 只回傳單位代碼，直接以 UserInfo 的單位對照補成中文名稱。
        if (!string.IsNullOrWhiteSpace(departmentId)
            && (string.IsNullOrWhiteSpace(department)
                || department.Equals(departmentId, StringComparison.OrdinalIgnoreCase)))
        {
            const string departmentNameSql = """
                SELECT TOP (1) LTRIM(RTRIM(DPCNM))
                FROM dbo.UserInfo
                WHERE LTRIM(RTRIM(DPID)) = @DepartmentID
                  AND NULLIF(LTRIM(RTRIM(DPCNM)), N'') IS NOT NULL;
                """;
            await using var departmentNameCommand = new SqlCommand(departmentNameSql, connection);
            departmentNameCommand.Parameters.Add("@DepartmentID", SqlDbType.NVarChar, 50).Value = departmentId;
            var resolvedDepartmentName = Convert.ToString(await departmentNameCommand.ExecuteScalarAsync())?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(resolvedDepartmentName))
                department = resolvedDepartmentName;
        }

        if (string.IsNullOrWhiteSpace(department) || string.IsNullOrWhiteSpace(title))
        {
            const string fallbackSql = """
                SELECT TOP (1)
                    LTRIM(RTRIM(DPCNM)) AS DepartmentName,
                    LTRIM(RTRIM(DUTNM)) AS DutyName
                FROM dbo.UserInfo
                WHERE LTRIM(RTRIM(SNTOPID)) = @EmployeeNo
                   OR LTRIM(RTRIM(UserID)) = @EmployeeNo;
                """;
            await using var fallbackCommand = new SqlCommand(fallbackSql, connection);
            fallbackCommand.Parameters.Add("@EmployeeNo", SqlDbType.NVarChar, 50).Value = employeeId;
            await using var fallbackReader = await fallbackCommand.ExecuteReaderAsync();
            if (await fallbackReader.ReadAsync())
            {
                if (string.IsNullOrWhiteSpace(department))
                    department = fallbackReader["DepartmentName"] is DBNull ? "" : fallbackReader["DepartmentName"].ToString()?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(title))
                    title = fallbackReader["DutyName"] is DBNull ? "" : fallbackReader["DutyName"].ToString()?.Trim() ?? "";
            }
        }

        return Results.Ok(new
        {
            success = true,
            message = "登入成功",
            user = new
            {
                account,
                name,
                employeeId,
                sntopid = employeeId,
                department = department,
                departmentId = departmentId,
                dpid = departmentId,
                dpcnm = department,
                title,
                dutnm = title,
                operatorId = employeeId,
                userID = employeeId
            }
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            success = false,
            message = "登入處理發生錯誤，請洽資訊室。",
            detail = ex.Message
        }, statusCode: 500);
    }
});

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
        WHERE LTRIM(RTRIM(SNTOPID)) = @EmployeeNo
           OR LTRIM(RTRIM(UserID)) = @EmployeeNo;
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

app.MapGet("/api/qr-code/{token}", async (string token, string userId, HttpRequest request, IConfiguration configuration) =>
{
    if (string.IsNullOrWhiteSpace(token)) return Results.BadRequest("缺少 QR Token。");
    if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

    await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
    await connection.OpenAsync();
    const string permissionSql = """
        SELECT COUNT(1)
        FROM dbo.Meeting
        WHERE QRCode = @Token
          AND ISNULL(IsDeleted, 0) = 0
          AND (@User = CreatedByEmployeeNo OR @User IN (Host1EmployeeNo, Host2EmployeeNo, Host3EmployeeNo));
        """;
    await using var check = new SqlCommand(permissionSql, connection);
    check.Parameters.Add("@Token", SqlDbType.VarChar, 100).Value = token.Trim();
    check.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = userId.Trim();
    if (Convert.ToInt32(await check.ExecuteScalarAsync()) == 0)
        return Results.Json(new { success = false, message = "您沒有權限查看此會議 QR Code。" }, statusCode: 403);

    var targetUrl = BuildMeetingLiffUrl(configuration, request, token);

    using var generator = new QRCodeGenerator();
    using var data = generator.CreateQrCode(targetUrl, QRCodeGenerator.ECCLevel.Q);
    var png = new PngByteQRCode(data).GetGraphic(12);
    return Results.File(png, "image/png");
});

app.MapGet("/api/qr-target/{token}", (string token, HttpRequest request, IConfiguration configuration) =>
{
    if (string.IsNullOrWhiteSpace(token))
        return Results.BadRequest(new { success = false, message = "缺少 QR Token。" });

    var url = BuildMeetingLiffUrl(configuration, request, token);
    return Results.Ok(new { success = true, url, isLocalhost = false, type = "meeting" });
});

// 醫院 LINE 平台已完成 LINE 使用者識別，本系統直接接收 token + userId。
// userId 對應 LineDB.dbo.UserInfo.UserID，不是員工編號 SNTOPID。
app.MapPost("/api/liff/meeting/authorize", async (
    LiffMeetingAuthorizeRequest request,
    IDataProtectionProvider dataProtectionProvider,
    IConfiguration configuration) =>
{
    if (!string.Equals(request.Type?.Trim(), "meeting", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { success = false, code = "INVALID_TYPE", message = "不支援的會議入口類型。" });
    if (string.IsNullOrWhiteSpace(request.MeetingToken))
        return Results.BadRequest(new { success = false, code = "MISSING_MEETING_TOKEN", message = "缺少會議 Token。" });
    if (string.IsNullOrWhiteSpace(request.UserId))
        return Results.BadRequest(new { success = false, code = "MISSING_USER_ID", message = "缺少 LINE UserID，請由 LINE 重新進入會議。" });

    // 依醫院既定規格，網址中的 userId 由院內 LINE 流程提供並視為可信任身分來源。
    var effectiveUserId = request.UserId.Trim();

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
            WHERE LTRIM(RTRIM(UserID)) = @LineUserID;
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

    if (DateTime.Now > end.AddMinutes(30))
    {
        return Results.Json(new
        {
            success = false,
            code = "MEETING_CLOSED",
            message = $"此會議已結束。{meetingName}｜{start:yyyy/MM/dd HH:mm}～{end:HH:mm}"
        }, statusCode: StatusCodes.Status410Gone);
    }

    var requiresProfile = !linkedInternalUser;
    var sessionEmployeeNo = linkedInternalUser ? employeeNo : CreateGuestKey(effectiveUserId);
    var mobileSession = CreateMobileMeetingSession(
        dataProtectionProvider,
        linkedInternalUser ? 1 : 2,
        sessionEmployeeNo,
        meetingNo,
        request.MeetingToken.Trim(),
        end,
        externalName: null,
        externalOrganization: null,
        externalTitle: null);

    return Results.Ok(new
    {
        success = true,
        requiresProfile,
        mobileSession,
        user = new
        {
            lineUserId = effectiveUserId,
            lineDisplayName = "",
            employeeNo = linkedInternalUser ? employeeNo : sessionEmployeeNo,
            employeeId = linkedInternalUser ? employeeNo : sessionEmployeeNo,
            sntopid = linkedInternalUser ? employeeNo : "",
            userID = effectiveUserId,
            name = linkedInternalUser ? employeeName : "",
            departmentId = linkedInternalUser ? departmentId : "",
            dpid = linkedInternalUser ? departmentId : "",
            department = linkedInternalUser ? departmentName : "",
            dpcnm = linkedInternalUser ? departmentName : "",
            title = linkedInternalUser ? dutyName : "",
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
});

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
        if (DateTime.Now > endTime.AddMinutes(30))
            throw new InvalidOperationException("此會議已結束，無法再建立簽到資料。");

        // ParticipantType=2（院外/未綁定使用者）依既有資料表 CHECK 規則，EmployeeNo 必須為 NULL。
        // 這裡使用 CreatedByEmployeeNo 保存本次 LINE 訪客的匿名 GuestKey，作為同一場會議內的隱藏識別值。
        const string upsertSql = """
            IF EXISTS (
                SELECT 1 FROM dbo.MeetingParticipant
                WHERE MeetingNo=@MeetingNo
                  AND ParticipantType=2
                  AND EmployeeNo IS NULL
                  AND CreatedByEmployeeNo=@GuestKey
            )
            BEGIN
                UPDATE dbo.MeetingParticipant
                SET ExternalName=@Name,
                    ExternalOrganization=@Organization,
                    ExternalTitle=@Title,
                    UpdatedByEmployeeNo=@GuestKey,
                    UpdatedTime=SYSDATETIME()
                WHERE MeetingNo=@MeetingNo
                  AND ParticipantType=2
                  AND EmployeeNo IS NULL
                  AND CreatedByEmployeeNo=@GuestKey;
            END
            ELSE
            BEGIN
                INSERT dbo.MeetingParticipant
                    (MeetingNo, ParticipantType, EmployeeNo, ExternalName, ExternalOrganization, ExternalTitle,
                     CreatedByEmployeeNo, CreatedTime, UpdatedByEmployeeNo, UpdatedTime)
                VALUES
                    (@MeetingNo, 2, NULL, @Name, @Organization, @Title,
                     @GuestKey, SYSDATETIME(), @GuestKey, SYSDATETIME());
            END;
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
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Json(new { success = false, message = CleanDatabaseMessage(ex) }, statusCode: 400);
    }
});

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
        var session = CreateMobileMeetingSession(dataProtectionProvider, 2, guestKey, meetingNo, meetingToken, end);
        return Results.Ok(new
        {
            success = true, requiresProfile = true, mobileSession = session,
            user = new { lineUserId = devLineUserId, employeeNo = guestKey, employeeId = guestKey, userID = devLineUserId, name = "", department = "", title = "", participantType = "External", requiresProfile = true },
            meeting = new { meetingNo, id = meetingNo, name = meetingName, subName = meetingSubName, date = start.ToString("yyyy-MM-dd"), start = start.ToString("HH:mm"), end = end.ToString("HH:mm"), checkInOpenAt = start.AddMinutes(-CheckInRules.OpenMinutesBeforeStart).ToString("yyyy-MM-ddTHH:mm:ss"), startDateTime = start.ToString("yyyy-MM-ddTHH:mm:ss"), endDateTime = end.ToString("yyyy-MM-ddTHH:mm:ss"), location, note, relation = "externalGuest", allowUnlistedParticipants = true, expectedParticipantCount, requiresProfile = true }
        });
    });

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
                WHERE LTRIM(RTRIM(SNTOPID)) = @EmployeeNo
                   OR LTRIM(RTRIM(UserID)) = @EmployeeNo
                ORDER BY CASE WHEN LTRIM(RTRIM(SNTOPID)) = @EmployeeNo THEN 1 ELSE 2 END;
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
    });
}

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
        WHERE LTRIM(RTRIM(DPID)) = @DepartmentID
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

app.MapPost("/api/meetings", async (MeetingSaveRequest request, IConfiguration configuration) =>
    await SaveMeeting(request, null, configuration));

app.MapPut("/api/meetings/{meetingNo}", async (string meetingNo, MeetingSaveRequest request, IConfiguration configuration) =>
    await SaveMeeting(request, meetingNo, configuration));

async Task<IResult> SaveMeeting(MeetingSaveRequest request, string? existingMeetingNo, IConfiguration configuration)
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

    await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
    await connection.OpenAsync();
    await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

    try
    {
        var meetingNo = existingMeetingNo?.Trim();
        var isNew = string.IsNullOrWhiteSpace(meetingNo);

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
            const string checkSql = """
                SELECT COUNT(*)
                FROM dbo.Meeting WITH (UPDLOCK, HOLDLOCK)
                WHERE MeetingNo = @MeetingNo
                  AND ISNULL(IsDeleted, 0) = 0
                  AND (CreatedByEmployeeNo = @User
                       OR Host1EmployeeNo = @User
                       OR Host2EmployeeNo = @User
                       OR Host3EmployeeNo = @User);
                """;
            await using var check = new SqlCommand(checkSql, connection, tx);
            check.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo!;
            check.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = user;
            if (Convert.ToInt32(await check.ExecuteScalarAsync()) == 0)
                throw new InvalidOperationException("找不到會議，或您沒有修改權限。");
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
                    @QRCode, @User, SYSDATETIME(),
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
                    UpdatedTime = SYSDATETIME()
                WHERE MeetingNo = @MeetingNo;
                """;
            await using var command = new SqlCommand(updateSql, connection, tx);
            AddMeetingParameters(command, meetingNo!, request, start, end, locationType, "");
            if (await command.ExecuteNonQueryAsync() == 0)
                throw new InvalidOperationException("會議無法修改，請確認權限或會議是否存在。");
        }

        // 同步院內參與名單。歷史院外人員資料不在此頁維護，因此不主動刪除。
        var existingInternal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string existingSql = """
            SELECT EmployeeNo
            FROM dbo.MeetingParticipant WITH (UPDLOCK, HOLDLOCK)
            WHERE MeetingNo = @MeetingNo AND ParticipantType = 1;
            """;
        await using (var existingCommand = new SqlCommand(existingSql, connection, tx))
        {
            existingCommand.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo!;
            await using var reader = await existingCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var employeeNo = reader["EmployeeNo"] is DBNull ? "" : reader["EmployeeNo"].ToString()?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(employeeNo)) existingInternal.Add(employeeNo);
            }
        }

        var submittedInternal = internalPeople.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedInternal = existingInternal.Where(x => !submittedInternal.Contains(x)).ToList();

        // 已經簽到或簽退的人不可從名單移除，避免修改會議時連帶刪掉簽到紀錄。
        if (removedInternal.Count > 0)
        {
            var signedRemoved = new List<string>();
            const string signedSql = """
                SELECT a.EmployeeNo, ISNULL(NULLIF(LTRIM(RTRIM(u.CNM)), N''), a.EmployeeNo) AS DisplayName
                FROM dbo.MeetingAttendance AS a
                LEFT JOIN LineDB.dbo.UserInfo AS u ON LTRIM(RTRIM(u.SNTOPID)) = a.EmployeeNo
                WHERE a.MeetingNo = @MeetingNo
                  AND a.ParticipantType = 1
                  AND (a.SignInTime IS NOT NULL OR a.SignOutTime IS NOT NULL);
                """;
            await using (var signedCommand = new SqlCommand(signedSql, connection, tx))
            {
                signedCommand.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo!;
                await using var signedReader = await signedCommand.ExecuteReaderAsync();
                var removedSet = removedInternal.ToHashSet(StringComparer.OrdinalIgnoreCase);
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

        foreach (var removed in removedInternal)
        {
            // 只會移除尚未簽到的人；清掉可能殘留的空白簽到列，再移除名單。
            await using (var deleteAttendance = new SqlCommand(
                "DELETE FROM dbo.MeetingAttendance WHERE MeetingNo=@MeetingNo AND ParticipantType=1 AND EmployeeNo=@EmployeeNo;",
                connection, tx))
            {
                deleteAttendance.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo!;
                deleteAttendance.Parameters.Add("@EmployeeNo", SqlDbType.VarChar, 20).Value = removed;
                await deleteAttendance.ExecuteNonQueryAsync();
            }
            await using var deleteParticipant = new SqlCommand(
                "DELETE FROM dbo.MeetingParticipant WHERE MeetingNo=@MeetingNo AND ParticipantType=1 AND EmployeeNo=@EmployeeNo;",
                connection, tx);
            deleteParticipant.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo!;
            deleteParticipant.Parameters.Add("@EmployeeNo", SqlDbType.VarChar, 20).Value = removed;
            await deleteParticipant.ExecuteNonQueryAsync();
        }

        foreach (var employeeNo in submittedInternal)
        {
            if (existingInternal.Contains(employeeNo))
            {
                await using var touch = new SqlCommand("""
                    UPDATE dbo.MeetingParticipant
                    SET UpdatedByEmployeeNo=@User, UpdatedTime=SYSDATETIME()
                    WHERE MeetingNo=@MeetingNo AND ParticipantType=1 AND EmployeeNo=@EmployeeNo;
                    """, connection, tx);
                touch.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo!;
                touch.Parameters.Add("@EmployeeNo", SqlDbType.VarChar, 20).Value = employeeNo;
                touch.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = user;
                await touch.ExecuteNonQueryAsync();
            }
            else
            {
                await using var insertParticipant = new SqlCommand("""
                    INSERT dbo.MeetingParticipant
                    (MeetingNo, ParticipantType, EmployeeNo, ExternalName, ExternalOrganization,
                     CreatedByEmployeeNo, CreatedTime, UpdatedByEmployeeNo, UpdatedTime)
                    VALUES (@MeetingNo,1,@EmployeeNo,NULL,NULL,@User,SYSDATETIME(),NULL,NULL);
                    """, connection, tx);
                insertParticipant.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo!;
                insertParticipant.Parameters.Add("@EmployeeNo", SqlDbType.VarChar, 20).Value = employeeNo;
                insertParticipant.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = user;
                await insertParticipant.ExecuteNonQueryAsync();
            }
        }

        await tx.CommitAsync();
        return Results.Ok(new { success = true, meetingNo, meetingID = meetingNo });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Json(new { success = false, message = CleanDatabaseMessage(ex) }, statusCode: 400);
    }
}

void AddMeetingParameters(
    SqlCommand command,
    string meetingNo,
    MeetingSaveRequest request,
    DateTime start,
    DateTime end,
    int locationType,
    string qrCode)
{
    var expectedParticipantCount = (request.ParticipantUserIDs ?? [])
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
    command.Parameters.Add("@RoomID", SqlDbType.VarChar, 100).Value =
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

app.MapGet("/api/meetings", async (
    string userId,
    string? keyword,
    DateTime? startDate,
    DateTime? endDate,
    IConfiguration configuration) =>
{
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
            (SELECT COUNT(*) FROM dbo.MeetingParticipant p WHERE p.MeetingNo = m.MeetingNo) AS ParticipantCount
        FROM dbo.Meeting m
        OUTER APPLY
        (
            SELECT TOP (1) sourceUser.*
            FROM LineDB.dbo.UserInfo sourceUser
            WHERE LTRIM(RTRIM(sourceUser.SNTOPID)) = m.Host1EmployeeNo
               OR LTRIM(RTRIM(sourceUser.UserID)) = m.Host1EmployeeNo
            ORDER BY CASE WHEN LTRIM(RTRIM(sourceUser.SNTOPID)) = m.Host1EmployeeNo THEN 1 ELSE 2 END
        ) u1
        OUTER APPLY
        (
            SELECT TOP (1) sourceUser.*
            FROM LineDB.dbo.UserInfo sourceUser
            WHERE LTRIM(RTRIM(sourceUser.SNTOPID)) = m.Host2EmployeeNo
               OR LTRIM(RTRIM(sourceUser.UserID)) = m.Host2EmployeeNo
            ORDER BY CASE WHEN LTRIM(RTRIM(sourceUser.SNTOPID)) = m.Host2EmployeeNo THEN 1 ELSE 2 END
        ) u2
        OUTER APPLY
        (
            SELECT TOP (1) sourceUser.*
            FROM LineDB.dbo.UserInfo sourceUser
            WHERE LTRIM(RTRIM(sourceUser.SNTOPID)) = m.Host3EmployeeNo
               OR LTRIM(RTRIM(sourceUser.UserID)) = m.Host3EmployeeNo
            ORDER BY CASE WHEN LTRIM(RTRIM(sourceUser.SNTOPID)) = m.Host3EmployeeNo THEN 1 ELSE 2 END
        ) u3
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
});

app.MapGet("/api/meetings/{meetingNo}", async (string meetingNo, string userId, IConfiguration configuration) =>
    await GetMeetingDetail(meetingNo, userId, configuration));

async Task<IResult> GetMeetingDetail(string meetingNo, string userId, IConfiguration configuration)
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
            (SELECT COUNT(*) FROM dbo.MeetingParticipant p WHERE p.MeetingNo = m.MeetingNo) AS ParticipantCount
        FROM dbo.Meeting m
        OUTER APPLY
        (
            SELECT TOP (1) sourceUser.*
            FROM LineDB.dbo.UserInfo sourceUser
            WHERE LTRIM(RTRIM(sourceUser.SNTOPID)) = m.Host1EmployeeNo
               OR LTRIM(RTRIM(sourceUser.UserID)) = m.Host1EmployeeNo
            ORDER BY CASE WHEN LTRIM(RTRIM(sourceUser.SNTOPID)) = m.Host1EmployeeNo THEN 1 ELSE 2 END
        ) u1
        OUTER APPLY
        (
            SELECT TOP (1) sourceUser.*
            FROM LineDB.dbo.UserInfo sourceUser
            WHERE LTRIM(RTRIM(sourceUser.SNTOPID)) = m.Host2EmployeeNo
               OR LTRIM(RTRIM(sourceUser.UserID)) = m.Host2EmployeeNo
            ORDER BY CASE WHEN LTRIM(RTRIM(sourceUser.SNTOPID)) = m.Host2EmployeeNo THEN 1 ELSE 2 END
        ) u2
        OUTER APPLY
        (
            SELECT TOP (1) sourceUser.*
            FROM LineDB.dbo.UserInfo sourceUser
            WHERE LTRIM(RTRIM(sourceUser.SNTOPID)) = m.Host3EmployeeNo
               OR LTRIM(RTRIM(sourceUser.UserID)) = m.Host3EmployeeNo
            ORDER BY CASE WHEN LTRIM(RTRIM(sourceUser.SNTOPID)) = m.Host3EmployeeNo THEN 1 ELSE 2 END
        ) u3
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
            lastSavedAt = reader["UpdatedTime"] is DBNull ? reader["CreatedTime"] : reader["UpdatedTime"]
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
            detailData.lastSavedAt
        };
    }

    var participants = new List<object>();
    const string participantSql = """
        SELECT
            p.ParticipantType, p.EmployeeNo, p.ExternalName, p.ExternalOrganization, p.ExternalTitle,
            CASE WHEN p.ParticipantType = 1 THEN ISNULL(NULLIF(LTRIM(RTRIM(u.CNM)), N''), p.EmployeeNo) ELSE p.ExternalName END AS Name,
            CASE WHEN p.ParticipantType = 1 THEN ISNULL(LTRIM(RTRIM(u.DPCNM)), N'') ELSE p.ExternalOrganization END AS Organization,
            CASE WHEN p.ParticipantType = 1 THEN ISNULL(LTRIM(RTRIM(u.DPID)), N'') ELSE N'' END AS DepartmentID,
            CASE WHEN p.ParticipantType = 1 THEN ISNULL(LTRIM(RTRIM(u.DUTNM)), N'') ELSE ISNULL(p.ExternalTitle, N'') END AS Title,
            a.SignInTime,
            a.SignOutTime,
            a.ProxySignEmployeeNo
        FROM dbo.MeetingParticipant p
        OUTER APPLY
        (
            SELECT TOP (1) sourceUser.*
            FROM LineDB.dbo.UserInfo sourceUser
            WHERE p.ParticipantType = 1
              AND (LTRIM(RTRIM(sourceUser.SNTOPID)) = LTRIM(RTRIM(p.EmployeeNo))
                   OR LTRIM(RTRIM(sourceUser.UserID)) = LTRIM(RTRIM(p.EmployeeNo))
                   OR (TRY_CONVERT(BIGINT, LTRIM(RTRIM(sourceUser.SNTOPID))) IS NOT NULL
                       AND TRY_CONVERT(BIGINT, LTRIM(RTRIM(sourceUser.SNTOPID))) = TRY_CONVERT(BIGINT, LTRIM(RTRIM(p.EmployeeNo)))))
            ORDER BY CASE
                WHEN LTRIM(RTRIM(sourceUser.SNTOPID)) = LTRIM(RTRIM(p.EmployeeNo)) THEN 1
                WHEN TRY_CONVERT(BIGINT, LTRIM(RTRIM(sourceUser.SNTOPID))) = TRY_CONVERT(BIGINT, LTRIM(RTRIM(p.EmployeeNo))) THEN 2
                ELSE 3
            END
        ) u
        LEFT JOIN dbo.MeetingAttendance a
          ON a.MeetingNo = p.MeetingNo
         AND a.ParticipantType = p.ParticipantType
         AND ISNULL(a.EmployeeNo, '') = ISNULL(p.EmployeeNo, '')
         AND ISNULL(a.ExternalName, N'') = ISNULL(p.ExternalName, N'')
         AND ISNULL(a.ExternalOrganization, N'') = ISNULL(p.ExternalOrganization, N'')
        WHERE p.MeetingNo = @MeetingNo
        ORDER BY p.ParticipantType, Name;
        """;
    await using (var command = new SqlCommand(participantSql, connection))
    {
        command.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var isInternal = Convert.ToInt32(reader["ParticipantType"]) == 1;
            participants.Add(new
            {
                userID = isInternal ? reader["EmployeeNo"].ToString() : $"EXT|{reader["ExternalName"]}|{reader["ExternalOrganization"]}",
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

app.MapDelete("/api/meetings/{meetingNo}", async (string meetingNo, string userId, IConfiguration configuration) =>
{
    await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
    await connection.OpenAsync();

    const string sql = """
        UPDATE dbo.Meeting
        SET IsDeleted = 1,
            DeletedTime = SYSDATETIME(),
            DeletedByEmployeeNo = @User,
            UpdatedByEmployeeNo = @User,
            UpdatedTime = SYSDATETIME()
        WHERE MeetingNo = @MeetingNo
          AND ISNULL(IsDeleted, 0) = 0
          AND
          (
              CreatedByEmployeeNo = @User
              OR Host1EmployeeNo = @User
              OR Host2EmployeeNo = @User
              OR Host3EmployeeNo = @User
          );
        """;
    await using var command = new SqlCommand(sql, connection);
    command.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
    command.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = userId.Trim();
    var affected = await command.ExecuteNonQueryAsync();
    if (affected == 0)
        return Results.BadRequest(new { success = false, message = "找不到會議，或您沒有刪除權限。" });

    return Results.Ok(new { success = true, message = "會議已刪除。" });
});

app.MapGet("/api/meetings/{meetingNo}/attendance", async (string meetingNo, string userId, IConfiguration configuration) =>
{
    await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
    await connection.OpenAsync();

    const string permissionSql = """
        SELECT COUNT(*)
        FROM dbo.Meeting
        WHERE MeetingNo = @MeetingNo
          AND ISNULL(IsDeleted, 0) = 0
          AND (@User IN (Host1EmployeeNo, Host2EmployeeNo, Host3EmployeeNo) OR CreatedByEmployeeNo = @User);
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
        SELECT m.MeetingNo, m.MeetingName, m.MeetingSubName, m.StartDateTime, m.EndDateTime,
               m.LocationType, m.RoomID, m.CustomLocation,
               ISNULL(m.AllowUnlistedParticipants, 0) AS AllowUnlistedParticipants,
               ISNULL(m.ExpectedParticipantCount, 0) AS ExpectedParticipantCount,
               ISNULL(NULLIF(LTRIM(RTRIM(u1.CNM)), N''), m.Host1EmployeeNo) AS Host1Name,
               CASE WHEN NULLIF(LTRIM(RTRIM(m.Host2EmployeeNo)), '') IS NULL THEN N''
                    ELSE ISNULL(NULLIF(LTRIM(RTRIM(u2.CNM)), N''), m.Host2EmployeeNo) END AS Host2Name,
               CASE WHEN NULLIF(LTRIM(RTRIM(m.Host3EmployeeNo)), '') IS NULL THEN N''
                    ELSE ISNULL(NULLIF(LTRIM(RTRIM(u3.CNM)), N''), m.Host3EmployeeNo) END AS Host3Name
        FROM dbo.Meeting m
        LEFT JOIN LineDB.dbo.UserInfo u1 ON LTRIM(RTRIM(u1.SNTOPID)) = m.Host1EmployeeNo
        LEFT JOIN LineDB.dbo.UserInfo u2 ON LTRIM(RTRIM(u2.SNTOPID)) = m.Host2EmployeeNo
        LEFT JOIN LineDB.dbo.UserInfo u3 ON LTRIM(RTRIM(u3.SNTOPID)) = m.Host3EmployeeNo
        WHERE m.MeetingNo = @MeetingNo
          AND ISNULL(m.IsDeleted, 0) = 0;
        """;
    await using (var command = new SqlCommand(basicSql, connection))
    {
        command.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return Results.NotFound(new { success = false, message = "找不到會議。" });
        var start = Convert.ToDateTime(reader["StartDateTime"]);
        var end = Convert.ToDateTime(reader["EndDateTime"]);
        var locationType = Convert.ToInt32(reader["LocationType"]);
        var roomId = reader["RoomID"] is DBNull ? "" : reader["RoomID"].ToString();
        var customLocation = reader["CustomLocation"] is DBNull ? "" : reader["CustomLocation"].ToString();
        var meetingId = reader["MeetingNo"].ToString();
        var meetingName = reader["MeetingName"].ToString();
        var meetingSubName = reader["MeetingSubName"] is DBNull ? "" : reader["MeetingSubName"].ToString();
        var allowUnlistedParticipants = Convert.ToBoolean(reader["AllowUnlistedParticipants"]);
        var expectedParticipantCount = Convert.ToInt32(reader["ExpectedParticipantCount"]);
        var hostNames = new[]
        {
            reader["Host1Name"] is DBNull ? "" : reader["Host1Name"].ToString()?.Trim() ?? "",
            reader["Host2Name"] is DBNull ? "" : reader["Host2Name"].ToString()?.Trim() ?? "",
            reader["Host3Name"] is DBNull ? "" : reader["Host3Name"].ToString()?.Trim() ?? ""
        }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        await reader.CloseAsync();
        var locationDisplay = await ResolveLocationDisplay(connection, locationType, roomId, customLocation);
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
            allowUnlistedParticipants,
            expectedParticipantCount
        };
    }

    var rows = new List<object>();
    const string sql = """
        SELECT
            p.ParticipantType, p.EmployeeNo, p.ExternalName, p.ExternalOrganization, p.ExternalTitle,
            CASE WHEN p.ParticipantType = 1 THEN ISNULL(NULLIF(LTRIM(RTRIM(u.CNM)), N''), p.EmployeeNo) ELSE p.ExternalName END AS OriginalName,
            CASE WHEN p.ParticipantType = 1 THEN ISNULL(LTRIM(RTRIM(u.DPCNM)), N'') ELSE p.ExternalOrganization END AS OriginalDepartment,
            CASE WHEN p.ParticipantType = 1 THEN ISNULL(LTRIM(RTRIM(u.DUTNM)), N'') ELSE ISNULL(p.ExternalTitle, N'') END AS OriginalTitle,
            a.SignInTime, a.SignOutTime, a.ProxySignEmployeeNo, a.Remark,
            ISNULL(NULLIF(LTRIM(RTRIM(proxyUser.CNM)), N''), a.ProxySignEmployeeNo) AS ProxyName,
            ISNULL(LTRIM(RTRIM(proxyUser.DPCNM)), N'') AS ProxyDepartment,
            ISNULL(LTRIM(RTRIM(proxyUser.DUTNM)), N'') AS ProxyTitle
        FROM dbo.MeetingParticipant p
        OUTER APPLY
        (
            SELECT TOP (1) sourceUser.*
            FROM LineDB.dbo.UserInfo sourceUser
            WHERE p.ParticipantType = 1
              AND (LTRIM(RTRIM(sourceUser.SNTOPID)) = LTRIM(RTRIM(p.EmployeeNo))
                   OR LTRIM(RTRIM(sourceUser.UserID)) = LTRIM(RTRIM(p.EmployeeNo))
                   OR (TRY_CONVERT(BIGINT, LTRIM(RTRIM(sourceUser.SNTOPID))) IS NOT NULL
                       AND TRY_CONVERT(BIGINT, LTRIM(RTRIM(sourceUser.SNTOPID))) = TRY_CONVERT(BIGINT, LTRIM(RTRIM(p.EmployeeNo)))))
            ORDER BY CASE
                WHEN LTRIM(RTRIM(sourceUser.SNTOPID)) = LTRIM(RTRIM(p.EmployeeNo)) THEN 1
                WHEN TRY_CONVERT(BIGINT, LTRIM(RTRIM(sourceUser.SNTOPID))) = TRY_CONVERT(BIGINT, LTRIM(RTRIM(p.EmployeeNo))) THEN 2
                ELSE 3
            END
        ) u
        LEFT JOIN dbo.MeetingAttendance a
          ON a.MeetingNo = p.MeetingNo
         AND a.ParticipantType = p.ParticipantType
         AND ISNULL(a.EmployeeNo, '') = ISNULL(p.EmployeeNo, '')
         AND ISNULL(a.ExternalName, N'') = ISNULL(p.ExternalName, N'')
         AND ISNULL(a.ExternalOrganization, N'') = ISNULL(p.ExternalOrganization, N'')
        OUTER APPLY
        (
            SELECT TOP (1) sourceProxy.*
            FROM LineDB.dbo.UserInfo sourceProxy
            WHERE LTRIM(RTRIM(sourceProxy.SNTOPID)) = a.ProxySignEmployeeNo
               OR LTRIM(RTRIM(sourceProxy.UserID)) = a.ProxySignEmployeeNo
            ORDER BY CASE WHEN LTRIM(RTRIM(sourceProxy.SNTOPID)) = a.ProxySignEmployeeNo THEN 1 ELSE 2 END
        ) proxyUser
        WHERE p.MeetingNo = @MeetingNo
        ORDER BY p.ParticipantType, OriginalName;
        """;
    await using var listCommand = new SqlCommand(sql, connection);
    listCommand.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
    await using var listReader = await listCommand.ExecuteReaderAsync();
    while (await listReader.ReadAsync())
    {
        var isInternal = Convert.ToInt32(listReader["ParticipantType"]) == 1;
        rows.Add(new
        {
            userID = isInternal ? listReader["EmployeeNo"].ToString() : $"EXT|{listReader["ExternalName"]}|{listReader["ExternalOrganization"]}",
            employeeId = isInternal ? listReader["EmployeeNo"].ToString() : "",
            name = listReader["OriginalName"].ToString(),
            department = listReader["OriginalDepartment"].ToString(),
            title = listReader["OriginalTitle"].ToString(),
            participantType = isInternal ? "Internal" : "External",
            externalName = isInternal ? null : listReader["ExternalName"].ToString(),
            externalOrganization = isInternal ? null : listReader["ExternalOrganization"].ToString(),
            @in = listReader["SignInTime"] is DBNull ? "" : Convert.ToDateTime(listReader["SignInTime"]).ToString("HH:mm"),
            @out = listReader["SignOutTime"] is DBNull ? "" : Convert.ToDateTime(listReader["SignOutTime"]).ToString("HH:mm"),
            attendanceStatus = listReader["SignInTime"] is not DBNull || listReader["ProxySignEmployeeNo"] is not DBNull ? "出席" : "未出席",
            proxyEmployeeNo = listReader["ProxySignEmployeeNo"] is DBNull ? "" : listReader["ProxySignEmployeeNo"].ToString(),
            proxyDepartment = listReader["ProxySignEmployeeNo"] is DBNull ? "" : listReader["ProxyDepartment"].ToString(),
            proxyName = listReader["ProxySignEmployeeNo"] is DBNull ? "" : listReader["ProxyName"].ToString(),
            note = listReader["Remark"] is DBNull ? "" : listReader["Remark"].ToString()
        });
    }

    return Results.Ok(new { success = true, meeting, rows });
});

app.MapPut("/api/meetings/{meetingNo}/attendance", async (
    string meetingNo, string userId, AttendanceSaveRequest request, IConfiguration configuration) =>
{
    await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
    await connection.OpenAsync();
    await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

    try
    {
        const string meetingSql = """
            SELECT StartDateTime
            FROM dbo.Meeting
            WHERE MeetingNo = @MeetingNo
              AND ISNULL(IsDeleted, 0) = 0
              AND (@User IN (Host1EmployeeNo, Host2EmployeeNo, Host3EmployeeNo) OR CreatedByEmployeeNo = @User);
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

        foreach (var row in request.Rows ?? [])
        {
            var isExternal = row.UserID.StartsWith("EXT|", StringComparison.OrdinalIgnoreCase);
            string? employeeNo = isExternal ? null : row.UserID.Trim();
            string? externalName = null;
            string? externalOrganization = null;
            if (isExternal)
            {
                var parts = row.UserID.Split('|', 3);
                if (parts.Length != 3) throw new InvalidOperationException("院外人員資料格式不正確。");
                externalName = parts[1];
                externalOrganization = parts[2];
            }

            DateTime? signIn = ParseAttendanceTime(meetingDate, row.In);
            DateTime? signOut = ParseAttendanceTime(meetingDate, row.Out);
            if (signIn.HasValue && signOut.HasValue && signOut < signIn)
                throw new InvalidOperationException("簽退時間不可早於簽到時間。");

            // 簽到時間回到初始值，整筆出席資料回到原始狀態。
            if (!signIn.HasValue)
            {
                const string deleteSql = """
                    DELETE FROM dbo.MeetingAttendance
                    WHERE MeetingNo = @MeetingNo
                      AND ParticipantType = @ParticipantType
                      AND ISNULL(EmployeeNo, '') = ISNULL(@EmployeeNo, '')
                      AND ISNULL(ExternalName, N'') = ISNULL(@ExternalName, N'')
                      AND ISNULL(ExternalOrganization, N'') = ISNULL(@ExternalOrganization, N'');
                    """;
                await using var delete = new SqlCommand(deleteSql, connection, tx);
                AddAttendanceIdentityParameters(delete, meetingNo, isExternal ? 2 : 1, employeeNo, externalName, externalOrganization);
                await delete.ExecuteNonQueryAsync();
                continue;
            }

            DateTime? originalIn = null;
            DateTime? originalOut = null;
            string originalProxy = "";
            var exists = false;
            const string currentSql = """
                SELECT SignInTime, SignOutTime, ProxySignEmployeeNo
                FROM dbo.MeetingAttendance WITH (UPDLOCK, HOLDLOCK)
                WHERE MeetingNo = @MeetingNo
                  AND ParticipantType = @ParticipantType
                  AND ISNULL(EmployeeNo, '') = ISNULL(@EmployeeNo, '')
                  AND ISNULL(ExternalName, N'') = ISNULL(@ExternalName, N'')
                  AND ISNULL(ExternalOrganization, N'') = ISNULL(@ExternalOrganization, N'');
                """;
            await using (var current = new SqlCommand(currentSql, connection, tx))
            {
                AddAttendanceIdentityParameters(current, meetingNo, isExternal ? 2 : 1, employeeNo, externalName, externalOrganization);
                await using var reader = await current.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    exists = true;
                    originalIn = reader["SignInTime"] is DBNull ? null : Convert.ToDateTime(reader["SignInTime"]);
                    originalOut = reader["SignOutTime"] is DBNull ? null : Convert.ToDateTime(reader["SignOutTime"]);
                    originalProxy = reader["ProxySignEmployeeNo"] is DBNull ? "" : reader["ProxySignEmployeeNo"].ToString()?.Trim() ?? "";
                }
            }

            static bool SameMinute(DateTime? a, DateTime? b) =>
                (!a.HasValue && !b.HasValue) ||
                (a.HasValue && b.HasValue && a.Value.Year == b.Value.Year && a.Value.Month == b.Value.Month && a.Value.Day == b.Value.Day && a.Value.Hour == b.Value.Hour && a.Value.Minute == b.Value.Minute);

            var timeChanged = !exists || !SameMinute(originalIn, signIn) || !SameMinute(originalOut, signOut);
            var effectiveProxy = timeChanged
                ? userId.Trim()
                : (!string.IsNullOrWhiteSpace(row.Operator) ? row.Operator.Trim() : originalProxy);

            if (exists)
            {
                const string updateSql = """
                    UPDATE dbo.MeetingAttendance
                    SET SignInTime = @SignInTime,
                        SignOutTime = @SignOutTime,
                        ProxySignEmployeeNo = @Proxy,
                        Remark = @Remark,
                        UpdatedByEmployeeNo = @User,
                        UpdatedTime = SYSDATETIME()
                    WHERE MeetingNo = @MeetingNo
                      AND ParticipantType = @ParticipantType
                      AND ISNULL(EmployeeNo, '') = ISNULL(@EmployeeNo, '')
                      AND ISNULL(ExternalName, N'') = ISNULL(@ExternalName, N'')
                      AND ISNULL(ExternalOrganization, N'') = ISNULL(@ExternalOrganization, N'');
                    """;
                await using var update = new SqlCommand(updateSql, connection, tx);
                AddAttendanceIdentityParameters(update, meetingNo, isExternal ? 2 : 1, employeeNo, externalName, externalOrganization);
                update.Parameters.Add("@SignInTime", SqlDbType.DateTime2).Value = signIn.Value;
                update.Parameters.Add("@SignOutTime", SqlDbType.DateTime2).Value = (object?)signOut ?? DBNull.Value;
                update.Parameters.Add("@Proxy", SqlDbType.VarChar, 20).Value = DbValue(effectiveProxy);
                update.Parameters.Add("@Remark", SqlDbType.NVarChar, 500).Value = DbValue(row.Note);
                update.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = userId.Trim();
                await update.ExecuteNonQueryAsync();
            }
            else
            {
                const string insertSql = """
                    INSERT dbo.MeetingAttendance
                    (
                        MeetingNo, ParticipantType, EmployeeNo, ExternalName, ExternalOrganization,
                        SignInTime, SignOutTime, ProxySignEmployeeNo, Remark,
                        UpdatedByEmployeeNo, UpdatedTime
                    )
                    VALUES
                    (
                        @MeetingNo, @ParticipantType, @EmployeeNo, @ExternalName, @ExternalOrganization,
                        @SignInTime, @SignOutTime, @Proxy, @Remark,
                        @User, SYSDATETIME()
                    );
                    """;
                await using var insert = new SqlCommand(insertSql, connection, tx);
                AddAttendanceIdentityParameters(insert, meetingNo, isExternal ? 2 : 1, employeeNo, externalName, externalOrganization);
                insert.Parameters.Add("@SignInTime", SqlDbType.DateTime2).Value = signIn.Value;
                insert.Parameters.Add("@SignOutTime", SqlDbType.DateTime2).Value = (object?)signOut ?? DBNull.Value;
                insert.Parameters.Add("@Proxy", SqlDbType.VarChar, 20).Value = DbValue(effectiveProxy);
                insert.Parameters.Add("@Remark", SqlDbType.NVarChar, 500).Value = DbValue(row.Note);
                insert.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = userId.Trim();
                await insert.ExecuteNonQueryAsync();
            }
        }

        await tx.CommitAsync();
        return Results.Ok(new { success = true });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Json(new { success = false, message = CleanDatabaseMessage(ex) }, statusCode: 400);
    }
});

app.MapGet("/api/meetings/{meetingNo}/my-attendance", async (
    string meetingNo,
    HttpRequest httpRequest,
    IDataProtectionProvider dataProtectionProvider,
    IConfiguration configuration) =>
{
    if (!TryReadMobileMeetingSession(httpRequest, dataProtectionProvider, out var mobileSession))
        return MobileSessionUnauthorized();

    if (!string.Equals(mobileSession!.MeetingNo, meetingNo.Trim(), StringComparison.OrdinalIgnoreCase))
        return MobileSessionUnauthorized();

    var employeeNo = mobileSession.EmployeeNo;

    await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
    await connection.OpenAsync();
    const string sql = """
        SELECT
            a.SignInTime,
            a.SignOutTime,
            a.ProxySignEmployeeNo,
            ISNULL(NULLIF(LTRIM(RTRIM(proxyUser.CNM)), N''), a.ProxySignEmployeeNo) AS ProxyName,
            ISNULL(LTRIM(RTRIM(proxyUser.DPCNM)), N'') AS ProxyDepartment
        FROM dbo.MeetingAttendance a
        OUTER APPLY
        (
            SELECT TOP (1) sourceProxy.*
            FROM LineDB.dbo.UserInfo sourceProxy
            WHERE LTRIM(RTRIM(sourceProxy.SNTOPID)) = a.ProxySignEmployeeNo
               OR LTRIM(RTRIM(sourceProxy.UserID)) = a.ProxySignEmployeeNo
            ORDER BY CASE WHEN LTRIM(RTRIM(sourceProxy.SNTOPID)) = a.ProxySignEmployeeNo THEN 1 ELSE 2 END
        ) proxyUser
        WHERE a.MeetingNo = @MeetingNo
          AND a.ParticipantType = @ParticipantType
          AND
          (
              (@ParticipantType = 1 AND a.EmployeeNo = @EmployeeNo)
              OR
              (@ParticipantType = 2
               AND a.EmployeeNo IS NULL
               AND ISNULL(a.ExternalName, N'') = ISNULL(@ExternalName, N'')
               AND ISNULL(a.ExternalOrganization, N'') = ISNULL(@ExternalOrganization, N''))
          );
        """;
    await using var command = new SqlCommand(sql, connection);
    command.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
    command.Parameters.Add("@ParticipantType", SqlDbType.TinyInt).Value = mobileSession.ParticipantType;
    command.Parameters.Add("@EmployeeNo", SqlDbType.VarChar, 20).Value = mobileSession.ParticipantType == 1
        ? employeeNo.Trim()
        : DBNull.Value;
    command.Parameters.Add("@ExternalName", SqlDbType.NVarChar, 100).Value = mobileSession.ParticipantType == 2
        ? (object)(mobileSession.ExternalName ?? "")
        : DBNull.Value;
    command.Parameters.Add("@ExternalOrganization", SqlDbType.NVarChar, 200).Value = mobileSession.ParticipantType == 2
        ? (object)(mobileSession.ExternalOrganization ?? "")
        : DBNull.Value;
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) return Results.Ok(new { signInTime = "", signOutTime = "", proxyEmployeeNo = "", proxyName = "", proxyDepartment = "" });
    return Results.Ok(new
    {
        signInTime = reader["SignInTime"] is DBNull ? "" : Convert.ToDateTime(reader["SignInTime"]).ToString("yyyy-MM-dd HH:mm"),
        signOutTime = reader["SignOutTime"] is DBNull ? "" : Convert.ToDateTime(reader["SignOutTime"]).ToString("yyyy-MM-dd HH:mm"),
        proxyEmployeeNo = reader["ProxySignEmployeeNo"] is DBNull ? "" : reader["ProxySignEmployeeNo"].ToString()?.Trim() ?? "",
        proxyName = reader["ProxyName"] is DBNull ? "" : reader["ProxyName"].ToString()?.Trim() ?? "",
        proxyDepartment = reader["ProxyDepartment"] is DBNull ? "" : reader["ProxyDepartment"].ToString()?.Trim() ?? ""
    });
});

app.MapPost("/api/meetings/{meetingNo}/attendance/{action}", async (
    string meetingNo,
    string action,
    OwnAttendanceRequest request,
    HttpRequest httpRequest,
    IDataProtectionProvider dataProtectionProvider,
    IConfiguration configuration) =>
{
    if (action is not ("signin" or "signout"))
        return Results.BadRequest(new { success = false, message = "簽到動作不正確。" });

    if (!TryReadMobileMeetingSession(httpRequest, dataProtectionProvider, out var mobileSession))
        return MobileSessionUnauthorized();

    if (!string.Equals(mobileSession!.MeetingNo, meetingNo.Trim(), StringComparison.OrdinalIgnoreCase))
        return MobileSessionUnauthorized();

    var employeeNo = mobileSession.EmployeeNo;
    if (mobileSession.ParticipantType == 2
        && (string.IsNullOrWhiteSpace(mobileSession.ExternalName)
            || string.IsNullOrWhiteSpace(mobileSession.ExternalOrganization)
            || string.IsNullOrWhiteSpace(mobileSession.ExternalTitle)))
    {
        return Results.BadRequest(new { success = false, message = "請先完成單位、姓名及職稱資料後再簽到。" });
    }

    return await SaveSingleAttendance(
        meetingNo,
        action,
        mobileSession.ParticipantType,
        mobileSession.ParticipantType == 2 ? null : employeeNo,
        mobileSession.ParticipantType == 2 ? mobileSession.ExternalName : null,
        mobileSession.ParticipantType == 2 ? mobileSession.ExternalOrganization : null,
        null,
        employeeNo,
        configuration);
});

app.MapPost("/api/meetings/{meetingNo}/attendance/proxy", async (
    string meetingNo,
    ProxyAttendanceRequest request,
    HttpRequest httpRequest,
    IDataProtectionProvider dataProtectionProvider,
    IConfiguration configuration) =>
{
    if (request.Action is not ("signin" or "signout"))
        return Results.BadRequest(new { success = false, message = "代簽動作不正確。" });

    if (!TryReadMobileMeetingSession(httpRequest, dataProtectionProvider, out var mobileSession))
        return MobileSessionUnauthorized();

    if (!string.Equals(mobileSession!.MeetingNo, meetingNo.Trim(), StringComparison.OrdinalIgnoreCase))
        return MobileSessionUnauthorized();

    var operatorEmployeeNo = mobileSession.EmployeeNo;
    if (mobileSession.ParticipantType != 1)
        return Results.BadRequest(new { success = false, message = "非院內綁定使用者不可使用代簽功能。" });
    if (request.ParticipantType.Equals("external", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { success = false, message = "院外人員主持人代簽功能已移除。" });

    await using (var permissionConnection = new SqlConnection(GetConnection(configuration, "LineMeetingDb")))
    {
        await permissionConnection.OpenAsync();
        await using var permissionCommand = new SqlCommand(
            "SELECT ISNULL(AllowUnlistedParticipants, 0) FROM dbo.Meeting WHERE MeetingNo = @MeetingNo AND ISNULL(IsDeleted,0)=0;",
            permissionConnection);
        permissionCommand.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
        var allowOpen = Convert.ToBoolean(await permissionCommand.ExecuteScalarAsync() ?? false);
        if (allowOpen)
            return Results.BadRequest(new { success = false, message = "此會議允許非名單人員加入，因此不可使用代簽功能。" });
    }

    var isExternal = request.ParticipantType.Equals("external", StringComparison.OrdinalIgnoreCase);

    return await SaveSingleAttendance(
        meetingNo,
        request.Action,
        isExternal ? 2 : 1,
        isExternal ? null : request.ParticipantEmployeeNo,
        isExternal ? request.ExternalName : null,
        isExternal ? request.ExternalOrganization : null,
        operatorEmployeeNo,
        operatorEmployeeNo,
        configuration);
});

async Task<IResult> SaveSingleAttendance(
    string meetingNo,
    string action,
    int participantType,
    string? employeeNo,
    string? externalName,
    string? externalOrganization,
    string? proxyEmployeeNo,
    string updatedBy,
    IConfiguration configuration)
{
    await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
    await connection.OpenAsync();
    await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

    try
    {
        const string meetingSql = """
            SELECT StartDateTime, EndDateTime,
                   Host1EmployeeNo, Host2EmployeeNo, Host3EmployeeNo, CreatedByEmployeeNo,
                   ISNULL(AllowUnlistedParticipants, 0) AS AllowUnlistedParticipants
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
        bool allowUnlistedParticipants;

        await using (var meetingCommand = new SqlCommand(meetingSql, connection, tx))
        {
            meetingCommand.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
            await using var reader = await meetingCommand.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new InvalidOperationException("找不到會議。");
            startTime = Convert.ToDateTime(reader["StartDateTime"]);
            endTime = Convert.ToDateTime(reader["EndDateTime"]);
            host1EmployeeNo = reader["Host1EmployeeNo"] is DBNull ? "" : reader["Host1EmployeeNo"].ToString()?.Trim() ?? "";
            host2EmployeeNo = reader["Host2EmployeeNo"] is DBNull ? "" : reader["Host2EmployeeNo"].ToString()?.Trim() ?? "";
            host3EmployeeNo = reader["Host3EmployeeNo"] is DBNull ? "" : reader["Host3EmployeeNo"].ToString()?.Trim() ?? "";
            createdByEmployeeNo = reader["CreatedByEmployeeNo"] is DBNull ? "" : reader["CreatedByEmployeeNo"].ToString()?.Trim() ?? "";
            allowUnlistedParticipants = Convert.ToBoolean(reader["AllowUnlistedParticipants"]);
        }

        var now = DateTime.Now;
        var checkInOpenAt = startTime.AddMinutes(-CheckInRules.OpenMinutesBeforeStart);
        if (action == "signin" && now < checkInOpenAt)
            throw new InvalidOperationException($"本會議將於 {checkInOpenAt:yyyy/MM/dd HH:mm} 開放簽到。");
        if (action == "signin" && now > endTime)
            throw new InvalidOperationException("本會議已結束，無法再簽到。");
        if (action == "signout" && now > endTime.AddMinutes(30))
            throw new InvalidOperationException($"本會議簽退已於 {endTime.AddMinutes(30):yyyy/MM/dd HH:mm} 截止。");

        var isProxyOperation = !string.IsNullOrWhiteSpace(proxyEmployeeNo);
        if (isProxyOperation && allowUnlistedParticipants)
            throw new InvalidOperationException("此會議允許非名單人員加入，因此不可使用代簽功能。");
        if (isProxyOperation)
        {
            var hosts = new[] { host1EmployeeNo, host2EmployeeNo, host3EmployeeNo, createdByEmployeeNo };
            var isHostOperator = hosts.Any(x => string.Equals(x, updatedBy, StringComparison.OrdinalIgnoreCase));
            if (participantType == 2)
            {
                if (!isHostOperator)
                    throw new InvalidOperationException("只有會議發起人或主持人可以替院外人員簽到／簽退。");
            }
            else
            {
                const string operatorSql = """
                    SELECT COUNT(*)
                    FROM dbo.MeetingParticipant
                    WHERE MeetingNo = @MeetingNo AND ParticipantType = 1 AND EmployeeNo = @EmployeeNo;
                    """;
                await using var operatorCommand = new SqlCommand(operatorSql, connection, tx);
                operatorCommand.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
                operatorCommand.Parameters.Add("@EmployeeNo", SqlDbType.VarChar, 20).Value = updatedBy.Trim();
                var isInternalParticipant = Convert.ToInt32(await operatorCommand.ExecuteScalarAsync()) > 0;
                if (!isHostOperator && !isInternalParticipant)
                    throw new InvalidOperationException("只有本次會議的院內參與者可以代簽院內人員。");
            }
        }

        const string participantSql = """
            SELECT COUNT(*)
            FROM dbo.MeetingParticipant
            WHERE MeetingNo = @MeetingNo
              AND ParticipantType = @ParticipantType
              AND ISNULL(EmployeeNo, '') = ISNULL(@EmployeeNo, '')
              AND ISNULL(ExternalName, N'') = ISNULL(@ExternalName, N'')
              AND ISNULL(ExternalOrganization, N'') = ISNULL(@ExternalOrganization, N'');
            """;
        await using (var participantCommand = new SqlCommand(participantSql, connection, tx))
        {
            AddAttendanceIdentityParameters(participantCommand, meetingNo, participantType, employeeNo, externalName, externalOrganization);
            var participantExists = Convert.ToInt32(await participantCommand.ExecuteScalarAsync()) > 0;
            if (!participantExists)
            {
                if (allowUnlistedParticipants && participantType == 1 && !isProxyOperation && action == "signin" && !string.IsNullOrWhiteSpace(employeeNo))
                {
                    const string addOpenParticipantSql = """
                        INSERT dbo.MeetingParticipant
                        (MeetingNo, ParticipantType, EmployeeNo, ExternalName, ExternalOrganization,
                         CreatedByEmployeeNo, CreatedTime, UpdatedByEmployeeNo, UpdatedTime)
                        VALUES (@MeetingNo, 1, @EmployeeNo, NULL, NULL, @EmployeeNo, SYSDATETIME(), NULL, NULL);
                        """;
                    await using var addOpenParticipant = new SqlCommand(addOpenParticipantSql, connection, tx);
                    addOpenParticipant.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
                    addOpenParticipant.Parameters.Add("@EmployeeNo", SqlDbType.VarChar, 20).Value = employeeNo.Trim();
                    await addOpenParticipant.ExecuteNonQueryAsync();
                }
                else
                {
                    throw new InvalidOperationException("此人員不在本次會議參與名單中，無法簽到。");
                }
            }
        }

        DateTime? existingSignIn = null;
        DateTime? existingSignOut = null;
        string existingProxy = "";
        var attendanceExists = false;
        const string lockSql = """
            SELECT SignInTime, SignOutTime, ProxySignEmployeeNo
            FROM dbo.MeetingAttendance WITH (UPDLOCK, HOLDLOCK)
            WHERE MeetingNo = @MeetingNo
              AND ParticipantType = @ParticipantType
              AND ISNULL(EmployeeNo, '') = ISNULL(@EmployeeNo, '')
              AND ISNULL(ExternalName, N'') = ISNULL(@ExternalName, N'')
              AND ISNULL(ExternalOrganization, N'') = ISNULL(@ExternalOrganization, N'');
            """;
        await using (var lockCommand = new SqlCommand(lockSql, connection, tx))
        {
            AddAttendanceIdentityParameters(lockCommand, meetingNo, participantType, employeeNo, externalName, externalOrganization);
            await using var reader = await lockCommand.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                attendanceExists = true;
                existingSignIn = reader["SignInTime"] is DBNull ? null : Convert.ToDateTime(reader["SignInTime"]);
                existingSignOut = reader["SignOutTime"] is DBNull ? null : Convert.ToDateTime(reader["SignOutTime"]);
                existingProxy = reader["ProxySignEmployeeNo"] is DBNull ? "" : reader["ProxySignEmployeeNo"].ToString()?.Trim() ?? "";
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

            if (attendanceExists)
            {
                const string updateSignInSql = """
                    UPDATE dbo.MeetingAttendance
                    SET SignInTime = SYSDATETIME(), SignOutTime = NULL,
                        ProxySignEmployeeNo = @Proxy,
                        UpdatedByEmployeeNo = @UpdatedBy, UpdatedTime = SYSDATETIME()
                    WHERE MeetingNo = @MeetingNo
                      AND ParticipantType = @ParticipantType
                      AND ISNULL(EmployeeNo, '') = ISNULL(@EmployeeNo, '')
                      AND ISNULL(ExternalName, N'') = ISNULL(@ExternalName, N'')
                      AND ISNULL(ExternalOrganization, N'') = ISNULL(@ExternalOrganization, N'')
                      AND SignInTime IS NULL;
                    """;
                await using var update = new SqlCommand(updateSignInSql, connection, tx);
                AddAttendanceIdentityParameters(update, meetingNo, participantType, employeeNo, externalName, externalOrganization);
                update.Parameters.Add("@Proxy", SqlDbType.VarChar, 20).Value = (object?)proxyEmployeeNo ?? DBNull.Value;
                update.Parameters.Add("@UpdatedBy", SqlDbType.VarChar, 20).Value = updatedBy;
                if (await update.ExecuteNonQueryAsync() != 1)
                    throw new InvalidOperationException("此人員剛剛已由其他人完成簽到，請重新整理名單。");
            }
            else
            {
                const string insertSql = """
                    INSERT dbo.MeetingAttendance
                    (MeetingNo, ParticipantType, EmployeeNo, ExternalName, ExternalOrganization,
                     SignInTime, SignOutTime, ProxySignEmployeeNo, Remark, UpdatedByEmployeeNo, UpdatedTime)
                    VALUES
                    (@MeetingNo, @ParticipantType, @EmployeeNo, @ExternalName, @ExternalOrganization,
                     SYSDATETIME(), NULL, @Proxy, NULL, @UpdatedBy, SYSDATETIME());
                    """;
                await using var insert = new SqlCommand(insertSql, connection, tx);
                AddAttendanceIdentityParameters(insert, meetingNo, participantType, employeeNo, externalName, externalOrganization);
                insert.Parameters.Add("@Proxy", SqlDbType.VarChar, 20).Value = (object?)proxyEmployeeNo ?? DBNull.Value;
                insert.Parameters.Add("@UpdatedBy", SqlDbType.VarChar, 20).Value = updatedBy;
                await insert.ExecuteNonQueryAsync();
            }
        }
        else
        {
            if (!existingSignIn.HasValue)
                throw new InvalidOperationException("尚未簽到，不能簽退。");
            if (existingSignOut.HasValue)
                throw new InvalidOperationException("此人員已完成簽退，請重新整理名單。");
            if (isProxyOperation && !string.Equals(existingProxy, updatedBy, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("此人員不是由您代簽，只有原代簽人可以代為簽退。");

            const string updateSignOutSql = """
                UPDATE dbo.MeetingAttendance
                SET SignOutTime = SYSDATETIME(),
                    ProxySignEmployeeNo = @Proxy,
                    UpdatedByEmployeeNo = @UpdatedBy, UpdatedTime = SYSDATETIME()
                WHERE MeetingNo = @MeetingNo
                  AND ParticipantType = @ParticipantType
                  AND ISNULL(EmployeeNo, '') = ISNULL(@EmployeeNo, '')
                  AND ISNULL(ExternalName, N'') = ISNULL(@ExternalName, N'')
                  AND ISNULL(ExternalOrganization, N'') = ISNULL(@ExternalOrganization, N'')
                  AND SignInTime IS NOT NULL AND SignOutTime IS NULL;
                """;
            await using var update = new SqlCommand(updateSignOutSql, connection, tx);
            AddAttendanceIdentityParameters(update, meetingNo, participantType, employeeNo, externalName, externalOrganization);
            var proxyToKeep = string.IsNullOrWhiteSpace(existingProxy) ? proxyEmployeeNo : existingProxy;
            update.Parameters.Add("@Proxy", SqlDbType.VarChar, 20).Value = (object?)proxyToKeep ?? DBNull.Value;
            update.Parameters.Add("@UpdatedBy", SqlDbType.VarChar, 20).Value = updatedBy;
            if (await update.ExecuteNonQueryAsync() != 1)
                throw new InvalidOperationException("簽退狀態已被其他人更新，請重新整理名單。");
        }

        await tx.CommitAsync();
        return Results.Ok(new { success = true });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Json(new { success = false, message = CleanDatabaseMessage(ex) }, statusCode: 400);
    }
}

void AddAttendanceIdentityParameters(
    SqlCommand command,
    string meetingNo,
    int participantType,
    string? employeeNo,
    string? externalName,
    string? externalOrganization)
{
    command.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
    command.Parameters.Add("@ParticipantType", SqlDbType.TinyInt).Value = participantType;
    command.Parameters.Add("@EmployeeNo", SqlDbType.VarChar, 20).Value = (object?)employeeNo ?? DBNull.Value;
    command.Parameters.Add("@ExternalName", SqlDbType.NVarChar, 100).Value = (object?)externalName ?? DBNull.Value;
    command.Parameters.Add("@ExternalOrganization", SqlDbType.NVarChar, 200).Value = (object?)externalOrganization ?? DBNull.Value;
}

static DateTime? ParseAttendanceTime(DateTime date, string? time)
{
    if (string.IsNullOrWhiteSpace(time)) return null;
    if (!TimeSpan.TryParse(time, out var value))
        throw new InvalidOperationException("簽到或簽退時間格式不正確。");
    return date.Add(value);
}

static string CreateGuestKey(string lineUserId)
{
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(lineUserId.Trim()));
    return "G" + Convert.ToHexString(hash).ToLowerInvariant()[..19];
}

static string CreateMobileMeetingSession(
    IDataProtectionProvider dataProtectionProvider,
    int participantType,
    string employeeNo,
    string meetingNo,
    string meetingToken,
    DateTime meetingEndDateTime,
    string? externalName = null,
    string? externalOrganization = null,
    string? externalTitle = null)
{
    var protector = dataProtectionProvider.CreateProtector("MeetingLogin.MobileMeetingSession.v2");

    var expiresLocal = DateTime.SpecifyKind(
        meetingEndDateTime.AddMinutes(30),
        DateTimeKind.Local);

    var payload = new MobileMeetingSessionPayload(
        participantType,
        employeeNo.Trim(),
        externalName?.Trim() ?? "",
        externalOrganization?.Trim() ?? "",
        externalTitle?.Trim() ?? "",
        meetingNo.Trim(),
        meetingToken.Trim(),
        new DateTimeOffset(expiresLocal).ToUnixTimeSeconds());

    return protector.Protect(JsonSerializer.Serialize(payload));
}


static bool TryReadMobileMeetingSession(
    HttpRequest request,
    IDataProtectionProvider dataProtectionProvider,
    out MobileMeetingSessionPayload? payload)
{
    payload = null;
    var protectedValue = request.Headers["X-Meeting-Session"].FirstOrDefault()?.Trim();

    if (string.IsNullOrWhiteSpace(protectedValue))
        return false;

    try
    {
        var protector = dataProtectionProvider.CreateProtector("MeetingLogin.MobileMeetingSession.v2");
        var json = protector.Unprotect(protectedValue);
        payload = JsonSerializer.Deserialize<MobileMeetingSessionPayload>(json);

        if (payload is null
            || payload.ParticipantType is not (1 or 2)
            || string.IsNullOrWhiteSpace(payload.EmployeeNo)
            || string.IsNullOrWhiteSpace(payload.MeetingNo)
            || string.IsNullOrWhiteSpace(payload.MeetingToken)
            || payload.ExpiresUnixTime <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            payload = null;
            return false;
        }

        return true;
    }
    catch
    {
        payload = null;
        return false;
    }
}

static IResult MobileSessionUnauthorized() =>
    Results.Json(
        new
        {
            success = false,
            code = "LIFF_SESSION_REQUIRED",
            message = "LINE 會議驗證已失效，請重新掃描會議 QR Code。"
        },
        statusCode: StatusCodes.Status401Unauthorized);

object MapMeeting(SqlDataReader reader, bool detail = false)
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
        lastSavedAt = reader["UpdatedTime"] is DBNull ? reader["CreatedTime"] : reader["UpdatedTime"]
    };
}

app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new
        {
            success = false,
            code = "API_NOT_FOUND",
            message = $"找不到 API：{context.Request.Path}"
        });
        return;
    }

    await context.Response.SendFileAsync(
        Path.Combine(app.Environment.WebRootPath, "index.html"));
});
app.Run();

public sealed record LoginRequest(string? Account, string? Password);

public sealed record LiffMeetingAuthorizeRequest(string? Type, string? MeetingToken, string? UserId);

public sealed record DevMobileSessionRequest(string? EmployeeNo, string? MeetingToken);
public sealed record DevMobileGuestSessionRequest(string? MeetingToken);

public sealed record GuestProfileRequest(string? Organization, string? Name, string? Title);

public sealed record MobileMeetingSessionPayload(
    int ParticipantType,
    string EmployeeNo,
    string ExternalName,
    string ExternalOrganization,
    string ExternalTitle,
    string MeetingNo,
    string MeetingToken,
    long ExpiresUnixTime);

public sealed record AttendanceSaveRequest(List<AttendanceRowRequest>? Rows);

public sealed record AttendanceRowRequest(
    string UserID,
    string? In,
    string? Out,
    string? Source,
    string? Operator,
    string? Note);

public sealed record OwnAttendanceRequest(string? EmployeeNo);

public sealed record ProxyAttendanceRequest(
    string Action,
    string ParticipantType,
    string? ParticipantEmployeeNo,
    string? ExternalName,
    string? ExternalOrganization,
    string? ProxyEmployeeNo,
    string? OperatedByEmployeeNo);

public sealed record MeetingSaveRequest(
    string Name,
    string? SubName,
    string Date,
    string Start,
    string End,
    bool HasLocation,
    string? LocationID,
    string? CustomLocation,
    string Host1UserID,
    string? Host2UserID,
    string? Host3UserID,
    string? Note,
    string CurrentUserID,
    List<string>? ParticipantUserIDs,
    bool AllowUnlistedParticipants);

public static class CheckInRules
{
    public static int OpenMinutesBeforeStart { get; set; }
}
