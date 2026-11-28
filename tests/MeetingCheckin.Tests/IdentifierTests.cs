using System.Text.RegularExpressions;

namespace MeetingCheckin.Tests;

public class IdentifierTests
{
    [Fact]
    public void MeetingToken_HasHospitalFormat_AndIsNotRepeated()
    {
        var tokens = Enumerable.Range(0, 10_000).Select(_ => QrToken.NewToken("2026110001")).ToList();
        Assert.All(tokens, t => Assert.Matches(new Regex("^M-[0-9a-f]{32}$"), t));
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public void GuestKey_IsStablePerLineUser_AndFitsColumn()
    {
        var key = QrToken.MakeGuestKey("U1234567890abcdef");
        Assert.Equal(key, QrToken.MakeGuestKey(" U1234567890abcdef "));
        Assert.NotEqual(key, QrToken.MakeGuestKey("U0000000000000000"));
        Assert.Matches("^G[0-9a-f]{19}$", key);   // MeetingParticipant.GuestKey 為 VARCHAR(20)
    }

    [Fact]
    public void ParticipantRowId_UsesEmployeeNoForStaff_AndParticipantIdForGuests()
    {
        Assert.Equal("10001", ParticipantHelper.GetRowKey(1, " 10001 ", 7));
        Assert.Equal("EXT|7", ParticipantHelper.GetRowKey(2, DBNull.Value, 7));
    }

    [Theory]
    [InlineData("09:05", 9, 5)]
    [InlineData(" 18:52 ", 18, 52)]
    public void AttendanceTime_IsPlacedOnMeetingDate(string text, int hour, int minute)
    {
        var date = new DateTime(2026, 11, 20);
        Assert.Equal(date.AddHours(hour).AddMinutes(minute), ParticipantHelper.ParseAttendanceTime(date, text));
    }

    [Fact]
    public void AttendanceTime_EmptyMeansNotSignedIn_InvalidIsRejected()
    {
        Assert.Null(ParticipantHelper.ParseAttendanceTime(DateTime.Today, ""));
        Assert.Throws<InvalidOperationException>(() => ParticipantHelper.ParseAttendanceTime(DateTime.Today, "abc"));
    }
}
