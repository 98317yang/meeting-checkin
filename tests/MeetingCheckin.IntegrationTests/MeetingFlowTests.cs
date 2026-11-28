using Microsoft.Data.SqlClient;

namespace MeetingCheckin.IntegrationTests;

// 主持人建立會議 → 與會者用手機簽到 → 主持人在簽到名單看到 → 修改歷程有記錄。
[Collection("api")]
public class MeetingFlowTests(ApiFactory factory)
{
    [IntegrationFact]
    public async Task CreatedMeeting_AppearsInHostsMeetingList()
    {
        var host = factory.NewClient();
        await Api.LoginOrFailAsync(host, Demo.Host);
        var name = Api.Unique("建立測試");
        var (meetingNo, _, _) = await Api.CreateMeetingAsync(host, name, [Demo.DoctorEmployeeNo]);

        var list = await host.GetFromJsonAsync<JsonElement>("/api/meetings?keyword=" + Uri.EscapeDataString(name));
        Assert.Contains(list.EnumerateArray(), m => m.GetProperty("id").GetString() == meetingNo);
    }

    [IntegrationFact]
    public async Task MobileSignIn_ShowsOnHostSheet_AndIsRecordedInAuditLog()
    {
        var host = factory.NewClient();
        await Api.LoginOrFailAsync(host, Demo.Host);
        var (meetingNo, token, end) = await Api.CreateMeetingAsync(host, Api.Unique("簽到流程"), [Demo.DoctorEmployeeNo]);

        // 陳志豪用手機簽到
        var session = factory.IssueMobileSession(Demo.DoctorEmployeeNo, meetingNo, token, end);
        var phone = factory.NewClient();
        var signIn = await Api.MobileAsync(phone, HttpMethod.Post, $"/api/meetings/{meetingNo}/attendance/signin", session);
        Assert.True(signIn.StatusCode == HttpStatusCode.OK, await signIn.Content.ReadAsStringAsync());

        var mine = await (await Api.MobileAsync(phone, HttpMethod.Get, $"/api/meetings/{meetingNo}/my-attendance", session))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(mine.GetProperty("signInTime").GetString()));

        // 主持人的簽到名單立刻看得到
        var sheet = await host.GetFromJsonAsync<JsonElement>($"/api/meetings/{meetingNo}/attendance");
        var row = sheet.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("userID").GetString() == Demo.DoctorEmployeeNo);
        Assert.Equal("出席", row.GetProperty("attendanceStatus").GetString());

        // 觸發程序留下修改歷程：誰、從哪裡簽到
        await using var connection = new SqlConnection(factory.LineMeetingConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT l.Action, l.ModifyUser, l.Source
            FROM dbo.MeetingAttendanceLog AS l
            JOIN dbo.MeetingParticipant AS p ON p.ParticipantID = l.ParticipantID
            WHERE p.MeetingNo = @MeetingNo AND p.EmployeeNo = @EmployeeNo;
            """, connection);
        command.Parameters.AddWithValue("@MeetingNo", meetingNo);
        command.Parameters.AddWithValue("@EmployeeNo", Demo.DoctorEmployeeNo);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "沒有修改歷程");
        Assert.Equal("I", reader.GetString(0));
        Assert.Equal(Demo.DoctorEmployeeNo, reader.GetString(1));
        Assert.Equal("mobile", reader.GetString(2));
    }

    [IntegrationFact]
    public async Task SigningInTwice_DoesNotCreateDuplicateRecords()
    {
        var host = factory.NewClient();
        await Api.LoginOrFailAsync(host, Demo.Host);
        var (meetingNo, token, end) = await Api.CreateMeetingAsync(host, Api.Unique("重複簽到"), [Demo.DoctorEmployeeNo]);
        var session = factory.IssueMobileSession(Demo.DoctorEmployeeNo, meetingNo, token, end);
        var phone = factory.NewClient();

        // 同時送出兩次簽到（例如手機連點），只能成功一次，簽到時間也不會被第二次覆蓋
        var attempts = await Task.WhenAll(
            Api.MobileAsync(phone, HttpMethod.Post, $"/api/meetings/{meetingNo}/attendance/signin", session),
            Api.MobileAsync(phone, HttpMethod.Post, $"/api/meetings/{meetingNo}/attendance/signin", session));
        Assert.Contains(attempts, r => r.StatusCode == HttpStatusCode.OK);

        await using var connection = new SqlConnection(factory.LineMeetingConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT COUNT(*)
            FROM dbo.MeetingAttendanceLog AS l
            JOIN dbo.MeetingParticipant AS p ON p.ParticipantID = l.ParticipantID
            WHERE p.MeetingNo = @MeetingNo AND p.EmployeeNo = @EmployeeNo;
            """, connection);
        command.Parameters.AddWithValue("@MeetingNo", meetingNo);
        command.Parameters.AddWithValue("@EmployeeNo", Demo.DoctorEmployeeNo);
        Assert.Equal(1, (int)(await command.ExecuteScalarAsync())!);   // 只有一筆「新增簽到」，沒有第二次寫入
    }

    [IntegrationFact]
    public async Task RegeneratedQrCode_InvalidatesSessionsFromOldCode()
    {
        var host = factory.NewClient();
        await Api.LoginOrFailAsync(host, Demo.Host);
        var (meetingNo, oldToken, end) = await Api.CreateMeetingAsync(host, Api.Unique("QR 重產"), [Demo.DoctorEmployeeNo]);
        var session = factory.IssueMobileSession(Demo.DoctorEmployeeNo, meetingNo, oldToken, end);
        var phone = factory.NewClient();
        var url = $"/api/meetings/{meetingNo}/my-attendance";

        Assert.Equal(HttpStatusCode.OK, (await Api.MobileAsync(phone, HttpMethod.Get, url, session)).StatusCode);

        var regenerate = await host.PostAsync($"/api/meetings/{meetingNo}/qr-code/regenerate", null);
        Assert.Equal(HttpStatusCode.OK, regenerate.StatusCode);
        var newToken = (await regenerate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("qrCode").GetString();
        Assert.NotEqual(oldToken, newToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.MobileAsync(phone, HttpMethod.Get, url, session)).StatusCode);
    }
}
