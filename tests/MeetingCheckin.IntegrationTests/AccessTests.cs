namespace MeetingCheckin.IntegrationTests;

// 登入與存取控制：沒登入能看到什麼、看不到什麼。
[Collection("api")]
public class AccessTests(ApiFactory factory)
{
    [IntegrationFact]
    public async Task RootPath_ShowsLoginPage_WithoutLogin()
    {
        var response = await factory.NewClient().GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("type=\"password\"", await response.Content.ReadAsStringAsync());
    }

    [IntegrationFact]
    public async Task MeetingApis_RequireLogin()
    {
        var client = factory.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/meetings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/meetings/2026110001/attendance")).StatusCode);
    }

    [IntegrationFact]
    public async Task Login_SetsSessionCookie_AndLogoutEndsIt()
    {
        var client = factory.NewClient();
        await Api.LoginOrFailAsync(client, Demo.Host);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("10001", me.GetProperty("employeeNo").GetString());

        await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [IntegrationFact]
    public async Task WrongPassword_IsRejected_WithoutInternalDetails()
    {
        var response = await Api.LoginAsync(factory.NewClient(), Demo.Nurse, "wrong-password");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("detail", out _));
    }

    [IntegrationFact]
    public async Task HealthCheck_ReportsStatus_WithoutDatabaseDetails()
    {
        var response = await factory.NewClient().GetAsync("/api/health/database");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "success", "message" }, body.EnumerateObject().Select(p => p.Name).ToArray());
    }
}
