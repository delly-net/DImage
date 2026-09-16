using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DImage.Api.Auth;

/// <summary>
/// MCP 静态 TOKEN 鉴权方案:从 <c>Authorization: Bearer &lt;TOKEN&gt;</c> 取原始值做定长比对。
/// 本方案<b>不解析 JWT</b>,与 JwtBearer 方案互不干扰。
/// </summary>
public sealed class McpTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string BearerPrefix = "Bearer ";

    private readonly CredentialProvider _credentials;

    public McpTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        CredentialProvider credentials)
        : base(options, logger, encoder)
    {
        _credentials = credentials;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(header) ||
            !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail("缺少 Bearer 凭据"));
        }

        var token = header[BearerPrefix.Length..].Trim();

        if (!_credentials.VerifyToken(token))
        {
            return Task.FromResult(AuthenticateResult.Fail("MCP TOKEN 无效"));
        }

        var identity = new ClaimsIdentity(
            new[] { new Claim(AuthConstants.Claims.AuthScheme, AuthConstants.Claims.AuthSchemeMcpToken) },
            Scheme.Name);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>
    /// 覆盖默认的空 401 响应体:被 <c>RequireAuthorization</c> 拦下时端点代码根本不会执行,
    /// 只有在此处写入 JSON,「无效 → 401 + valid:false」的语义才闭合。
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/json; charset=utf-8";

        await Response.WriteAsync("""{"valid":false,"error":"invalid_token"}""");
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.ContentType = "application/json; charset=utf-8";

        await Response.WriteAsync("""{"valid":false,"error":"forbidden"}""");
    }
}
