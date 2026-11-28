namespace MeetingCheckin;

internal static class QrEndpoints
{
    public static void MapQrEndpoints(this WebApplication app)
    {
        app.MapGet("/api/qr-code/{token}", async (string token, ClaimsPrincipal principal, HttpRequest request, IConfiguration configuration) =>
        {
            var userId = GetUserId(principal);
            if (string.IsNullOrWhiteSpace(token)) return Results.BadRequest("缺少 QR Token。");
            if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

            await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
            await connection.OpenAsync();
            const string permissionSql = """
                SELECT COUNT(1)
                FROM dbo.Meeting
                WHERE QRCode = @Token
                  AND ISNULL(IsDeleted, 0) = 0
                  AND (@User = CreateUser OR @User IN (Host1, Host2, Host3));
                """;
            await using var check = new SqlCommand(permissionSql, connection);
            check.Parameters.Add("@Token", SqlDbType.VarChar, 100).Value = token.Trim();
            check.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = userId.Trim();
            if (Convert.ToInt32(await check.ExecuteScalarAsync()) == 0)
                return Results.Json(new { success = false, message = "您沒有權限查看此會議 QR Code。" }, statusCode: 403);

            var targetUrl = GetLiffUrl(configuration, request, token);

            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(targetUrl, QRCodeGenerator.ECCLevel.Q);
            var png = new PngByteQRCode(data).GetGraphic(12);
            return Results.File(png, "image/png");
        });

        // 重新產生 QR Code：QR Code 被拍照外流時，主持人可以讓舊碼立即失效。
        // 已用舊碼取得的手機憑證也會一起失效（CheckMobileSessionAsync 會比對目前的 token），
        // 現場的人重新掃描新的 QR Code 即可；已完成的簽到紀錄不受影響。
        app.MapPost("/api/meetings/{meetingNo}/qr-code/regenerate", async (string meetingNo, ClaimsPrincipal principal, IConfiguration configuration) =>
        {
            var userId = GetUserId(principal);
            return await RetryOnDeadlock(async () =>
            {
                await using var connection = new SqlConnection(GetConnection(configuration, "LineMeetingDb"));
                await connection.OpenAsync();
                await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

                await using (var check = new SqlCommand("""
                    SELECT EndTime
                    FROM dbo.Meeting WITH (UPDLOCK, HOLDLOCK)
                    WHERE MeetingNo = @MeetingNo
                      AND IsDeleted = 0
                      AND @User IN (CreateUser, Host1, Host2, Host3);
                    """, connection, tx))
                {
                    check.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
                    check.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = userId;
                    var end = await check.ExecuteScalarAsync();
                    if (end is null or DBNull)
                        return Results.Json(new { success = false, message = "找不到會議，或您沒有權限。" }, statusCode: 403);
                    if (TaipeiTime.Now > Convert.ToDateTime(end).AddMinutes(SignInRule.CloseMinutesAfterEnd))
                        return Results.BadRequest(new { success = false, message = "會議已結束，不需要重新產生 QR Code。" });
                }

                var token = await NewUniqueTokenAsync(connection, tx, meetingNo.Trim());
                await using (var update = new SqlCommand("""
                    UPDATE dbo.Meeting
                    SET QRCode = @Token, UpdateUser = @User, UpdateTime = dbo.fn_Now()
                    WHERE MeetingNo = @MeetingNo;
                    """, connection, tx))
                {
                    update.Parameters.Add("@Token", SqlDbType.VarChar, 100).Value = token;
                    update.Parameters.Add("@User", SqlDbType.VarChar, 20).Value = userId;
                    update.Parameters.Add("@MeetingNo", SqlDbType.VarChar, 20).Value = meetingNo.Trim();
                    await update.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
                return Results.Ok(new { success = true, qrCode = token });
            });
        });

        app.MapGet("/api/qr-target/{token}", (string token, HttpRequest request, IConfiguration configuration) =>
        {
            if (string.IsNullOrWhiteSpace(token))
                return Results.BadRequest(new { success = false, message = "缺少 QR Token。" });

            var url = GetLiffUrl(configuration, request, token);
            return Results.Ok(new { success = true, url, isLocalhost = false, type = "meeting" });
        });
    }

}
