namespace DImage.Api.Mcp;

/// <summary>
/// MCP 协议相关常量集中定义,避免协议版本等字符串在多个端点散落。
/// 与 <see cref="Auth.AuthConstants"/> 同属「常量集中定义」原则的落地。
/// </summary>
public static class McpProtocol
{
    /// <summary>
    /// 对外展示的 MCP 协议版本,由验证端点与工具清单端点共用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>取值依据是实测,不是文档推断</b>。本服务未显式设置
    /// <c>McpServerOptions.ProtocolVersion</c>,即保持「支持全部已知版本」,
    /// 实际协商结果取决于客户端请求的版本。对 SDK 2.2.0 的受控实例逐版本实测(<c>initialize</c> 握手):
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>2024-11-05</c> / <c>2025-03-26</c> / <c>2025-06-18</c> / <c>2025-11-25</c> —— 原样回显,均受支持;</description></item>
    ///   <item><description>未知版本(如 <c>1999-01-01</c>)—— 回落为 <c>2025-11-25</c>;</description></item>
    ///   <item><description><c>2026-07-28</c> —— 被拒绝,错误码 <c>-32022</c>,提示「not available through the initialize handshake」。</description></item>
    /// </list>
    /// <para>
    /// 故本常量取 <b><c>2025-11-25</c></b>:它既是握手受支持的最高版本,也是服务端在客户端未指定
    /// 可识别版本时实际落到的版本 —— 即「一个普通客户端最终会用到的那一个」。
    /// </para>
    /// <para>
    /// <b>为什么不能写 <c>2026-07-28</c></b>:该修订属于「按请求携带元数据」的形态,
    /// 只在 <c>server/discover</c> 流程中对外声明,<b>无法经 <c>initialize</c> 握手达成</b>。
    /// 把它写成本常量,会让帮助页展示一个任何握手客户端都到不了的版本号 ——
    /// 而协议版本恰恰是排查 MCP 客户端兼容问题的第一线索,写错的代价是误导排查方向。
    /// </para>
    /// </remarks>
    public const string ProtocolVersion = "2025-11-25";
}
