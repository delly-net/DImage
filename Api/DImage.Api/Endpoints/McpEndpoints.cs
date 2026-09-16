using DImage.Api.Auth;
using DImage.Api.Mcp;

namespace DImage.Api.Endpoints;

/// <summary>
/// MCP 就绪探针与 TOKEN 有效性验证。
/// 本任务不引入 MCP SDK,仅交付可复用的鉴权能力与验证接口。
/// </summary>
public static class McpEndpoints
{
    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapPost("/mcp/verify", (IConfiguration configuration) =>
            {
                // TOKEN 是静态环境变量,本身没有内建过期时间,故诚实返回 null 而非伪造过期时刻
                return Results.Ok(new
                {
                    valid = true,
                    tokenType = "static",
                    expiresAt = (DateTimeOffset?)null,
                    serverInfo = new
                    {
                        name = configuration["Service:Name"] ?? "DImage.Api",
                        version = configuration["Service:Version"] ?? "0.0.0",
                        protocolVersion = McpProtocol.ProtocolVersion,
                        authScheme = "Bearer"
                    }
                });
            })
            .WithName("VerifyMcpToken")
            .WithTags("Mcp")
            .RequireAuthorization(AuthConstants.Policies.McpAccess);

        // 根级:供运维探针使用,不占版本位;仅返回布尔配置状态,绝不回显 TOKEN 值
        app.MapGet("/health/mcp", (CredentialProvider credentials) => Results.Ok(new
            {
                status = "healthy",
                mcp = new
                {
                    authRequired = true,
                    tokenConfigured = credentials.TokenConfigured,
                    verifyEndpoint = "POST /api/v1/mcp/verify"
                }
            }))
            .WithName("GetMcpHealth")
            .WithTags("Service")
            .AllowAnonymous();

        return app;
    }
}
