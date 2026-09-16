using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using DImage.Api.Options;
using Microsoft.Extensions.Options;

namespace DImage.Api.Auth;

/// <summary>
/// 启动时一次性解析三份凭据,解析顺序:<c>Auth:{Name}</c> → 环境变量 <c>{NAME}</c> → 随机值兜底。
/// 全部缺失时生成随机凭据并以 Warning 输出到控制台,保证本地零配置可跑通。
/// 凭据值不对外暴露,仅通过 <see cref="VerifyPassword"/> / <see cref="VerifyToken"/> 做定长比对。
/// </summary>
public sealed class CredentialProvider
{
    /// <summary>随机凭据的字节长度,Base64Url 编码后为 43 字符。</summary>
    private const int CredentialByteLength = 32;

    /// <summary>HMAC-SHA256 期望的最小密钥长度(字节)。</summary>
    private const int MinJwtSecretBytes = 32;

    private const string PasswordVariable = "PASSWORD";
    private const string TokenVariable = "TOKEN";
    private const string JwtSecretVariable = "JWT_SECRET";

    private readonly IConfiguration _configuration;
    private readonly ILogger<CredentialProvider> _logger;
    private readonly string _password;
    private readonly string _token;

    public CredentialProvider(
        IOptions<AuthOptions> options,
        IConfiguration configuration,
        ILogger<CredentialProvider> logger)
    {
        _configuration = configuration;
        _logger = logger;

        var auth = options.Value;

        _password = Resolve(nameof(AuthOptions.Password), PasswordVariable, auth.Password, out var passwordFromConfig);
        _token = Resolve(nameof(AuthOptions.Token), TokenVariable, auth.Token, out var tokenFromConfig);

        var jwtSecret = Resolve(nameof(AuthOptions.JwtSecret), JwtSecretVariable, auth.JwtSecret, out var secretFromConfig);

        PasswordConfigured = passwordFromConfig;
        TokenConfigured = tokenFromConfig;
        JwtSecretConfigured = secretFromConfig;

        JwtSigningKey = DeriveSigningKey(jwtSecret);
    }

    /// <summary>密码是否来自显式配置(环境变量或 appsettings),false 表示用的随机兜底值。</summary>
    public bool PasswordConfigured { get; }

    /// <summary>TOKEN 是否来自显式配置(环境变量或 appsettings),false 表示用的随机兜底值。</summary>
    public bool TokenConfigured { get; }

    /// <summary>JWT 密钥是否来自显式配置。</summary>
    public bool JwtSecretConfigured { get; }

    /// <summary>JWT 签名密钥(HMAC-SHA256 用,始终为定长 32 字节)。</summary>
    public byte[] JwtSigningKey { get; }

    /// <summary>定长比对密码。</summary>
    public bool VerifyPassword(string? candidate) => FixedTimeEquals(_password, candidate);

    /// <summary>定长比对 MCP TOKEN。</summary>
    public bool VerifyToken(string? candidate) => FixedTimeEquals(_token, candidate);

    /// <summary>
    /// 定长比对:双方先经 SHA-256 压成 32 字节定长,再交给
    /// <see cref="CryptographicOperations.FixedTimeEquals"/> 比较,
    /// 同时消除长度侧信道与逐字节比较的时间差。
    /// </summary>
    private static bool FixedTimeEquals(string expected, string? candidate)
    {
        if (candidate is null)
        {
            return false;
        }

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));

        return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }

    /// <summary>
    /// 密钥短于 32 字节时不硬失败,而是按 SHA-256 派生出定长密钥并提示。
    /// </summary>
    private byte[] DeriveSigningKey(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);

        if (bytes.Length >= MinJwtSecretBytes)
        {
            return bytes;
        }

        _logger.LogWarning(
            "{Variable} 长度不足 {Length} 字节(建议 ≥ {Min} 字节),已按 SHA-256 派生定长密钥;建议直接配置 ≥{Min} 字节随机密钥。",
            JwtSecretVariable, bytes.Length, MinJwtSecretBytes, MinJwtSecretBytes);

        return SHA256.HashData(bytes);
    }

    private string Resolve(string sectionKey, string variable, string configured, out bool fromConfig)
    {
        var value = _configuration[$"{AuthOptions.SectionName}:{sectionKey}"];

        if (string.IsNullOrWhiteSpace(value))
        {
            value = _configuration[variable] ?? Environment.GetEnvironmentVariable(variable);
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            fromConfig = true;
            return value;
        }

        fromConfig = false;

        var generated = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(CredentialByteLength));

        _logger.LogWarning(
            "未配置环境变量 {Variable},已生成本次进程有效的随机凭据:{Generated}。" +
            "请设置环境变量 {VariableName} 以固定凭据,否则重启后当前值失效。",
            variable, generated, variable);

        return generated;
    }
}
