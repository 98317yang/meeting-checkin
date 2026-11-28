namespace MeetingCheckin;

// 程式內所有「現在時間」都從這裡取得，固定換算成台北時間。
// 資料庫端對應的是 dbo.fn_Now()，兩邊用同一個時區，部署到其他時區的主機也不會錯開。
public static class TaipeiTime
{
    // IANA 時區名稱；.NET 6 之後在 Windows、Linux、macOS 都能使用。
    public static TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    // 測試時可以替換成固定時間。
    public static Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    public static DateTime Now => TimeZoneInfo.ConvertTime(UtcNow(), TimeZone).DateTime;
}
