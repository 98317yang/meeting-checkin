using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace MeetingCheckin.Tests;

public class MobileSessionTests
{
    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static bool Read(string? header, out MobileMeetingSessionPayload? payload)
    {
        var context = new DefaultHttpContext();
        if (header is not null) context.Request.Headers["X-Meeting-Session"] = header;
        return MobileSession.TryReadMobileMeetingSession(context.Request, Protection, out payload);
    }

    [Fact]
    public void IssuedSession_CanBeReadBack()
    {
        var end = AppClock.Now.AddHours(1);
        var session = MobileSession.CreateMobileMeetingSession(Protection, 1, "10001", "2026110001", "M-token", end);

        Assert.True(Read(session, out var payload));
        Assert.Equal("10001", payload!.EmployeeNo);
        Assert.Equal("2026110001", payload.MeetingNo);
        Assert.Equal("M-token", payload.MeetingToken);
    }

    [Fact]
    public void TamperedOrMissingSession_IsRejected()
    {
        var session = MobileSession.CreateMobileMeetingSession(Protection, 1, "10001", "2026110001", "M-token", AppClock.Now.AddHours(1));
        Assert.False(Read(session[..^2] + "xx", out _));
        Assert.False(Read(null, out _));
    }

    [Fact]
    public void SessionFromLongEndedMeeting_IsRejected()
    {
        var longAgo = AppClock.Now.AddDays(-30);
        var session = MobileSession.CreateMobileMeetingSession(Protection, 1, "10001", "2026110001", "M-token", longAgo);
        Assert.False(Read(session, out _));
    }
}
