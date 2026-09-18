namespace DImage.Api.Options;

/// <summary>
/// 服务对外地址与标识的解析:「服务对外根地址」的唯一事实源。
/// </summary>
/// <remarks>
/// <para>
/// <b>读取优先级:<c>API_BASE_URL</c>(环境变量)→ <c>Service:BaseUrl</c>(配置)→ 空串。</b>
/// 环境变量优先是刻意选择:部署期只需设置环境变量即可把对外地址切到真实域名,
/// <b>无须改动随仓库提交的 <c>appsettings.json</c></b> —— 后者里的 <c>localhost</c> 值只应作为本地开发的兜底。
/// </para>
/// <para>
/// 与 <see cref="Auth.CredentialProvider"/> 的形态保持一致:先读 <c>IConfiguration[变量名]</c>
/// (经环境变量 provider 生效,同时兼容命令行参数与用户机密注入),再回落 <see cref="Environment.GetEnvironmentVariable(string)"/>。
/// 两条通道都试,是为了让「同一个变量名」在不同注入方式下都能生效。
/// </para>
/// <para>
/// <b>地址一律不按请求 <c>Host</c> 隐式推导</b>:服务经反向代理或分域部署时,请求 Host 可能是代理地址或前端域,
/// 推导出的下载地址会把用户引向错误的服务。调用方拿到的地址永远来自本类。
/// </para>
/// <para>
/// <b>未配置不是错误,而是一种状态</b>:两个来源都为空时返回空串,由端点层各自翻译 ——
/// 终端端点给单行中文注释文本(用户直接 <c>irm ... | iex</c>,抛堆栈毫无意义),
/// 管理端接口给结构化错误码。两种形态有意不同,故本类只负责解析,不负责呈现。
/// </para>
/// </remarks>
public static class ServiceAddress
{
    /// <summary>服务对外根地址的环境变量名,优先级高于 <c>Service:BaseUrl</c> 配置。</summary>
    public const string BaseUrlVariable = "API_BASE_URL";

    /// <summary>
    /// 地址未配置时的可读提示,直接返回给在终端执行安装命令的用户。
    /// </summary>
    /// <remarks>
    /// <b>文案必须同时点名两个来源</b>:只写「请配置 Service:BaseUrl」会让用户去改配置文件却改不动 ——
    /// 环境变量压着配置,配置改了也不生效。把两个来源都写出来,用户才能按自己手上有的手段解决。
    /// 以 <c>#</c> 开头是为让误把响应体当脚本执行时也只是一句注释,不产生语法错误。
    /// </remarks>
    public const string MissingBaseUrlTip =
        "# 服务对外地址未配置(请配置环境变量 API_BASE_URL,或配置项 Service:BaseUrl),请联系服务管理员配置后重新获取安装命令。";

    /// <summary>
    /// 读取并规范化服务对外根地址;两个来源均未配置时返回空串。
    /// </summary>
    /// <returns>已去除首尾空白与尾部斜杠的地址,或空串。</returns>
    public static string ReadBaseUrl(IConfiguration configuration)
    {
        var value = configuration[BaseUrlVariable] ?? Environment.GetEnvironmentVariable(BaseUrlVariable);

        if (string.IsNullOrWhiteSpace(value))
        {
            value = configuration["Service:BaseUrl"];
        }

        // 去除尾部斜杠:调用方一律以「根地址 + 绝对路径」拼接,残留斜杠会产生 //mcp、//skill 这类双斜杠地址
        return Normalize(value);
    }

    /// <summary>读取服务名;未配置时回落到程序集名。</summary>
    public static string ReadServiceName(IConfiguration configuration)
        => configuration["Service:Name"] ?? "DImage.Api";

    /// <summary>读取服务版本;未配置时回落为 <c>0.0.0</c>。</summary>
    public static string ReadServiceVersion(IConfiguration configuration)
        => configuration["Service:Version"] ?? "0.0.0";

    /// <summary>去除首尾空白与尾部斜杠,使拼接路径时不会出现双斜杠。</summary>
    public static string Normalize(string? url) => (url ?? "").Trim().TrimEnd('/');
}
