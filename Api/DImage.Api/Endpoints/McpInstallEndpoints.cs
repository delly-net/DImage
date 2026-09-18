using DImage.Api.Options;
using DImage.Api.Skills;

namespace DImage.Api.Endpoints;

/// <summary>
/// MCP 安装端点:把本服务的一条命令接入方式(<c>irm .../mcp/install | iex</c>)与脚本正文提供给用户终端。
/// </summary>
/// <remarks>
/// <para>
/// <b>与 <see cref="SkillEndpoints"/> 的分工</b>:那边管「装技能」(写 <c>.claude\skills\</c>),这边管「接 MCP」
/// (写 <c>.mcp.json</c>)。二者同为终端侧、同为匿名、同样以 <c>irm | iex</c> 消费,故注册位置与鉴权形态刻意保持一致。
/// </para>
/// <para>
/// <b>端点挂根路径 <c>/mcp/install</c>,与 MCP 协议端点 <c>/mcp</c> 相邻</b>:选择相邻是有意的 ——
/// 用户在 <c>.mcp.json</c> 里填的 <c>url</c> 是 <c>{根地址}/mcp</c>,而装这个配置的命令是 <c>{根地址}/mcp/install</c>,
/// 两个地址放在一起才容易被记住、被讲清楚。代价是存在路由遮蔽风险:<c>MapMcp</c> 注册的是 <c>/mcp</c> 及其子路径,
/// 若它把 <c>/mcp/install</c> 也吃掉,本端点将永远不命中。**该风险须经实测确认**(验收项 A6:同一实例上
/// <c>GET /mcp/install</c> 返回脚本、<c>POST /mcp</c> 的 <c>tools/list</c> 正常返回工具清单)。
/// 若实测出现遮蔽,回退方案是把路径常量改为 <c>/mcp-install</c> 并同步 <see cref="SkillService.McpInstallPath"/> 的全部引用 ——
/// 路径集中在常量里定义,正是为了让这种回退只改一处。
/// </para>
/// <para>
/// <b>与技能安装端点一样不做任何鉴权</b>,理由见 <see cref="SkillEndpoints"/> 的类注释:响应体是一份不含真实凭据的脚本
/// (TOKEN 由用户本机的环境变量或交互输入提供),匿名开放不泄露任何东西。
/// </para>
/// </remarks>
public static class McpInstallEndpoints
{
    public static IEndpointRouteBuilder MapMcpInstallEndpoints(this IEndpointRouteBuilder app)
    {
        // 用户在终端执行 `irm {服务地址}/mcp/install | iex`,拿到的就是本端点返回的脚本正文。
        // 用 Results.Text 而非 Ok:响应必须能直接管道给 iex,包进 JSON 会让 iex 去执行一串 JSON。
        app.MapGet(SkillService.McpInstallPath, (IConfiguration configuration) =>
            {
                var baseUrl = ServiceAddress.ReadBaseUrl(configuration);
                if (baseUrl.Length == 0)
                {
                    // 未配置对外地址时,若返回脚本会让用户在 .mcp.json 里写入一个不存在的地址,
                    // 失败点从「服务端少配一个变量」挪到「客户端连不上」,排查代价高得多。
                    // 故此处返回一行以 # 开头的提示文本:用户误把它当脚本体执行时,也只是一句注释,不产生语法错误。
                    return Results.Text(ServiceAddress.MissingBaseUrlTip, "text/plain; charset=utf-8");
                }

                var script = SkillService.GenerateMcpInstallScript(
                    baseUrl, ServiceAddress.ReadServiceName(configuration), ServiceAddress.ReadServiceVersion(configuration));

                // charset=utf-8 必须显式带上:PowerShell 5.1 的 Invoke-WebRequest 在 charset 缺失时按
                // ISO-8859-1 解码,脚本里的中文注释与提示会整篇乱码(脚本仍可执行,但用户读到的是乱码)
                return Results.Text(script, "text/plain; charset=utf-8");
            })
            .WithName("GetMcpInstallScript")
            .WithTags("Mcp")
            .AllowAnonymous();

        return app;
    }
}
