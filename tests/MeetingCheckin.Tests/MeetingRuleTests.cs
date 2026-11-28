namespace MeetingCheckin.Tests;

public class MeetingRuleTests
{
    private static readonly DateTime Start = new(2026, 11, 20, 14, 0, 0);
    private static readonly DateTime End = new(2026, 11, 20, 15, 0, 0);
    private static readonly string?[] Hosts = ["10001", "20001", null];
    private static readonly DateTime DuringMeeting = new(2026, 11, 20, 14, 30, 0);
    private static readonly DateTime AfterMeeting = new(2026, 11, 20, 16, 0, 0);

    private static string? Check(bool hasAttendance = false, DateTime? newStart = null, string?[]? newHosts = null,
        bool newWalkIn = false, DateTime? now = null) =>
        MeetingRule.CheckMeetingFields(hasAttendance, Start, End, Hosts, false,
            newStart ?? Start, newHosts ?? Hosts, newWalkIn, now ?? DuringMeeting);

    [Fact]
    public void Version_MissingOrDifferent_IsRejected()
    {
        Assert.NotNull(MeetingRule.CheckVersion(null, "AAAA"));
        Assert.NotNull(MeetingRule.CheckVersion("AAAA", "BBBB"));
        Assert.Null(MeetingRule.CheckVersion("AAAA", "AAAA"));
    }

    [Fact]
    public void Date_CanChangeBeforeAnyoneSignsIn() =>
        Assert.Null(Check(hasAttendance: false, newStart: Start.AddDays(1)));

    [Fact]
    public void Date_IsLockedAfterSomeoneSignsIn() =>
        Assert.Equal("已有人簽到，不能修改會議日期。", Check(hasAttendance: true, newStart: Start.AddDays(1)));

    [Fact]
    public void Time_OnSameDay_CanStillChangeAfterSignIn() =>
        Assert.Null(Check(hasAttendance: true, newStart: Start.AddHours(1)));

    [Fact]
    public void HostsAndOpenSetting_CanChangeWhileMeetingIsRunning()
    {
        Assert.Null(Check(newHosts: ["10001", null, null]));
        Assert.Null(Check(newWalkIn: true));
    }

    [Fact]
    public void Hosts_AreLockedAfterMeetingEnds() =>
        Assert.Equal("會議已結束，不能修改主持人。", Check(newHosts: ["10001", null, null], now: AfterMeeting));

    [Fact]
    public void Hosts_SameValuesWithSpacesOrEmpty_AreNotAChange() =>
        Assert.Null(Check(newHosts: [" 10001 ", "20001", ""], now: AfterMeeting));

    [Fact]
    public void OpenSetting_IsLockedAfterMeetingEnds() =>
        Assert.Equal("會議已結束，不能修改是否開放非名單人員加入。", Check(newWalkIn: true, now: AfterMeeting));

    [Fact]
    public void InvitedList_IsLockedOnlyAfterMeetingEnds()
    {
        var existing = new HashSet<string>(["30001", "40002"], StringComparer.OrdinalIgnoreCase);
        var changed = new HashSet<string>(["30001"], StringComparer.OrdinalIgnoreCase);
        var sameButReordered = new HashSet<string>(["40002", "30001"], StringComparer.OrdinalIgnoreCase);

        Assert.Null(MeetingRule.CheckInvitedList(false, existing, changed));
        Assert.NotNull(MeetingRule.CheckInvitedList(true, existing, changed));
        Assert.Null(MeetingRule.CheckInvitedList(true, existing, sameButReordered));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]    // 已有人簽到
    [InlineData(false, false, false)]  // 主持人但不是建立者
    public void Delete_OnlyCreatorAndOnlyBeforeAnyoneSignsIn(bool isCreator, bool hasAttendance, bool expected) =>
        Assert.Equal(expected, MeetingRule.CanDelete(isCreator, hasAttendance));
}
