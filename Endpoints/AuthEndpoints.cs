namespace MeetingCheckin;

internal static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext httpContext, IConfiguration configuration) =>
        {
            var account = request.Account?.Trim().ToUpperInvariant();
            var password = request.Password ?? "";

            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(password))
            {
                return Results.BadRequest(new { success = false, message = "請輸入帳號與密碼。" });
            }

            if (LoginThrottle.IsLocked(account))
                return LoginThrottle.LockedResult();

            try
            {
                await using var connection = new SqlConnection(GetConnection(configuration, "LineDb"));
                await using var command = new SqlCommand("dbo.Login_Check", connection)
                {
                    CommandType = CommandType.StoredProcedure,
                    CommandTimeout = 30
                };

                command.Parameters.Add("@LogAccount", SqlDbType.NVarChar, 100).Value = account;
                command.Parameters.Add("@LogPASS", SqlDbType.NVarChar, 200).Value = password;

                var messageParameter = new SqlParameter("@RetuenMsg", SqlDbType.NVarChar, 500)
                {
                    Direction = ParameterDirection.Output
                };
                var nameParameter = new SqlParameter("@cnm", SqlDbType.NVarChar, 100)
                {
                    Direction = ParameterDirection.Output
                };
                var departmentParameter = new SqlParameter("@dpid", SqlDbType.NVarChar, 50)
                {
                    Direction = ParameterDirection.Output
                };
                var employeeIdParameter = new SqlParameter("@idno", SqlDbType.NVarChar, 50)
                {
                    Direction = ParameterDirection.Output
                };
                var operatorIdParameter = new SqlParameter("@opid", SqlDbType.NVarChar, 50)
                {
                    Direction = ParameterDirection.Output
                };

                command.Parameters.AddRange([
                    messageParameter,
                    nameParameter,
                    departmentParameter,
                    employeeIdParameter,
                    operatorIdParameter
                ]);

                await connection.OpenAsync();
                await command.ExecuteNonQueryAsync();

                var loginMessage = Output(messageParameter);
                var name = Output(nameParameter);
                var departmentId = Output(departmentParameter);
                var operatorId = Output(operatorIdParameter);

                var success = loginMessage.StartsWith("True", StringComparison.OrdinalIgnoreCase)
                    || (!loginMessage.StartsWith("False", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(name));

                if (!success)
                {
                    LoginThrottle.RecordFailure(account);
                    return Results.Json(
                        new { success = false, message = "帳號或密碼輸入錯誤。" },
                        statusCode: 401);
                }
                LoginThrottle.Reset(account);

                // Login_Check 的正確欄位對應：
                // @cnm  = 登入顯示名稱
                // @dpid = 單位代碼
                // @idno = 身分證字號（不回傳前端）
                // @opid = 員工編號／操作人員編號
                var employeeId = operatorId;
                var userId = operatorId;
                var department = "";
                var title = "";

                if (string.IsNullOrWhiteSpace(employeeId))
                {
                    return Results.Json(new
                    {
                        success = false,
                        message = "登入成功，但 Login_Check 未回傳員工編號（OPID）。"
                    }, statusCode: 400);
                }

                // 科別名稱一律依醫院提供的 GetEmployee @TYPE='DP' 取得。
                try
                {
                    await using var departmentCommand = new SqlCommand("dbo.GetEmployee", connection)
                    {
                        CommandType = CommandType.StoredProcedure,
                        CommandTimeout = 30
                    };
                    departmentCommand.Parameters.Add("@TYPE", SqlDbType.NVarChar, 10).Value = "DP";

                    await using var departmentReader = await departmentCommand.ExecuteReaderAsync();
                    while (await departmentReader.ReadAsync())
                    {
                        var rowDepartmentId = GetText(departmentReader, "DPID", "DepartmentID", "DeptID");
                        if (!rowDepartmentId.Equals(departmentId, StringComparison.OrdinalIgnoreCase)) continue;
                        department = GetText(departmentReader, "DPCNM", "DepartmentName", "DeptName", "DPNM");
                        break;
                    }
                }
                catch
                {
                    // 若醫院 SP 欄位調整，下面仍會以 UserInfo 作為備援。
                }

                // 職稱優先依 GetEmployee @TYPE='EMP', @DPID 取得。
                try
                {
                    await using var employeeCommand = new SqlCommand("dbo.GetEmployee", connection)
                    {
                        CommandType = CommandType.StoredProcedure,
                        CommandTimeout = 30
                    };
                    employeeCommand.Parameters.Add("@TYPE", SqlDbType.NVarChar, 10).Value = "EMP";
                    employeeCommand.Parameters.Add("@DPID", SqlDbType.NVarChar, 50).Value =
                        string.IsNullOrWhiteSpace(departmentId) ? DBNull.Value : departmentId;

                    await using var employeeReader = await employeeCommand.ExecuteReaderAsync();
                    while (await employeeReader.ReadAsync())
                    {
                        var rowEmployeeId = GetText(employeeReader,
                            "SNTOPID", "OPID", "EmployeeNo", "EmployeeID", "UserID");
                        if (!rowEmployeeId.Equals(employeeId, StringComparison.OrdinalIgnoreCase)) continue;

                        title = GetText(employeeReader, "DUTNM", "DutyName", "JobTitle", "Title");
                        if (string.IsNullOrWhiteSpace(department))
                        {
                            department = GetText(employeeReader, "DPCNM", "DepartmentName", "DeptName", "DPNM");
                        }
                        break;
                    }
                }
                catch
                {
                    // 由 UserInfo 備援。
                }

                // 若登入 SP 只回傳單位代碼，直接以 UserInfo 的單位對照補成中文名稱。
                if (!string.IsNullOrWhiteSpace(departmentId)
                    && (string.IsNullOrWhiteSpace(department)
                        || department.Equals(departmentId, StringComparison.OrdinalIgnoreCase)))
                {
                    const string departmentNameSql = """
                        SELECT TOP (1) LTRIM(RTRIM(DPCNM))
                        FROM dbo.UserInfo
                        WHERE DPID = @DepartmentID
                          AND NULLIF(LTRIM(RTRIM(DPCNM)), N'') IS NOT NULL;
                        """;
                    await using var departmentNameCommand = new SqlCommand(departmentNameSql, connection);
                    departmentNameCommand.Parameters.Add("@DepartmentID", SqlDbType.NVarChar, 50).Value = departmentId;
                    var resolvedDepartmentName = Convert.ToString(await departmentNameCommand.ExecuteScalarAsync())?.Trim() ?? "";
                    if (!string.IsNullOrWhiteSpace(resolvedDepartmentName))
                        department = resolvedDepartmentName;
                }

                if (string.IsNullOrWhiteSpace(department) || string.IsNullOrWhiteSpace(title))
                {
                    const string fallbackSql = """
                        SELECT TOP (1)
                            LTRIM(RTRIM(DPCNM)) AS DepartmentName,
                            LTRIM(RTRIM(DUTNM)) AS DutyName
                        FROM dbo.UserInfo
                        WHERE SNTOPID = @EmployeeNo;
                        """;
                    await using var fallbackCommand = new SqlCommand(fallbackSql, connection);
                    fallbackCommand.Parameters.Add("@EmployeeNo", SqlDbType.NVarChar, 50).Value = employeeId;
                    await using var fallbackReader = await fallbackCommand.ExecuteReaderAsync();
                    if (await fallbackReader.ReadAsync())
                    {
                        if (string.IsNullOrWhiteSpace(department))
                            department = fallbackReader["DepartmentName"] is DBNull ? "" : fallbackReader["DepartmentName"].ToString()?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(title))
                            title = fallbackReader["DutyName"] is DBNull ? "" : fallbackReader["DutyName"].ToString()?.Trim() ?? "";
                    }
                }

                // 簽發登入 cookie。之後電腦版 API 都從這裡取得員工編號。
                var identity = new ClaimsIdentity(
                    [new Claim(EmployeeNoClaim, employeeId), new Claim(ClaimTypes.Name, name ?? "")],
                    CookieAuthenticationDefaults.AuthenticationScheme);
                await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

                return Results.Ok(new
                {
                    success = true,
                    message = "登入成功",
                    user = new
                    {
                        account,
                        name,
                        employeeId,
                        sntopid = employeeId,
                        department = department,
                        departmentId = departmentId,
                        dpid = departmentId,
                        dpcnm = department,
                        title,
                        dutnm = title,
                        operatorId = employeeId,
                        userID = employeeId
                    }
                });
            }
            catch (Exception ex)
            {
                // 錯誤細節只寫入伺服器紀錄；登入 API 不需登入，回應裡不能帶資料庫的錯誤原文。
                httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Login").LogError(ex, "Login failed with an unexpected error.");
                return Results.Json(new
                {
                    success = false,
                    message = "登入處理發生錯誤，請洽資訊室。"
                }, statusCode: 500);
            }
        }).AllowAnonymous();

        app.MapPost("/api/auth/logout", async (HttpContext httpContext) =>
        {
            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { success = true });
        }).AllowAnonymous();

        // 前端可用來確認登入是否仍有效，以及目前登入的是誰。
        app.MapGet("/api/auth/me", (ClaimsPrincipal principal) =>
            Results.Ok(new { success = true, employeeNo = GetUserId(principal), name = principal.Identity?.Name ?? "" }));
    }

}
