global using Xunit;
global using MeetingCheckin;

// TaipeiTime、SignInRule、LoginThrottle 是靜態狀態，測試之間不能同時改，因此關閉平行執行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MeetingCheckin.Tests;

// 把 TaipeiTime 固定在指定的台北時間，離開 using 區塊時還原。
internal sealed class FixedClock : IDisposable
{
    private readonly Func<DateTimeOffset> _original = TaipeiTime.UtcNow;
    public DateTimeOffset UtcNow { get; set; }

    public FixedClock(DateTime taipeiTime)
    {
        UtcNow = new DateTimeOffset(taipeiTime, TimeSpan.FromHours(8)).ToUniversalTime();
        TaipeiTime.UtcNow = () => UtcNow;
    }

    public void Advance(TimeSpan by) => UtcNow += by;
    public void Dispose() => TaipeiTime.UtcNow = _original;
}
