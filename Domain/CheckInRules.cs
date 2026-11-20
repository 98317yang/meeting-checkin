namespace MeetingCheckin;

// 簽到／簽退的時間規則。開放與截止時間由設定檔決定（Program.cs 啟動時讀入）。
public static class CheckInRules
{
    // 開始前幾分鐘開放簽到（0 表示開始時間才開放）。
    public static int OpenMinutesBeforeStart { get; set; }

    // 會議結束後幾分鐘內仍可簽退（結束後不能再簽到）。
    public static int CloseMinutesAfterEnd { get; set; } = 30;

    // 回傳不能操作的原因；可以操作時回傳 null。action 為 "signin" 或 "signout"。
    public static string? CheckWindow(string action, DateTime start, DateTime end, DateTime now)
    {
        var openAt = start.AddMinutes(-OpenMinutesBeforeStart);
        var closeAt = end.AddMinutes(CloseMinutesAfterEnd);
        return action switch
        {
            "signin" when now < openAt => $"本會議將於 {openAt:yyyy/MM/dd HH:mm} 開放簽到。",
            "signin" when now > end => "本會議已結束，無法再簽到。",
            "signout" when now > closeAt => $"本會議簽退已於 {closeAt:yyyy/MM/dd HH:mm} 截止。",
            _ => null
        };
    }
}
