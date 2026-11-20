namespace MeetingCheckin.Tests;

public class AppClockTests
{
    [Fact]
    public void Now_IsTaipeiTime_RegardlessOfHostTimeZone()
    {
        var original = AppClock.UtcNow;
        try
        {
            // UTC 2026-11-20 06:00 = 台北 14:00
            AppClock.UtcNow = () => new DateTimeOffset(2026, 11, 20, 6, 0, 0, TimeSpan.Zero);
            Assert.Equal(new DateTime(2026, 11, 20, 14, 0, 0), AppClock.Now);
        }
        finally
        {
            AppClock.UtcNow = original;
        }
    }
}
