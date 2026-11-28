namespace MeetingCheckin.IntegrationTests;

// 權限與資料保護：非主持人不能看或改、同時編輯不會互相覆蓋、有簽到紀錄的會議不能刪。
[Collection("api")]
public class PermissionTests(ApiFactory factory)
{
    [IntegrationFact]
    public async Task NonHost_CannotViewOrEditAttendanceSheet()
    {
        var host = factory.NewClient();
        await Api.LoginOrFailAsync(host, Demo.Host);
        var (meetingNo, _, _) = await Api.CreateMeetingAsync(host, Api.Unique("權限測試"), [Demo.DoctorEmployeeNo]);

        var participant = factory.NewClient();
        await Api.LoginOrFailAsync(participant, Demo.Doctor);   // 受邀但不是主持人
        Assert.Equal(HttpStatusCode.Forbidden, (await participant.GetAsync($"/api/meetings/{meetingNo}/attendance")).StatusCode);

        var edit = await participant.PutAsJsonAsync($"/api/meetings/{meetingNo}/attendance", new
        {
            rows = new[] { new { userID = Demo.DoctorEmployeeNo, @in = "09:00", @out = "", @operator = "", note = "",
                                 original = new { @in = "", @out = "", @operator = "", note = "" } } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, edit.StatusCode);

        var sheet = await host.GetFromJsonAsync<JsonElement>($"/api/meetings/{meetingNo}/attendance");
        var row = sheet.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("userID").GetString() == Demo.DoctorEmployeeNo);
        Assert.Equal("", row.GetProperty("in").GetString());   // 沒有被改到

        Assert.Equal(HttpStatusCode.Forbidden, (await participant.DeleteAsync($"/api/meetings/{meetingNo}")).StatusCode);
    }

    [IntegrationFact]
    public async Task SavingWithStaleVersion_DoesNotOverwriteNewerEdit()
    {
        var host = factory.NewClient();
        await Api.LoginOrFailAsync(host, Demo.Host);
        var name = Api.Unique("同時編輯");
        var (meetingNo, _, _) = await Api.CreateMeetingAsync(host, name, [Demo.DoctorEmployeeNo]);
        var opened = await Api.GetMeetingAsync(host, meetingNo);   // 兩個人在同一時間打開編輯頁

        object Edit(string note) => new
        {
            name,
            subName = "",
            date = opened.GetProperty("date").GetString(),
            start = opened.GetProperty("start").GetString(),
            end = opened.GetProperty("end").GetString(),
            hasLocation = false,
            host1UserID = "10001",
            note,
            participantUserIDs = new[] { Demo.DoctorEmployeeNo },
            allowWalkIn = false,
            rowVersion = opened.GetProperty("rowVersion").GetString(),
        };

        Assert.Equal(HttpStatusCode.OK, (await host.PutAsJsonAsync($"/api/meetings/{meetingNo}", Edit("第一個人的修改"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PutAsJsonAsync($"/api/meetings/{meetingNo}", Edit("第二個人的修改"))).StatusCode);
        Assert.Equal("第一個人的修改", (await Api.GetMeetingAsync(host, meetingNo)).GetProperty("note").GetString());
    }

    [IntegrationFact]
    public async Task MeetingWithSignIns_CannotBeDeleted_ButEmptyOneCan()
    {
        var host = factory.NewClient();
        await Api.LoginOrFailAsync(host, Demo.Host);

        var (empty, _, _) = await Api.CreateMeetingAsync(host, Api.Unique("可刪除"), [Demo.DoctorEmployeeNo]);
        Assert.Equal(HttpStatusCode.OK, (await host.DeleteAsync($"/api/meetings/{empty}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.DeleteAsync($"/api/meetings/{empty}")).StatusCode);

        var (used, token, end) = await Api.CreateMeetingAsync(host, Api.Unique("有簽到"), [Demo.DoctorEmployeeNo]);
        var session = factory.IssueMobileSession(Demo.DoctorEmployeeNo, used, token, end);
        await Api.MobileAsync(factory.NewClient(), HttpMethod.Post, $"/api/meetings/{used}/attendance/signin", session);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.DeleteAsync($"/api/meetings/{used}")).StatusCode);
    }
}
