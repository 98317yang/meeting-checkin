namespace MeetingCheckin;

internal static class MeetingTokens
{
    // QR token 格式「M- + 32 碼 MD5 十六進位」是醫院既有系統的規格，沿用以保持相容。
    // 這裡的 MD5 只是把內容轉成固定長度，不是拿來保護資料：無法被猜到的部分來自
    // Guid.NewGuid() 的隨機值（122 位元），所以不會因為 MD5 的碰撞弱點而能被推算。
    internal static string CreateMeetingToken(string meetingNo)
    {
        var raw = $"{meetingNo}|{Guid.NewGuid():N}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(raw));
        return $"M-{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    internal static async Task<string> CreateUniqueMeetingTokenAsync(
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

    internal static string CreateGuestKey(string lineUserId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(lineUserId.Trim()));
        return "G" + Convert.ToHexString(hash).ToLowerInvariant()[..19];
    }

}
