using DImage.Api.Auth;

namespace DImage.Api.Endpoints;

/// <summary>
/// 授权接口:密码换取 JWT。
/// </summary>
public static class AuthEndpoints
{
    /// <summary>换取 JWT 的请求体。</summary>
    public sealed record TokenRequest(string? Password);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapPost("/auth/token", (
                TokenRequest? request,
                CredentialProvider credentials,
                JwtTokenService jwt) =>
            {
                if (string.IsNullOrEmpty(request?.Password))
                {
                    return Results.Json(
                        new { error = "password_required" },
                        statusCode: StatusCodes.Status400BadRequest);
                }

                // 不区分「密码错误」与「凭据未配置」,避免信息枚举
                if (!credentials.VerifyPassword(request.Password))
                {
                    return Results.Json(
                        new { error = "invalid_password" },
                        statusCode: StatusCodes.Status401Unauthorized);
                }

                var (token, expiresAt) = jwt.CreateToken();

                return Results.Ok(new
                {
                    accessToken = token,
                    tokenType = "Bearer",
                    expiresIn = jwt.ExpireDays * 24 * 60 * 60,
                    expiresAt
                });
            })
            .Accepts<TokenRequest>("application/json")
            .WithName("IssueToken")
            .WithTags("Auth")
            .AllowAnonymous();

        return app;
    }
}
