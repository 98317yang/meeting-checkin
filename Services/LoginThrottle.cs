namespace MeetingCheckin;

// 登入失敗次數限制：同一帳號 15 分鐘內失敗 5 次，就暫停登入，直到這 15 分鐘結束。
// 只計算失敗、以帳號為單位（電腦登入與 LINE 綁定共用），不分帳號是否存在，
// 因此無法藉由回應判斷帳號是否存在。紀錄放在記憶體中，伺服器重啟後歸零，適用單一伺服器。
public static class LoginThrottle
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, (int Count, DateTimeOffset FirstFailure)> Failures = new();

    public static bool IsLocked(string account)
    {
        if (!Failures.TryGetValue(account, out var entry)) return false;
        if (TaipeiTime.UtcNow() - entry.FirstFailure > Window)
        {
            Failures.TryRemove(account, out _);
            return false;
        }
        return entry.Count >= MaxFailures;
    }

    public static void RecordFailure(string account)
    {
        var now = TaipeiTime.UtcNow();
        Failures.AddOrUpdate(account,
            _ => (1, now),
            (_, entry) => now - entry.FirstFailure > Window ? (1, now) : (entry.Count + 1, entry.FirstFailure));

        // 有人用大量不同帳號嘗試時，順手清掉已過期的紀錄，避免記憶體一直成長。
        if (Failures.Count > 10_000)
            foreach (var (key, entry) in Failures)
                if (now - entry.FirstFailure > Window) Failures.TryRemove(key, out _);
    }

    public static void Reset(string account) => Failures.TryRemove(account, out _);

    public static IResult LockedResult() => Results.Json(
        new { success = false, code = "TOO_MANY_ATTEMPTS", message = "登入失敗次數過多，請 15 分鐘後再試。" },
        statusCode: StatusCodes.Status429TooManyRequests);
}
