namespace MeetingCheckin.Tests;

public class TaipeiTimeTests
{
    [Fact]
    public void Now_IsTaipeiTime_RegardlessOfHostTimeZone()
    {
        var original = TaipeiTime.UtcNow;
        try
        {
            // UTC 2026-11-20 06:00 = 台北 14:00
            TaipeiTime.UtcNow = () => new DateTimeOffset(2026, 11, 20, 6, 0, 0, TimeSpan.Zero);
            Assert.Equal(new DateTime(2026, 11, 20, 14, 0, 0), TaipeiTime.Now);
        }
        finally
        {
            TaipeiTime.UtcNow = original;
        }
    }
}
