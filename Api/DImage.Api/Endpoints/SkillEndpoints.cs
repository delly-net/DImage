using DImage.Api.Skills;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace DImage.Api.Endpoints;

/// <summary>
/// Skill 下载端点:PowerShell 安装脚本、技能内容下载,以及供浏览器管理端取用的安装命令。
/// </summary>
/// <remarks>
/// <para>
/// <b>下载端点注册在根路径(<c>/skill/...</c>)而非 <c>/api/v1</c> 下,是有意为之</b>:
/// <c>/api/v1</c> 组承载浏览器管理端接口,其端点一律要求 JWT;而技能下载由用户在**终端**执行
/// <c>irm ... | iex</c>,没有登录态可携带。根路径无组级鉴权约定,故公共下载端点挂此处天然匿名。
/// 这与本项目「后端端点鉴权择一」约定并不冲突 —— 该约定约束的是业务接口,本组是**公共静态资源**型端点。
/// </para>
/// <para>
/// <b>技能内容与脚本中不包含任何凭据</b>(<c>&lt;TOKEN&gt;</c> 一律为占位符),这是匿名开放的前提:
/// 任何人取到的只是一份描述「服务能做什么」的 Markdown。
/// </para>
/// <para>
/// <b>地址一律取配置 <c>Service:BaseUrl</c>,不按请求 <c>Host</c> 隐式推导</b>:服务经反向代理或
/// 分域部署时,请求 Host 可能是代理地址或前端域,推导出的下载地址会把用户引向错误的服务。
/// 未配置时给出可读提示而非 400/500。
/// </para>
/// </remarks>
public static class SkillEndpoints
{
    /// <summary>地址未配置时的可读提示(返回给终端用户,故为纯文本单行,不含堆栈)。</summary>
    private const string BaseUrlMissingTip =
        "# 服务对外地址(Service:BaseUrl)未配置,请联系服务管理员配置后重新获取安装命令。";

    public static IEndpointRouteBuilder MapSkillEndpoints(this IEndpointRouteBuilder app)
    {
        // —————————————— 匿名:安装脚本 ——————————————
        // 用户在终端执行 `irm {服务地址}/skill/install | iex`,拿到的就是本端点返回的脚本正文。
        // 用 Results.Text 而非 Ok:响应必须是可直接管道给 iex 的脚本原文,包进 JSON 会让 iex 执行一串 JSON。
        app.MapGet(SkillService.InstallScriptPath, (IConfiguration configuration) =>
            {
                var baseUrl = ReadBaseUrl(configuration);
                if (baseUrl.Length == 0)
                {
                    return Results.Text(BaseUrlMissingTip, "text/plain; charset=utf-8");
                }

                var script = SkillService.GenerateInstallScript(baseUrl, ReadServiceName(configuration), ReadServiceVersion(configuration));
                return Results.Text(script, "text/plain; charset=utf-8");
            })
            .WithName("GetSkillInstallScript")
            .WithTags("Skill")
            .AllowAnonymous();

        // —————————————— 匿名:技能内容(按 key 分发) ——————————————
        // 安装脚本逐个技能调用本端点下载 SKILL.md。响应必须显式带 charset=utf-8:
        // Windows PowerShell 5.1 的 Invoke-WebRequest 在 charset 缺失时按 ISO-8859-1 解码,中文技能内容会整篇乱码。
        app.MapGet($"{SkillService.SkillContentPathPrefix}/{{skill}}/content", (
                string skill,
                IConfiguration configuration,
                IOptions<McpServerOptions> mcpOptions) =>
            {
                var baseUrl = ReadBaseUrl(configuration);
                if (baseUrl.Length == 0)
                {
                    return Results.Text(BaseUrlMissingTip, "text/plain; charset=utf-8");
                }

                // 工具清单取自 SDK 注册表,与 GET /api/v1/mcp/tools、MCP 的 tools/list 是同一份事实源
                var tools = mcpOptions.Value.ToolCollection ?? [];
                var content = SkillService.TryGenerateSkillContent(
                    skill, tools, baseUrl, ReadServiceName(configuration), ReadServiceVersion(configuration));

                if (content is null)
                {
                    // 回带可用 key 清单:该端点的调用方是安装脚本与排查者,「有哪些 key 可用」比「key 错了」更有用
                    return Results.Json(
                        new
                        {
                            error = "unknown_skill",
                            availableSkills = SkillCatalog.Definitions.Select(s => s.Key).ToArray()
                        },
                        statusCode: StatusCodes.Status404NotFound);
                }

                return Results.Text(content, "text/markdown; charset=utf-8");
            })
            .WithName("GetSkillContent")
            .WithTags("Skill")
            .AllowAnonymous();

        // —————————————— 鉴权:供浏览器管理端取安装命令与技能清单 ——————————————
        var api = app.MapGroup("/api/v1");

        api.MapGet("/skills/install-command", (IConfiguration configuration) =>
            {
                var baseUrl = ReadBaseUrl(configuration);
                if (baseUrl.Length == 0)
                {
                    // 只返回错误码,中文文案由前端渲染(沿用「接口只返回结构化数据」约定);
                    // 该错误码须与前端 client.ts 的 ERROR_CODE_MESSAGES 保持一致
                    return Results.BadRequest(new { error = "skill_base_url_not_configured" });
                }

                return Results.Ok(new
                {
                    baseUrl,
                    installUrl = $"{baseUrl}{SkillService.InstallScriptPath}",
                    installCommand = SkillService.GenerateInstallCommand(baseUrl),
                    skills = SkillCatalog.Definitions
                        .Select(s => new { key = s.Key, name = s.Name, description = s.Description })
                        .ToArray()
                });
            })
            .WithName("GetSkillInstallCommand")
            .WithTags("Skill")
            // 有意使用无参 RequireAuthorization() —— 走默认认证方案(JwtBearer),而非 McpAccess 策略。
            // 本端点服务对象是浏览器管理端,前端只持有 JWT、不持有 MCP TOKEN,照搬 McpAccess 必然 401,
            // 且失败形态与「令牌过期」高度相似,排查成本极高。
            // 请勿按「MCP/Skill 相关端点统一用 McpAccess」的约定字面改回 .RequireAuthorization(AuthConstants.Policies.McpAccess)。
            .RequireAuthorization();

        return app;
    }

    /// <summary>读取并规范化服务对外根地址;未配置时返回空串,由各端点走「未配置」分支。</summary>
    private static string ReadBaseUrl(IConfiguration configuration)
        => (configuration["Service:BaseUrl"] ?? "").Trim().TrimEnd('/');

    private static string ReadServiceName(IConfiguration configuration)
        => configuration["Service:Name"] ?? "DImage.Api";

    private static string ReadServiceVersion(IConfiguration configuration)
        => configuration["Service:Version"] ?? "0.0.0";
}
