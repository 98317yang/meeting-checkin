namespace MeetingCheckin;

internal static class RequestContext
{
    // 同一台院內電腦的 DNS 判定結果短暫快取，避免每個頁面 / API 請求都重做反向 DNS。
    internal static readonly ConcurrentDictionary<string, (bool IsHospitalComputer, string? ComputerName, DateTimeOffset ExpiresAt)> hospitalDeviceCache = new();

    internal static string BuildMeetingLiffUrl(IConfiguration configuration, HttpRequest request, string token)
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

    internal const string EmployeeNoClaim = "EmployeeNo";

    // 目前登入者的員工編號（來自登入 cookie）。
    internal static string CurrentEmployeeNo(ClaimsPrincipal principal) =>
        principal.FindFirstValue(EmployeeNoClaim)?.Trim() ?? "";

    // 本機直接連線：來源是本機，且沒有經過反向代理（ngrok 會加上 X-Forwarded-For，並保留原本的 Host）。
    internal static bool IsDirectLocalRequest(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (!IPAddress.IsLoopback(ip)) return false;

        var headers = context.Request.Headers;
        if (headers.ContainsKey("X-Forwarded-For") || headers.ContainsKey("X-Forwarded-Host") || headers.ContainsKey("Forwarded"))
            return false;

        var host = context.Request.Host.Host;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host is "127.0.0.1" or "::1" or "[::1]";
    }

    internal static async Task<(bool IsHospitalComputer, string IpAddress, string? ComputerName, string DetectionSource)> DetectHospitalComputerAsync(
        HttpContext context,
        IWebHostEnvironment environment)
    {
        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is null)
            return (false, "", null, "unknown");

        if (remoteIp.IsIPv4MappedToIPv6)
            remoteIp = remoteIp.MapToIPv4();

        // 經由 ngrok 轉發的外部請求連線來源也是本機，因此要確認是本機直接連線，才視為院內電腦。
        if (environment.IsDevelopment() && IsDirectLocalRequest(context))
            return (true, remoteIp.ToString(), Environment.MachineName?.Trim(), "developmentLocalhost");

        var ipAddress = remoteIp.ToString();

        // 內網限制改由設定檔控制（Intranet:Enabled / AllowedIpPrefixes / ComputerNamePattern）。
        // 未啟用時不限制來源，方便本機與展示環境使用。
        var intranet = context.RequestServices.GetRequiredService<IConfiguration>().GetSection("Intranet");
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

    private static readonly HashSet<string> ExternalStaticFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "/liff-entry.html",
        "/mobile-checkin.html",
        "/css/app.css",
        "/js/app.js",
        "/js/liff-entry.js",
        "/js/mobile-checkin.js",
    };

    internal static bool IsExternalAllowedPath(PathString path)
    {
        var value = path.Value ?? "";

        // 外網只提供 LINE / LIFF 手機簽到流程與必要靜態資源。
        // 只開放手機簽到用到的頁面與檔案；電腦端頁面的 JS 不在清單內。
        if (ExternalStaticFiles.Contains(value) || value.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
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

}
