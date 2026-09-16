namespace DImage.Api.Mcp;

/// <summary>
/// MCP 协议相关常量集中定义,避免协议版本等字符串在多个端点散落。
/// 与 <see cref="Auth.AuthConstants"/> 同属「常量集中定义」原则的落地。
/// </summary>
public static class McpProtocol
{
    /// <summary>MCP 协议版本,由验证端点与工具清单端点共用。</summary>
    public const string ProtocolVersion = "2025-06-18";
}
