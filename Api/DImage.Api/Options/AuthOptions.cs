namespace DImage.Api.Options;

/// <summary>
/// 认证授权配置节 <c>Auth</c>。
/// 三个凭据值(passwords/token/secret)在 appsettings.json 中一律留空,实际值由环境变量提供。
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>换取 JWT 的密码,对应环境变量 <c>PASSWORD</c>。</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>MCP 服务授权凭据,对应环境变量 <c>TOKEN</c>。</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>JWT 签名密钥,对应环境变量 <c>JWT_SECRET</c>。</summary>
    public string JwtSecret { get; set; } = string.Empty;

    /// <summary>
    /// JWT 有效期(天)。默认值必须落在属性初始值上:配置节缺失时仍为 7,
    /// 否则会签发出「签发即过期」的令牌。
    /// </summary>
    public int JwtExpireDays { get; set; } = 7;

    /// <summary>JWT 签发者(iss)。</summary>
    public string Issuer { get; set; } = "dimage-api";

    /// <summary>JWT 受众(aud)。</summary>
    public string Audience { get; set; } = "dimage-client";
}
