var builder = WebApplication.CreateBuilder(args);
// 手機簽到憑證（X-Meeting-Session）用 DataProtection 加密。金鑰固定存在設定的資料夾：
// 容器重建或程式重新部署後金鑰還在，已掃碼的人不必重掃；多台主機時把這個路徑指到共用資料夾即可。
var keysPath = builder.Configuration["DataProtection:KeysPath"];
builder.Services.AddDataProtection()
    .SetApplicationName("MeetingCheckin")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(
        string.IsNullOrWhiteSpace(keysPath) ? "keys" : keysPath, builder.Environment.ContentRootPath)));
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
builder.Services.AddHttpClient();

// 電腦版登入使用 HttpOnly cookie：登入成功後由伺服器簽發，前端 JavaScript 讀不到也改不了。
// 電腦版 API 一律從 cookie 取得目前使用者，不再相信網址或請求內容帶來的員工編號。
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "MeetingCheckin.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        // 與前端「閒置 90 分鐘自動登出」一致；有操作就往後延長。
        options.ExpireTimeSpan = TimeSpan.FromMinutes(90);
        options.SlidingExpiration = true;
        // API 不轉址到登入頁，直接回 401 / 403，由前端決定怎麼處理。
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return context.Response.WriteAsJsonAsync(new { success = false, code = "UNAUTHENTICATED", message = "登入已逾時，請重新登入。" });
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsJsonAsync(new { success = false, code = "FORBIDDEN", message = "您沒有權限執行此操作。" });
        };
    });
// 預設所有 API 都要登入；不需要登入的（登入本身、LINE 與手機簽到流程）才逐一標記 AllowAnonymous。
// 之後新增的 API 若忘了設定，也會是「需要登入」，而不是不小心對外開放。
builder.Services.AddAuthorization(options =>
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
var app = builder.Build();

// 開始前幾分鐘開放簽到（CheckIn:OpenMinutesBeforeStart，0 表示開始時間才開放）。
CheckInRules.OpenMinutesBeforeStart = Math.Clamp(app.Configuration.GetValue("CheckIn:OpenMinutesBeforeStart", 0), 0, 240);
// 會議結束後幾分鐘內仍可掃碼進入與簽退（CheckIn:CloseMinutesAfterEnd，預設 30）。結束後不能再簽到。
CheckInRules.CloseMinutesAfterEnd = Math.Clamp(app.Configuration.GetValue("CheckIn:CloseMinutesAfterEnd", 30), 0, 240);

app.UseResponseCompression();

// 開發測試端點（/api/dev/*）可以直接指定員工編號取得手機簽到身分，只給開發者在本機使用。
// 經過 ngrok 等反向代理轉發的請求，連線來源同樣是本機，因此不能只看 IP，要另外檢查轉發標頭與 Host。
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/dev") && !IsDirectLocalRequest(context))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});

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
            var extension = Path.GetExtension(context.File.Name).ToLowerInvariant();
            // JS / CSS 每次都向伺服器確認是否有新版；沒變時只回 304，幾乎沒有成本。
            // 這樣部署新版後瀏覽器一定會拿到新檔，不必再靠手動更新網址上的版本字串，
            // 也不會出現「HTML 是新的、JS 還是舊的」的情況。
            if (extension is ".js" or ".css")
                context.Context.Response.Headers.CacheControl = "no-cache";
            // 圖片很少更動，快取 7 天。
            else if (extension is ".png" or ".jpg" or ".jpeg" or ".webp")
                context.Context.Response.Headers.CacheControl = "public, max-age=604800";
        }
    }
});

// 放在靜態檔案之後：網頁、JS、CSS 不需要登入就能載入，API 才檢查身分。
app.UseAuthentication();
app.UseAuthorization();

// 各功能的 API 分別定義在 Endpoints 資料夾。
app.MapSystemEndpoints();
app.MapAuthEndpoints();
app.MapDirectoryEndpoints();
app.MapLineEndpoints();
app.MapQrEndpoints();
app.MapMeetingEndpoints();
app.MapAttendanceEndpoints();

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
})
// 根路徑「/」與不存在的頁面都會落到這裡並回傳登入頁，所以不能要求登入，
// 否則還沒登入的人連登入頁都看不到。這裡只回傳登入頁或 404 訊息，不含任何資料。
.AllowAnonymous();
app.Run();
