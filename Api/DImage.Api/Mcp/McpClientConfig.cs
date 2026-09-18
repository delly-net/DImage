using DImage.Api.Options;

namespace DImage.Api.Mcp;

/// <summary>
/// MCP 客户端接入配置(<c>.mcp.json</c>)的形状定义:服务 key、服务端条目与整份配置文本。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类是 <c>.mcp.json</c> 形状的唯一事实源</b>,有两个消费方:
/// </para>
/// <list type="number">
///   <item><description>技能内容(<c>dimage-on</c> 的 <c>SKILL.md</c>)中展示的客户端配置片段;</description></item>
///   <item><description>MCP 安装脚本生成的目标配置(用户执行 <c>irm .../mcp/install | iex</c> 后落盘的内容)。</description></item>
/// </list>
/// <para>
/// 二者若各写一份,必然出现「技能里教的配置」与「脚本实际写出的配置」逐渐分叉 ——
/// 而两者的差别只在 <c>headers</c> 少一行这类地方时,用户按哪个排查都会绕远路。
/// </para>
/// <para>
/// <b>纯值生成,刻意不做 DI 注入</b>:不注入 <c>IOptions&lt;T&gt;</c>、不读 <c>IConfiguration</c>,
/// 地址由调用方传入(沿 <see cref="Skills.SkillService"/> 与 <c>Imaging/</c> 的「只吃纯值参数」风格)。
/// </para>
/// </remarks>
public static class McpClientConfig
{
    /// <summary>客户端配置中本服务的 key(<c>mcpServers</c> 下的条目名)。</summary>
    public const string ServerKey = "dimage";

    /// <summary>传输类型,固定为 Streamable HTTP。</summary>
    public const string TransportType = "http";

    /// <summary>
    /// 生成 <c>.mcp.json</c> 全文(含未展开的 <c>&lt;TOKEN&gt;</c> 占位符)。
    /// </summary>
    /// <param name="baseUrl">服务对外根地址,允许带尾部斜杠。</param>
    /// <returns>以 LF 结尾的 JSON 文本,可直接展示或作为脚本的目标配置。</returns>
    /// <remarks>
    /// <b>凭据一律以占位符呈现</b>:本方法产出的文本会出现在技能内容与帮助页上,
    /// 任何真实值写进来都等于把凭据印在公开页面。真实 TOKEN 只在用户本机由安装脚本注入。
    /// </remarks>
    public static string BuildConfigJson(string baseUrl)
    {
        var root = ServiceAddress.Normalize(baseUrl);

        return string.Join('\n',
        [
            "{",
            "  \"mcpServers\": {",
            $"    \"{ServerKey}\": {{",
            $"      \"type\": \"{TransportType}\",",
            $"      \"url\": \"{root}{McpProtocol.EndpointPath}\",",
            "      \"headers\": { \"Authorization\": \"Bearer <TOKEN>\" }",
            "    }",
            "  }",
            "}",
            "",
        ]);
    }
}
