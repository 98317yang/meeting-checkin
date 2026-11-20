namespace MeetingCheckin;

// 修改、刪除會議的限制。SaveMeeting 與刪除 API 從資料庫讀出現況後交給這裡判斷，
// 規則集中在一處，也方便單元測試。違反規則時回傳錯誤訊息，沒問題回傳 null。
public static class MeetingEditRules
{
    // 編輯頁送回開啟當下的版本（rowversion）。不同代表期間有人存過，避免後存的人覆蓋前一個人。
    public static string? CheckVersion(string? submittedVersion, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(submittedVersion))
            return "編輯頁版本過舊，請重新整理後再儲存。";
        return submittedVersion == currentVersion
            ? null
            : "這場會議在您開啟後已被其他人修改，本次未儲存。請重新整理後再編輯。";
    }

    public static bool HasEnded(DateTime originalEnd, DateTime now) => now > originalEnd;

    // 會議本身的欄位：
    //  - 已有人簽到：不能改日期，否則簽到時間會跟著被搬到另一天。
    //  - 會議結束後：主持人、開放設定鎖定（名稱、時間、地點、備註仍可修改）。
    public static string? CheckMeetingFields(
        bool hasAttendance,
        DateTime originalStart,
        DateTime originalEnd,
        IReadOnlyList<string?> originalHosts,
        bool originalAllowUnlisted,
        DateTime newStart,
        IReadOnlyList<string?> newHosts,
        bool newAllowUnlisted,
        DateTime now)
    {
        if (hasAttendance && newStart.Date != originalStart.Date)
            return "已有人簽到，不能修改會議日期。";

        if (!HasEnded(originalEnd, now))
            return null;

        static string Norm(string? value) => value?.Trim() ?? "";
        if (!originalHosts.Select(Norm).SequenceEqual(newHosts.Select(Norm), StringComparer.OrdinalIgnoreCase))
            return "會議已結束，不能修改主持人。";
        if (originalAllowUnlisted != newAllowUnlisted)
            return "會議已結束，不能修改是否開放非名單人員加入。";
        return null;
    }

    // 會議結束後，受邀名單不能再增減。
    public static string? CheckInvitedList(bool meetingEnded, IReadOnlySet<string> existingInvited, IReadOnlySet<string> submittedInvited) =>
        meetingEnded && !submittedInvited.SetEquals(existingInvited) ? "會議已結束，不能修改參與名單。" : null;

    // 只有建立者能刪除，而且已有人簽到的會議不能刪除，避免簽到紀錄跟著從畫面消失。
    public static bool CanDelete(bool isCreator, bool hasAttendance) => isCreator && !hasAttendance;
}
