using System.Text.RegularExpressions;

namespace MeetingCheckin.Tests;

public class IdentifierTests
{
    [Fact]
    public void MeetingToken_HasHospitalFormat_AndIsNotRepeated()
    {
        var tokens = Enumerable.Range(0, 10_000).Select(_ => MeetingTokens.CreateMeetingToken("2026110001")).ToList();
        Assert.All(tokens, t => Assert.Matches(new Regex("^M-[0-9a-f]{32}$"), t));
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public void GuestKey_IsStablePerLineUser_AndFitsColumn()
    {
        var key = MeetingTokens.CreateGuestKey("U1234567890abcdef");
        Assert.Equal(key, MeetingTokens.CreateGuestKey(" U1234567890abcdef "));
        Assert.NotEqual(key, MeetingTokens.CreateGuestKey("U0000000000000000"));
        Assert.Matches("^G[0-9a-f]{19}$", key);   // MeetingParticipant.GuestKey 為 VARCHAR(20)
    }

    [Fact]
    public void ParticipantRowId_UsesEmployeeNoForStaff_AndParticipantIdForGuests()
    {
        Assert.Equal("10001", Participants.ParticipantRowId(1, " 10001 ", 7));
        Assert.Equal("EXT|7", Participants.ParticipantRowId(2, DBNull.Value, 7));
    }

    [Theory]
    [InlineData("09:05", 9, 5)]
    [InlineData(" 18:52 ", 18, 52)]
    public void AttendanceTime_IsPlacedOnMeetingDate(string text, int hour, int minute)
    {
        var date = new DateTime(2026, 11, 20);
        Assert.Equal(date.AddHours(hour).AddMinutes(minute), Participants.ParseAttendanceTime(date, text));
    }

    [Fact]
    public void AttendanceTime_EmptyMeansNotSignedIn_InvalidIsRejected()
    {
        Assert.Null(Participants.ParseAttendanceTime(DateTime.Today, ""));
        Assert.Throws<InvalidOperationException>(() => Participants.ParseAttendanceTime(DateTime.Today, "abc"));
    }
}
