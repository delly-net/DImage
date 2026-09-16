namespace DImage.Api.Auth;

/// <summary>
/// 认证方案名、授权策略名与自定义 Claim 名的集中定义,避免字符串散落。
/// </summary>
public static class AuthConstants
{
    /// <summary>认证方案名。</summary>
    public static class Schemes
    {
        /// <summary>默认方案:JWT Bearer,供后续业务接口使用。</summary>
        public const string Jwt = "Bearer";

        /// <summary>具名方案:MCP 静态 TOKEN 鉴权。</summary>
        public const string McpToken = "McpToken";
    }

    /// <summary>授权策略名。</summary>
    public static class Policies
    {
        /// <summary>MCP 访问策略:已认证且经 McpToken 方案签发。</summary>
        public const string McpAccess = "McpAccess";
    }

    /// <summary>自定义 Claim 名。</summary>
    public static class Claims
    {
        /// <summary>标记该身份由哪个方案签发。</summary>
        public const string AuthScheme = "auth_scheme";

        /// <summary>McpToken 方案的身份标记值。</summary>
        public const string AuthSchemeMcpToken = "mcp_token";
    }
}
