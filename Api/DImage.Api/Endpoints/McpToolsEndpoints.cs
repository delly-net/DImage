using DImage.Api.Mcp;

namespace DImage.Api.Endpoints;

/// <summary>
/// MCP 工具清单只读端点:供浏览器管理端「帮助」页展示服务信息与可用工具。
/// 本任务仅立起接口契约,tools 为空数组;后续接入 MCP SDK 时只改数据源,前端无需改动。
/// </summary>
public static class McpToolsEndpoints
{
    public static IEndpointRouteBuilder MapMcpToolsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/mcp/tools", (IConfiguration configuration) =>
            {
                // 只返回结构化数据,中文文案一律由前端渲染 —— 接口不承载界面职责,
                // 便于文案调整与后续国际化,也避免界面变更牵动后端契约
                return Results.Ok(new
                {
                    server = new
                    {
                        name = configuration["Service:Name"] ?? "DImage.Api",
                        version = configuration["Service:Version"] ?? "0.0.0",
                        protocolVersion = McpProtocol.ProtocolVersion
                    },
                    auth = new
                    {
                        scheme = "Bearer",
                        headerName = "Authorization"
                    },
                    tools = Array.Empty<object>()
                });
            })
            .WithName("GetMcpTools")
            .WithTags("Mcp")
            // 有意使用无参 RequireAuthorization() —— 走默认认证方案(JwtBearer),而非 McpAccess 策略。
            // 本端点服务对象是浏览器管理端,前端只持有 JWT、不持有 MCP TOKEN,照搬 McpAccess 必然 401,
            // 且失败形态与「令牌过期」高度相似,排查成本极高。
            // 请勿按「MCP 相关端点统一用 McpAccess」的约定字面改回 .RequireAuthorization(AuthConstants.Policies.McpAccess)。
            .RequireAuthorization();

        return app;
    }
}
