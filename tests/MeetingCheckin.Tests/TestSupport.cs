global using Xunit;
global using MeetingCheckin;

// AppClock、CheckInRules、LoginThrottle 是靜態狀態，測試之間不能同時改，因此關閉平行執行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MeetingCheckin.Tests;

// 把 AppClock 固定在指定的台北時間，離開 using 區塊時還原。
internal sealed class FixedClock : IDisposable
{
    private readonly Func<DateTimeOffset> _original = AppClock.UtcNow;
    public DateTimeOffset UtcNow { get; set; }

    public FixedClock(DateTime taipeiTime)
    {
        UtcNow = new DateTimeOffset(taipeiTime, TimeSpan.FromHours(8)).ToUniversalTime();
        AppClock.UtcNow = () => UtcNow;
    }

    public void Advance(TimeSpan by) => UtcNow += by;
    public void Dispose() => AppClock.UtcNow = _original;
}
