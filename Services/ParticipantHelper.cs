namespace MeetingCheckin;

internal static class ParticipantHelper
{
    internal static DateTime CombineDateTime(string date, string time)
    {
        if (!DateTime.TryParse($"{date} {time}", out var value))
        {
            throw new ArgumentException("日期或時間格式不正確。");
        }

        return value;
    }

    // 名單列在前端的識別值。院內人員用員工編號；訪客沒有可公開的穩定代號，用 ParticipantID。
    // 以前訪客是用「姓名|單位」當識別值，名字一改紀錄就對不上，這裡改用不會變的主鍵。
    internal static string GetRowKey(int participantType, object employeeNo, int participantId) =>
        participantType == 1 ? Convert.ToString(employeeNo)?.Trim() ?? "" : $"EXT|{participantId}";

    internal static string SourceName(int source) => source switch
    {
        1 => "Invited",
        2 => "WalkIn",
        _ => "Guest"
    };

    // 把前端送回的名單列識別值換回 ParticipantID，並確認這個人屬於指定的會議。
    internal static async Task<int?> FindParticipantId(SqlConnection connection, SqlTransaction tx, string meetingNo, string? rowId)
    {
        rowId = rowId?.Trim() ?? "";
        if (rowId.Length == 0) return null;

        var isGuest = rowId.StartsWith("EXT|", StringComparison.OrdinalIgnoreCase);
        int guestParticipantId = 0;
        if (isGuest && !int.TryParse(rowId.AsSpan(4), out guestParticipantId))
            return null;

        const string sql = """
            SELECT ParticipantID
            FROM dbo.MeetingParticipant
            WHERE MeetingNo = @MeetingNo
              AND ((@IsGuest = 0 AND EmployeeNo = @EmployeeNo)
                OR (@IsGuest = 1 AND ParticipantID = @ParticipantID AND ParticipantType = 2));
            """;
        await using var command = new SqlCommand(sql, connection, tx);
        command.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
        command.Parameters.Add("@IsGuest", SqlDbType.Bit).Value = isGuest;
        command.Parameters.Add("@EmployeeNo", SqlDbType.VarChar, 20).Value = isGuest ? DBNull.Value : rowId;
        command.Parameters.Add("@ParticipantID", SqlDbType.Int).Value = guestParticipantId;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    internal static DateTime? ParseAttendanceTime(DateTime date, string? time)
    {
        if (string.IsNullOrWhiteSpace(time)) return null;
        if (!TimeSpan.TryParse(time, out var value))
            throw new InvalidOperationException("簽到或簽退時間格式不正確。");
        return date.Add(value);
    }

    // 訪客在這場會議填過的資料；沒填過則回傳 null。
    internal static async Task<(string Name, string Organization, string Title)?> FindGuestProfile(
        SqlConnection connection, string meetingNo, string guestKey)
    {
        await using var command = new SqlCommand("""
            SELECT GuestName, GuestOrg, GuestTitle
            FROM dbo.MeetingParticipant
            WHERE MeetingNo = @MeetingNo AND GuestKey = @GuestKey;
            """, connection);
        command.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
        command.Parameters.Add("@GuestKey", SqlDbType.VarChar, 20).Value = guestKey;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (
            reader["GuestName"].ToString() ?? "",
            reader["GuestOrg"].ToString() ?? "",
            reader["GuestTitle"] is DBNull ? "" : reader["GuestTitle"].ToString() ?? "");
    }

}
