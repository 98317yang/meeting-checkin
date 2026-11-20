using System.Net;
using Microsoft.AspNetCore.Http;

namespace MeetingCheckin.Tests;

// 開發用端點與院內電腦判斷都依賴「是否為本機直接連線」。
// ngrok 等轉發工具的連線來源也是本機，必須靠轉發標頭與 Host 分辨。
public class RequestContextTests
{
    private static HttpContext Request(string ip, string host, string? forwardedFor = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        context.Request.Host = new HostString(host);
        if (forwardedFor is not null) context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        return context;
    }

    [Fact]
    public void Localhost_IsDirectLocal() =>
        Assert.True(RequestContext.IsDirectLocalRequest(Request("127.0.0.1", "localhost:5080")));

    [Fact]
    public void Tunnel_ForwardedThroughLoopback_IsNotLocal() =>
        Assert.False(RequestContext.IsDirectLocalRequest(Request("127.0.0.1", "abc.ngrok-free.app", "203.0.113.5")));

    [Fact]
    public void LoopbackWithPublicHostName_IsNotLocal() =>
        Assert.False(RequestContext.IsDirectLocalRequest(Request("127.0.0.1", "abc.ngrok-free.app")));

    [Fact]
    public void OtherMachine_IsNotLocal() =>
        Assert.False(RequestContext.IsDirectLocalRequest(Request("192.168.1.20", "localhost:5080")));

    [Fact]
    public void EmployeeNo_ComesFromLoginCookieClaim()
    {
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(RequestContext.EmployeeNoClaim, " 10001 ")], "Test"));
        Assert.Equal("10001", RequestContext.CurrentEmployeeNo(principal));
    }
}
