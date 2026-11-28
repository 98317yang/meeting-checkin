namespace MeetingCheckin;

internal static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapGet("/api/client-device", async (HttpContext context, IWebHostEnvironment environment) =>
        {
            var result = await CheckHospitalPcAsync(context, environment);

            return Results.Ok(new
            {
                success = true,
                ipAddress = result.IpAddress,
                computerName = result.ComputerName,
                isHospitalComputer = result.IsHospitalComputer,
                detectionSource = result.DetectionSource
            });
        }).AllowAnonymous();

        // 健康檢查不需要登入，所以只回傳「正常／異常」，不帶資料庫名稱或錯誤原文；
        // 錯誤細節寫進伺服器紀錄，給管理者查看。
        app.MapGet("/api/health/database", async (IConfiguration configuration, ILogger<HealthCheckLog> logger) =>
        {
            try
            {
                await using var lineDb = new SqlConnection(GetConnection(configuration, "LineDb"));
                await using var meetingDb = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));

                await lineDb.OpenAsync();
                await meetingDb.OpenAsync();

                await using var lineDbCommand = new SqlCommand("SELECT DB_NAME();", lineDb);
                await using var meetingDbCommand = new SqlCommand("SELECT DB_NAME();", meetingDb);

                await lineDbCommand.ExecuteScalarAsync();
                await meetingDbCommand.ExecuteScalarAsync();

                return Results.Ok(new { success = true, message = "資料庫連線正常。" });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Database health check failed.");
                return Results.Json(new { success = false, message = "資料庫連線失敗。" }, statusCode: 503);
            }
        }).AllowAnonymous();
    }

}

// 只用來當作健康檢查紀錄的分類名稱。
internal sealed class HealthCheckLog;
