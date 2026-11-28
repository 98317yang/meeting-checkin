namespace MeetingCheckin;

internal static class MobileSession
{
    internal static string CreateMobileSession(
        IDataProtectionProvider dataProtectionProvider,
        int participantType,
        string employeeNo,
        string meetingNo,
        string meetingToken,
        DateTime meetingEndDateTime,
        string? guestName = null,
        string? guestOrg = null,
        string? guestTitle = null)
    {
        var protector = dataProtectionProvider.CreateProtector("MeetingCheckin.MobileSession");

        // 這裡只是上限，避免憑證永久有效。實際能不能用，每次都由 CheckMobileSessionAsync
        // 依資料庫裡「目前」的會議結束時間判斷，所以主持人延長會議時不用重新掃碼。
        // 會議時間是台北時間，換算成 UTC 時也要用台北時區，不能用主機的時區。
        var cap = meetingEndDateTime.AddMinutes(SignInRule.CloseMinutesAfterEnd).AddDays(7);
        var expires = new DateTimeOffset(DateTime.SpecifyKind(cap, DateTimeKind.Unspecified), TaipeiTime.TimeZone.GetUtcOffset(cap));

        var payload = new MobileSessionData(
            participantType,
            employeeNo.Trim(),
            guestName?.Trim() ?? "",
            guestOrg?.Trim() ?? "",
            guestTitle?.Trim() ?? "",
            meetingNo.Trim(),
            meetingToken.Trim(),
            expires.ToUnixTimeSeconds());

        return protector.Protect(JsonSerializer.Serialize(payload));
    }

    internal static bool TryReadMobileSession(
        HttpRequest request,
        IDataProtectionProvider dataProtectionProvider,
        out MobileSessionData? payload)
    {
        payload = null;
        var protectedValue = request.Headers["X-Meeting-Session"].FirstOrDefault()?.Trim();

        if (string.IsNullOrWhiteSpace(protectedValue))
            return false;

        try
        {
            var protector = dataProtectionProvider.CreateProtector("MeetingCheckin.MobileSession");
            var json = protector.Unprotect(protectedValue);
            payload = JsonSerializer.Deserialize<MobileSessionData>(json);

            if (payload is null
                || payload.ParticipantType is not (1 or 2)
                || string.IsNullOrWhiteSpace(payload.EmployeeNo)
                || string.IsNullOrWhiteSpace(payload.MeetingNo)
                || string.IsNullOrWhiteSpace(payload.MeetingToken)
                || payload.ExpiresUnixTime <= TaipeiTime.UtcNow().ToUnixTimeSeconds())
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

    // 用資料庫的現況檢查手機憑證：
    //  1. 憑證裡的 QR token 必須是會議目前的 token。主持人重新產生 QR Code 後，舊碼和用舊碼取得的憑證立即失效。
    //  2. 以「目前」的會議結束時間判斷是否已過可簽退時間，主持人延長會議時自動跟著延長。
    // 沒問題時回傳 null。
    internal static async Task<IResult?> CheckMobileSessionAsync(MobileSessionData session, IConfiguration configuration)
    {
        await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT QRCode, EndTime FROM dbo.Meeting WHERE MeetingNo = @MeetingNo AND IsDeleted = 0;", connection);
        command.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = session.MeetingNo;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return SessionExpired();
        if (!string.Equals(reader["QRCode"].ToString()?.Trim(), session.MeetingToken, StringComparison.Ordinal))
            return SessionExpired("會議 QR Code 已更新，請重新掃描現場的 QR Code。");
        if (TaipeiTime.Now > Convert.ToDateTime(reader["EndTime"]).AddMinutes(SignInRule.CloseMinutesAfterEnd))
            return SessionExpired("會議已結束，簽到與簽退時間已截止。");
        return null;
    }

    internal static IResult SessionExpired() =>
        SessionExpired("LINE 會議驗證已失效，請重新掃描會議 QR Code。");

    internal static IResult SessionExpired(string message) =>
        Results.Json(new { success = false, code = "LIFF_SESSION_REQUIRED", message }, statusCode: StatusCodes.Status401Unauthorized);


}
