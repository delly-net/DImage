using DImage.Api.Mcp;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace DImage.Api.Endpoints;

/// <summary>
/// MCP 工具清单只读端点:供浏览器管理端「帮助」页展示服务信息与可用工具。
/// <c>tools</c> 的数据源是 SDK 注册的 <see cref="McpServerOptions.ToolCollection"/> ——
/// 与 MCP 客户端 <c>tools/list</c> 拿到的是<b>同一份事实</b>,不存在「帮助页列了但实际调不到」的可能。
/// </summary>
public static class McpToolsEndpoints
{
    public static IEndpointRouteBuilder MapMcpToolsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/mcp/tools", (IConfiguration configuration, IOptions<McpServerOptions> mcpOptions) =>
            {
                // 从 SDK 注册表读取,而不是在本文件里维护一份手写的工具清单:
                // 手写清单与真实注册表之间没有任何强制同步机制,新增工具时必然悄悄漏掉一处,
                // 而「帮助页显示的工具」与「实际可调用的工具」不一致是最难被发现的那类缺陷
                // ToolCollection 在未注册任何工具时为 null —— 返回空清单而非抛出 NullReference,
                // 让「尚未接入工具」表现为可读的空数组
                var tools = (mcpOptions.Value.ToolCollection ?? [])
                    .Select(tool => new
                    {
                        name = tool.ProtocolTool.Name,
                        description = tool.ProtocolTool.Description
                    })
                    .ToArray();

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
                    tools
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
