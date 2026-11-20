namespace MeetingCheckin.Tests;

public class LoginThrottleTests
{
    [Fact]
    public void FifthFailure_LocksAccount_UntilWindowPasses()
    {
        using var clock = new FixedClock(new DateTime(2026, 11, 20, 9, 0, 0));
        var account = "T-" + Guid.NewGuid();

        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++) LoginThrottle.RecordFailure(account);
        Assert.False(LoginThrottle.IsLocked(account));

        LoginThrottle.RecordFailure(account);
        Assert.True(LoginThrottle.IsLocked(account));

        clock.Advance(LoginThrottle.Window + TimeSpan.FromSeconds(1));
        Assert.False(LoginThrottle.IsLocked(account));
    }

    [Fact]
    public void SuccessfulLogin_ClearsFailures()
    {
        var account = "T-" + Guid.NewGuid();
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++) LoginThrottle.RecordFailure(account);
        LoginThrottle.Reset(account);
        LoginThrottle.RecordFailure(account);
        Assert.False(LoginThrottle.IsLocked(account));
    }

    [Fact]
    public void Accounts_AreCountedSeparately()
    {
        var a = "T-" + Guid.NewGuid();
        var b = "T-" + Guid.NewGuid();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++) LoginThrottle.RecordFailure(a);
        Assert.True(LoginThrottle.IsLocked(a));
        Assert.False(LoginThrottle.IsLocked(b));
    }
}
