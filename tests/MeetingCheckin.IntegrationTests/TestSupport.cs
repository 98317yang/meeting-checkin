global using System.Net;
global using System.Net.Http.Json;
global using System.Text.Json;
global using Xunit;
global using MeetingCheckin;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingCheckin.IntegrationTests;

// 整合測試會啟動整個系統並連到真的 SQL Server，也會寫入測試用的會議與簽到紀錄。
// 為了不弄亂示範資料，預設跳過；設定 RUN_INTEGRATION_TESTS=1 才執行（CI 每次都用全新的資料庫）。
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") != "1")
            Skip = "設定 RUN_INTEGRATION_TESTS=1 並準備好資料庫後才執行。";
    }
}

// 用 WebApplicationFactory 在記憶體中啟動整個系統：路由、登入驗證、中介層、資料庫都是真的。
// 連線字串與正式執行相同，來自環境變數（CI）或 user-secrets（本機）。
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,   // 像瀏覽器一樣保存登入 Cookie
    });

    // 正式流程中，手機憑證是 LINE 驗證身分後由系統簽發；測試環境無法登入 LINE，
    // 因此直接用系統自己的簽發函式產生，之後的簽到、檢查、寫入流程都和正式環境相同。
    public string IssueMobileSession(string employeeNo, string meetingNo, string meetingToken, DateTime meetingEnd) =>
        MobileSession.CreateMobileMeetingSession(
            Services.GetRequiredService<IDataProtectionProvider>(), 1, employeeNo, meetingNo, meetingToken, meetingEnd);

    public string LineMeetingConnectionString =>
        Services.GetRequiredService<IConfiguration>().GetConnectionString("LineMeetingDb")!;
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>;

// 示範資料中的帳號（db/02-seed.sql）。
public static class Demo
{
    public const string Password = "Demo@1234";
    public const string Host = "DEMO01";          // 王小明 10001
    public const string Nurse = "DEMO02";         // 林雅婷 20001
    public const string Doctor = "DEMO03";        // 陳志豪 30001
    public const string DoctorEmployeeNo = "30001";
}

public static class Api
{
    public static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string account, string password = Demo.Password) =>
        await client.PostAsJsonAsync("/api/auth/login", new { account, password });

    public static async Task LoginOrFailAsync(HttpClient client, string account)
    {
        var response = await LoginAsync(client, account);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // 建立一場「現在正在進行」的會議（台北時間，不跨日），回傳會議編號、QR token 與結束時間。
    public static async Task<(string MeetingNo, string Token, DateTime End)> CreateMeetingAsync(
        HttpClient client, string name, string[] participants, bool allowUnlisted = false)
    {
        var now = AppClock.Now;
        var start = now.AddMinutes(-10) < now.Date ? now.Date : now.AddMinutes(-10);
        var end = now.AddMinutes(50) >= now.Date.AddDays(1) ? now.Date.AddHours(23).AddMinutes(59) : now.AddMinutes(50);
        var response = await client.PostAsJsonAsync("/api/meetings", new
        {
            name,
            subName = "",
            date = now.ToString("yyyy-MM-dd"),
            start = start.ToString("HH:mm"),
            end = end.ToString("HH:mm"),
            hasLocation = false,
            host1UserID = "10001",
            note = "",
            participantUserIDs = participants,
            allowUnlistedParticipants = allowUnlisted,
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body.ToString());
        var meetingNo = body.GetProperty("meetingID").GetString()!;
        var detail = await GetMeetingAsync(client, meetingNo);
        return (meetingNo, detail.GetProperty("qrCode").GetString()!, new DateTime(end.Year, end.Month, end.Day, end.Hour, end.Minute, 0));
    }

    public static async Task<JsonElement> GetMeetingAsync(HttpClient client, string meetingNo)
    {
        var body = await client.GetFromJsonAsync<JsonElement>($"/api/meetings/{meetingNo}");
        return body.GetProperty("meeting");
    }

    public static async Task<HttpResponseMessage> MobileAsync(HttpClient client, HttpMethod method, string url, string session)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Meeting-Session", session);
        if (method == HttpMethod.Post) request.Content = JsonContent.Create(new { });
        return await client.SendAsync(request);
    }

    public static string Unique(string label) => $"{label} {Guid.NewGuid().ToString("N")[..6]}";
}
