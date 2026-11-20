namespace MeetingCheckin;

public sealed record LoginRequest(string? Account, string? Password);

public sealed record LiffMeetingAuthorizeRequest(string? Type, string? MeetingToken, string? IdToken);

public sealed record LiffBindRequest(string? IdToken, string? Account, string? Password);

public sealed record DevMobileSessionRequest(string? EmployeeNo, string? MeetingToken);
public sealed record DevMobileGuestSessionRequest(string? MeetingToken);

public sealed record GuestProfileRequest(string? Organization, string? Name, string? Title);

public sealed record MobileMeetingSessionPayload(
    int ParticipantType,
    string EmployeeNo,
    string ExternalName,
    string ExternalOrganization,
    string ExternalTitle,
    string MeetingNo,
    string MeetingToken,
    long ExpiresUnixTime);

public sealed record AttendanceSaveRequest(List<AttendanceRowRequest>? Rows);

public sealed record AttendanceRowRequest(
    string UserID,
    string? In,
    string? Out,
    string? Source,
    string? Operator,
    string? Note,
    AttendanceOriginal? Original);

// 主持人開啟簽到單時看到的值，用來確認存檔前這一列沒有被別人改過。
public sealed record AttendanceOriginal(string? In, string? Out, string? Operator, string? Note);

public sealed record OwnAttendanceRequest(string? EmployeeNo);

public sealed record ProxyAttendanceRequest(
    string Action,
    string ParticipantType,
    string? ParticipantEmployeeNo,
    string? ExternalName,
    string? ExternalOrganization,
    string? ProxyEmployeeNo,
    string? OperatedByEmployeeNo);

public sealed record MeetingSaveRequest(
    string Name,
    string? SubName,
    string Date,
    string Start,
    string End,
    bool HasLocation,
    string? LocationID,
    string? CustomLocation,
    string Host1UserID,
    string? Host2UserID,
    string? Host3UserID,
    string? Note,
    string CurrentUserID,
    List<string>? ParticipantUserIDs,
    bool AllowUnlistedParticipants,
    string? RowVersion);   // 編輯時送回開啟頁面當下的會議版本
