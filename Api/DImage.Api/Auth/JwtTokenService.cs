using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using DImage.Api.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DImage.Api.Auth;

/// <summary>
/// JWT 签发服务:HS256 签名,有效期取 <c>Auth:JwtExpireDays</c>(默认 7 天)。
/// </summary>
public sealed class JwtTokenService
{
    private readonly CredentialProvider _credentials;
    private readonly AuthOptions _options;

    public JwtTokenService(CredentialProvider credentials, IOptions<AuthOptions> options)
    {
        _credentials = credentials;
        _options = options.Value;
    }

    /// <summary>JWT 有效期天数,供接口计算 <c>expiresIn</c>。</summary>
    public int ExpireDays => _options.JwtExpireDays;

    /// <summary>签发令牌,返回令牌原文与过期时刻。</summary>
    public (string Token, DateTimeOffset ExpiresAt) CreateToken()
    {
        var issuedAt = DateTimeOffset.UtcNow;
        var expiresAt = issuedAt.AddDays(_options.JwtExpireDays);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "dimage"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat, issuedAt.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new(JwtRegisteredClaimNames.Exp, expiresAt.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };

        var signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(_credentials.JwtSigningKey),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: issuedAt.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: signingCredentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
