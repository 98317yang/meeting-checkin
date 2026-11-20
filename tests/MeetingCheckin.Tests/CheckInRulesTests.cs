namespace MeetingCheckin.Tests;

public class CheckInRulesTests
{
    private static readonly DateTime Start = new(2026, 11, 20, 14, 0, 0);
    private static readonly DateTime End = new(2026, 11, 20, 15, 0, 0);

    public CheckInRulesTests()
    {
        CheckInRules.OpenMinutesBeforeStart = 15;
        CheckInRules.CloseMinutesAfterEnd = 30;
    }

    [Theory]
    [InlineData("13:44", false)]   // 開放前 1 分鐘
    [InlineData("13:45", true)]    // 開始前 15 分鐘開放
    [InlineData("14:30", true)]
    [InlineData("15:00", true)]    // 結束當下仍可簽到
    [InlineData("15:01", false)]   // 結束後不能再簽到
    public void SignIn_IsAllowedFromOpenTimeUntilEnd(string now, bool allowed)
    {
        var problem = CheckInRules.CheckWindow("signin", Start, End, At(now));
        Assert.Equal(allowed, problem is null);
    }

    [Theory]
    [InlineData("15:10", true)]
    [InlineData("15:30", true)]    // 結束後 30 分鐘內可簽退
    [InlineData("15:31", false)]
    public void SignOut_IsAllowedUntilCloseMinutesAfterEnd(string now, bool allowed)
    {
        var problem = CheckInRules.CheckWindow("signout", Start, End, At(now));
        Assert.Equal(allowed, problem is null);
    }

    [Fact]
    public void Messages_TellWhenSignInOpens()
    {
        Assert.Equal("本會議將於 2026/11/20 13:45 開放簽到。", CheckInRules.CheckWindow("signin", Start, End, At("09:00")));
    }

    private static DateTime At(string hhmm) => Start.Date.Add(TimeSpan.Parse(hhmm));
}
