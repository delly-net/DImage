using DImage.Api.Auth;
using DImage.Api.Endpoints;
using DImage.Api.Hosting;
using DImage.Api.Imaging;
using DImage.Api.Mcp;
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

// 内存图像注册表:上限从配置读取,缺省值一律回落到 Imaging 层定义的常量 ——
// 「默认值写在 C# 初始值上」是硬约定,配置文件缺失或键名写错时上限必须依然存在,
// 否则一次部署疏漏就会让 /mcp 变成一个没有上限的内存放大器
var registrySection = builder.Configuration.GetSection("ImageRegistry");
var registryLimits = new ImageRegistryLimits(
    MaxImageCount: registrySection.GetValue<int?>("MaxImageCount") ?? ImageRegistryLimits.DefaultMaxImageCount,
    MaxTotalBytes: registrySection.GetValue<long?>("MaxTotalBytes") ?? ImageRegistryLimits.DefaultMaxTotalBytes,
    IdleTtl: registrySection.GetValue<TimeSpan?>("IdleTtl") ?? ImageRegistryLimits.DefaultIdleTtl);

// ImageRegistryLimits 是 readonly record struct,不能作为 AddSingleton<TService> 的类型参数
// (该重载要求 TService : class),故经工厂注册 —— 顺带把上限的构造时机固定在启动期
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton(sp => new ImageBufferStore(registryLimits, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddHostedService<ImageBufferReaper>();

// MCP 服务端:默认无状态 Streamable HTTP 传输。
// 刻意不启用 EnableLegacySse —— 那是一条仅用于兼容旧客户端的废弃路径,
// 开启等于把一个额外的公网入口挂在 /mcp 上,而本项目没有任何客户端依赖它。
//
// 工具类型在此逐个登记:ToolCollection 是工具清单的唯一事实源,
// GET /api/v1/mcp/tools 与 /mcp 的 tools/list 都从它派生,故新增工具只需加一行 WithTools<T>,
// 前端与帮助页无需任何改动
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<ImageTools>()
    .WithTools<ShapeTools>();

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
        "POST /api/v1/mcp/verify",
        "GET /api/v1/mcp/tools",
        "GET /api/v1/skills/install-command",
        // Skill 下载端点挂在根路径(匿名),见 SkillEndpoints 的类注释
        "GET /skill/install",
        "GET /skill/install/{skill}/content",
        "GET /skill/install/{skill}/files/{file}",
        // MCP 客户端接入配置的安装脚本,与 Skill 安装并列暴露在根路径(匿名)
        "GET /mcp/install",
        // 该清单无自动生成机制,新增端点必须手工同步,否则服务元信息与实际接口清单不一致
        "POST /mcp"
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
app.MapMcpToolsEndpoints();
app.MapSkillEndpoints();
app.MapMcpInstallEndpoints();

// MCP 协议端点(Streamable HTTP)。授权策略必须显式指定 McpAccess:
// 不写的话会落到默认的 JwtBearer 方案上,而 MCP 客户端持有的是静态 TOKEN,
// 表现为「TOKEN 正确却始终 401」。MapMcp 返回 IEndpointConventionBuilder,故可链式挂策略。
// 路径取 McpProtocol.EndpointPath:同一路径还被 .mcp.json 配置片段与安装脚本写出,四处必须同源
app.MapMcp(McpProtocol.EndpointPath).RequireAuthorization(AuthConstants.Policies.McpAccess);

app.Run();
