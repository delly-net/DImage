using DImage.Api.Auth;
using DImage.Api.Endpoints;
using DImage.Api.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// 认证配置:三个凭据值在 appsettings.json 中留空,实际值由环境变量提供
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

builder.Services.AddSingleton<CredentialProvider>();
builder.Services.AddSingleton<JwtTokenService>();

// 两套方案并存:默认 JwtBearer(供后续业务接口),具名 McpToken(供 MCP 端点),二者互不干扰
builder.Services
    .AddAuthentication(AuthConstants.Schemes.Jwt)
    .AddJwtBearer(AuthConstants.Schemes.Jwt, _ => { })
    .AddScheme<AuthenticationSchemeOptions, McpTokenAuthenticationHandler>(AuthConstants.Schemes.McpToken, null);

// JwtBearer 的签名密钥由 CredentialProvider 单例提供,故经 DI 配置而非在 AddJwtBearer 委托内读取配置
builder.Services
    .AddOptions<JwtBearerOptions>(AuthConstants.Schemes.Jwt)
    .Configure<CredentialProvider, IOptions<AuthOptions>>(
        (options, credentials, authOptions) =>
        {
            var auth = authOptions.Value;

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = auth.Issuer,
                ValidateAudience = true,
                ValidAudience = auth.Audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(credentials.JwtSigningKey),
                ClockSkew = TimeSpan.Zero
            };
        });

builder.Services.AddAuthorization(options =>
{
    // 策略绑定 McpToken 方案:否则会落到默认的 JwtBearer 方案上,TOKEN 永远验不过
    options.AddPolicy(AuthConstants.Policies.McpAccess, policy =>
    {
        policy.AddAuthenticationSchemes(AuthConstants.Schemes.McpToken);
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(AuthConstants.Claims.AuthScheme, AuthConstants.Claims.AuthSchemeMcpToken);
    });
});

var app = builder.Build();

// 启动时即刻解析凭据:凭据是单例,若无人请求则不会实例化,
// 「未配置则生成随机值并告警」就会推迟到首次请求才出现在日志里,与计划的「启动时一次性解析」不符
app.Services.GetRequiredService<CredentialProvider>();

var serviceName = app.Configuration["Service:Name"] ?? "DImage.Api";
var serviceVersion = app.Configuration["Service:Version"] ?? "0.0.0";

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// 必须显式调用且位于端点映射之前,漏掉时 McpToken 方案永不执行
app.UseAuthentication();
app.UseAuthorization();

// 根级:服务元信息,便于人工确认服务已启动及可用接口清单
app.MapGet("/", () => Results.Ok(new
{
    service = serviceName,
    version = serviceVersion,
    endpoints = new[]
    {
        "GET /",
        "GET /health",
        "GET /health/mcp",
        "GET /api/v1/ping",
        "POST /api/v1/auth/token",
        "POST /api/v1/mcp/verify"
    }
}))
.WithName("GetServiceInfo")
.WithTags("Service");

// 根级:健康检查,供运维探针使用,不占版本位
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = serviceName,
    version = serviceVersion,
    timestamp = DateTimeOffset.UtcNow
}))
.WithName("GetHealth")
.WithTags("Service");

// 业务接口统一挂 /api/v1 前缀,预留版本位
var api = app.MapGroup("/api/v1");

api.MapGet("/ping", () => Results.Ok(new { message = "pong" }))
   .WithName("Ping")
   .WithTags("Api");

app.MapAuthEndpoints();
app.MapMcpEndpoints();

app.Run();
