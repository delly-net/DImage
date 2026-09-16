var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var app = builder.Build();

var serviceName = app.Configuration["Service:Name"] ?? "DImage.Api";
var serviceVersion = app.Configuration["Service:Version"] ?? "0.0.0";

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// 根级:服务元信息,便于人工确认服务已启动及可用接口清单
app.MapGet("/", () => Results.Ok(new
{
    service = serviceName,
    version = serviceVersion,
    endpoints = new[]
    {
        "GET /",
        "GET /health",
        "GET /api/v1/ping"
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

app.Run();
