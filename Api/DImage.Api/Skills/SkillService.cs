using System.Text;
using System.Text.Json;
using DImage.Api.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DImage.Api.Skills;

/// <summary>
/// Skill 文本生成服务:安装命令、PowerShell 安装脚本与各技能的 <c>SKILL.md</c> 内容。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类型是纯函数集合,刻意不做 DI 注入</b>:不注入 <c>IOptions&lt;T&gt;</c>、不读 <c>IConfiguration</c>,
/// 地址、服务名与版本一律由调用方(端点层)以纯值传入。理由与 <c>Imaging/</c> 的「只吃纯值参数」同源 ——
/// 生成结果是「输入相同则字节相同」的确定性文本,配置读取集中在端点层后,同一份文本才可能被断言逐字节比对。
/// </para>
/// <para>
/// <b>技能内容的工具清单取自 <see cref="McpServerTool"/> 集合</b>(由端点从
/// <c>McpServerOptions.ToolCollection</c> 传入),与 <c>GET /api/v1/mcp/tools</c>、MCP 的 <c>tools/list</c>
/// 是<b>同一份事实源</b>;本文件内不得出现任何手写的工具名或参数名清单。
/// </para>
/// </remarks>
public static class SkillService
{
    /// <summary>安装脚本的下载路径(根路径,匿名访问)。</summary>
    public const string InstallScriptPath = "/skill/install";

    /// <summary>技能内容的下载路径前缀(根路径,匿名访问),后接 <c>{skillKey}/content</c>。</summary>
    public const string SkillContentPathPrefix = "/skill/install";

    /// <summary>
    /// 生成用户复制到终端执行的安装命令(PowerShell)。
    /// </summary>
    /// <param name="baseUrl">服务对外根地址(<c>Service:BaseUrl</c>),允许带尾部斜杠。</param>
    /// <remarks>
    /// <c>irm</c> 是 <c>Invoke-RestMethod</c> 的别名;对 <c>text/plain</c> 响应它返回响应体字符串,
    /// 管道给 <c>iex</c>(<c>Invoke-Expression</c>)即在本会话中执行该脚本 —— 故脚本里的 <c>$PWD</c>
    /// 是<b>用户执行命令时所在的目录</b>,技能因此装到用户当前项目而非全局目录。
    /// </remarks>
    public static string GenerateInstallCommand(string baseUrl)
        => $"irm {NormalizeBaseUrl(baseUrl)}{InstallScriptPath} | iex";

    /// <summary>
    /// 生成 PowerShell 安装脚本:循环下载清单中每个技能的 <c>SKILL.md</c>,写入执行命令所在目录的
    /// <c>.claude\skills\{技能名}\</c>(UTF-8)。
    /// </summary>
    /// <param name="baseUrl">服务对外根地址(<c>Service:BaseUrl</c>)。</param>
    /// <param name="serviceName">服务名,仅用于脚本注释与结束提示。</param>
    /// <param name="serviceVersion">服务版本,仅用于脚本注释与结束提示。</param>
    /// <remarks>
    /// <para>
    /// <b>脚本必须与 PowerShell 5.1(Windows 内置)和 PowerShell 7 同时兼容</b>:只使用两版共有的语法
    /// (此处 <c>-UseBasicParsing</c> 在 7 中是被接受并忽略的兼容开关,在 5.1 中则用于绕开 IE 引擎依赖)。
    /// </para>
    /// <para>
    /// <b>落盘编码固定为「UTF-8 无 BOM」</b>:<c>[System.Text.Encoding]::UTF8</c> 会写入 BOM,
    /// 而 BOM 会落在 <c>SKILL.md</c> 的 YAML frontmatter 之前,使文件首个字符不再是 <c>---</c>。
    /// 故显式使用 <c>UTF8Encoding($false)</c>。
    /// </para>
    /// </remarks>
    public static string GenerateInstallScript(string baseUrl, string serviceName, string serviceVersion)
    {
        var root = NormalizeBaseUrl(baseUrl);

        // 以 , 分隔相邻元素、末项不带尾逗号:PowerShell 的 @(...) 内不允许尾逗号,否则解析报错
        var skillLines = string.Join(",\n", SkillCatalog.Definitions.Select(s =>
            $"    @{{ Name = \"{EscapePowerShell(s.Name)}\"; "
            + $"Key = \"{EscapePowerShell(s.Key)}\"; "
            + $"Desc = \"{EscapePowerShell(s.Description)}\" }}"));

        var script = $$"""
            # 小D图像 Skill 下载安装脚本(由服务端动态生成,请勿手工修改)
            # 服务:{{serviceName}} {{serviceVersion}}
            # 用法:irm {{root}}{{InstallScriptPath}} | iex

            $baseUrl = "{{EscapePowerShell(root)}}"
            $skills = @(
            {{skillLines}}
            )

            foreach ($s in $skills) {
                # 安装到执行命令所在目录(项目当前目录)的 .claude\skills\{技能名},而非全局用户目录
                $targetDir = Join-Path $PWD ".claude\skills\$($s.Name)"
                if (-not (Test-Path $targetDir)) {
                    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
                }

                # 技能内容按 key 分发下载;响应带 charset=utf-8,中文不会乱码
                $contentUrl = "$baseUrl{{SkillContentPathPrefix}}/$($s.Key)/content"
                $content = (Invoke-WebRequest -Uri $contentUrl -UseBasicParsing).Content

                # 显式以 UTF-8 无 BOM 落盘:Encoding.UTF8 会写 BOM,污染 SKILL.md 的 frontmatter
                $skillFile = Join-Path $targetDir "SKILL.md"
                [System.IO.File]::WriteAllText($skillFile, $content, (New-Object System.Text.UTF8Encoding($false)))

                Write-Host "  已安装:$($s.Name) - $($s.Desc)"
            }

            Write-Host ""
            Write-Host "[小D图像] Skill 安装完成" -ForegroundColor Green
            Write-Host "  服务:$baseUrl"
            Write-Host "  位置:$PWD\.claude\skills"
            Write-Host ""
            """;

        return ToLf(script);
    }

    /// <summary>
    /// 按技能 key 生成对应的 <c>SKILL.md</c> 内容。
    /// </summary>
    /// <param name="skillKey">技能 key(取自 <see cref="SkillCatalog.Definitions"/>)。</param>
    /// <param name="tools">当前服务注册的全部 MCP 工具(取自 <c>McpServerOptions.ToolCollection</c>)。</param>
    /// <param name="baseUrl">服务对外根地址(<c>Service:BaseUrl</c>)。</param>
    /// <param name="serviceName">服务名。</param>
    /// <param name="serviceVersion">服务版本。</param>
    /// <returns>技能内容;key 未知时返回 <c>null</c>(由端点层翻译为 404)。</returns>
    public static string? TryGenerateSkillContent(
        string skillKey,
        IEnumerable<McpServerTool> tools,
        string baseUrl,
        string serviceName,
        string serviceVersion)
    {
        return skillKey switch
        {
            SkillCatalog.DimageOnKey => GenerateDimageOnSkillContent(tools, baseUrl, serviceName, serviceVersion),
            _ => null,
        };
    }

    /// <summary>
    /// 把文本的行尾统一为 LF。
    /// </summary>
    /// <remarks>
    /// 内容是逐行拼出来的,而 <see cref="StringBuilder.AppendLine()"/> 用的是 <see cref="Environment.NewLine"/> ——
    /// 同一份技能在 Windows 上生成 CRLF、在 Linux 上生成 LF,部署平台一换,下载到的文件字节就变了。
    /// 技能内容是纯文本资源,统一 LF 后「同版本同内容」在任何平台上都成立(Windows PowerShell 5.1/7 执行 LF 脚本亦实测正常)。
    /// </remarks>
    private static string ToLf(string text) => text.Replace("\r\n", "\n");

    /// <summary>
    /// 生成「小D图像 MCP 能力总览」(dimage-on)的技能内容。
    /// </summary>
    /// <remarks>
    /// 技能的唯一职责是<b>把服务能力输出到对话上下文</b>:内容即数据源,技能执行时不调用任何工具、
    /// 不读任何文件。工具清单、参数表与副作用标注全部由 <paramref name="tools"/> 派生,
    /// 且<b>不含生成时间戳</b> —— 同一服务版本必须产出逐字节相同的文本,否则「下载内容与接口返回一致」
    /// 这类断言将失去意义。
    /// </remarks>
    public static string GenerateDimageOnSkillContent(
        IEnumerable<McpServerTool> tools,
        string baseUrl,
        string serviceName,
        string serviceVersion)
    {
        var root = NormalizeBaseUrl(baseUrl);
        var protocolTools = tools.Select(t => t.ProtocolTool).ToArray();

        var sb = new StringBuilder();

        // —————————————— frontmatter(与既有下载技能同构) ——————————————
        sb.AppendLine("---");
        sb.AppendLine($"name: {SkillCatalog.DimageOnName}");
        sb.AppendLine($"description: {SkillCatalog.DimageOnDescription}(服务 {serviceName} {serviceVersion})");
        sb.AppendLine("effort: low");
        sb.AppendLine("user-invocable: true");
        sb.AppendLine("disable-model-invocation: false");
        sb.AppendLine("agent: general-purpose");
        sb.AppendLine("---");
        sb.AppendLine();

        sb.AppendLine("# 小D图像 MCP 能力总览");
        sb.AppendLine();
        sb.AppendLine("此技能把 DImage 服务当前提供的**全部图像处理 MCP 能力**一次性输出到对话上下文,");
        sb.AppendLine("便于确认「这台服务现在能画什么、每项能力怎么调、参数怎么填」。");
        sb.AppendLine();

        sb.AppendLine("## 使用方法");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine($"/{SkillCatalog.DimageOnName}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("或在聊天中输入「输出图像能力清单」触发。");
        sb.AppendLine();

        sb.AppendLine("## 执行要求(务必照做)");
        sb.AppendLine();
        sb.AppendLine("1. **本技能内容即数据源**:不要调用任何 MCP 工具、不要读取本地文件、不要联网检索 ——");
        sb.AppendLine("   下文「服务接入」「能力清单」「典型工作流」「注意事项」四节就是需要输出的全部内容。");
        sb.AppendLine("2. **原样完整输出**上述四节:逐项列出每个工具,不得只列工具名、不得省略参数行、不得改写参数名。");
        sb.AppendLine($"3. 输出后补一句说明:本清单是**技能下载时的快照**(服务 {serviceName} {serviceVersion},"
            + $"共 {protocolTools.Length} 项工具);服务新增工具后需重新下载本技能才会同步。");
        sb.AppendLine();

        // —————————————— 服务接入 ——————————————
        sb.AppendLine("## 服务接入");
        sb.AppendLine();
        sb.AppendLine("| 项 | 值 |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| 服务 | {Inline(serviceName)} {Inline(serviceVersion)} |");
        sb.AppendLine($"| MCP 端点 | `{root}/mcp` |");
        sb.AppendLine("| 传输方式 | Streamable HTTP(无状态,不返回 Mcp-Session-Id) |");
        sb.AppendLine("| 鉴权 | 请求头 `Authorization: Bearer <TOKEN>`(静态凭据,无过期时间) |");
        sb.AppendLine($"| 协议版本 | {McpProtocol.ProtocolVersion} |");
        sb.AppendLine();
        sb.AppendLine("客户端接入配置(`.mcp.json`,置于项目根目录):");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"mcpServers\": {");
        sb.AppendLine("    \"dimage\": {");
        sb.AppendLine("      \"type\": \"http\",");
        sb.AppendLine($"      \"url\": \"{root}/mcp\",");
        sb.AppendLine("      \"headers\": { \"Authorization\": \"Bearer <TOKEN>\" }");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("> TOKEN 由部署方经环境变量提供,请向服务管理员索取;技能内容与脚本中**不包含**任何真实凭据。");
        sb.AppendLine();

        // —————————————— 能力清单(逐工具派生,无手写清单) ——————————————
        sb.AppendLine($"## 能力清单(共 {protocolTools.Length} 项)");
        sb.AppendLine();

        if (protocolTools.Length == 0)
        {
            sb.AppendLine("当前服务未发布任何 MCP 工具。");
            sb.AppendLine();
        }

        for (int i = 0; i < protocolTools.Length; i++)
        {
            Tool tool = protocolTools[i];
            string title = string.IsNullOrWhiteSpace(tool.Title) ? "" : $" — {Inline(tool.Title)}";

            sb.AppendLine($"### {i + 1}. `{Inline(tool.Name)}`{title}");
            sb.AppendLine();
            sb.AppendLine($"- 说明:{Inline(tool.Description)}");
            sb.AppendLine($"- 副作用:{DescribeAnnotations(tool.Annotations)}");
            sb.AppendLine("- 参数:");
            sb.AppendLine();
            AppendParameterTable(sb, tool);
            sb.AppendLine();
        }

        // —————————————— 典型工作流 ——————————————
        sb.AppendLine("## 典型工作流");
        sb.AppendLine();
        sb.AppendLine("1. `image_create` 创建画布,拿到 `id`;");
        sb.AppendLine("2. 用 `image_draw_*` 绘制形状(SVG 路径、多边形、椭圆、矩形、折线),或 `image_set_pixels` 逐点写像素;");
        sb.AppendLine("3. `image_download_png` 取回 PNG(以图像内容块返回,不提供下载链接);");
        sb.AppendLine("4. `image_release` 释放 `id`。");
        sb.AppendLine();

        // —————————————— 注意事项 ——————————————
        sb.AppendLine("## 注意事项");
        sb.AppendLine();
        sb.AppendLine("- **绘制工具非幂等**:抗锯齿下重复绘制同一形状会加深半覆盖像素,不要以「重试」的方式重画。");
        sb.AppendLine("- **越界语义两种并存**:`image_set_pixels` 任一点越界即整批拒绝(零写入);"
            + "`image_draw_*` 的坐标若为有限值且量级合法但落在画布外,则裁剪到画布内正常绘制并返回 `ok:true`。");
        sb.AppendLine("- **失败以结构化结果返回**:`{ ok:false, code, message }`,常见 `code`:"
            + "`invalid_dimension` / `invalid_format` / `invalid_color` / `image_not_found` / "
            + "`pixel_out_of_range` / `capacity_exceeded` / `invalid_geometry` / `invalid_stroke_width` / "
            + "`invalid_fill_rule` / `invalid_path` / `limit_exceeded`。");
        sb.AppendLine("- **颜色格式**:`#RRGGBB` 或 `#RRGGBBAA`(大小写不敏感,必须带 `#`)。");
        sb.AppendLine("- **角度约定**:椭圆的 `start_angle` / `end_angle` 以**度**为单位,`0°` 指向 `+x` 轴,"
            + "角度增大方向为**顺时针**(与屏幕坐标系一致,与数学课本相反)。");
        sb.AppendLine("- **id 生命周期**:图像存于服务端内存注册表,空闲超时或服务重启后失效,"
            + "再次访问返回 `image_not_found`;用完请显式释放。");
        sb.AppendLine("- **注册表有容量上限**:单进程内对象数与总字节数均有上限,超限的创建请求直接失败(不会驱逐已有对象)。");
        sb.AppendLine("- **多实例下 id 不共享**:注册表为进程级,水平扩容后不同实例上的 `id` 互不可见。");
        sb.AppendLine();

        return ToLf(sb.ToString());
    }

    /// <summary>
    /// 追加单个工具的参数表(参数名 / 类型 / 必填 / 说明),由 MCP 工具的 <c>InputSchema</c> 派生。
    /// </summary>
    /// <remarks>
    /// 参数名直接取自 JSON Schema 的 <c>properties</c> 键名,即客户端 <c>tools/list</c> 看到的**同一个名字** ——
    /// 手写一份参数表必然与 schema 逐渐分叉,而分叉后的技能会教调用方填一个不存在的参数名。
    /// </remarks>
    private static void AppendParameterTable(StringBuilder sb, Tool tool)
    {
        JsonElement schema = tool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object
            || !properties.EnumerateObject().Any())
        {
            sb.AppendLine("本工具无参数。");
            return;
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var requiredElement)
            && requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in requiredElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name)
                {
                    required.Add(name);
                }
            }
        }

        sb.AppendLine("| 参数 | 类型 | 必填 | 说明 |");
        sb.AppendLine("| --- | --- | --- | --- |");

        foreach (JsonProperty property in properties.EnumerateObject())
        {
            string description = ReadString(property.Value, "description") ?? "";
            string defaultValue = DescribeDefault(property.Value);

            // 描述里已写明默认值的(如「默认 rgba32」)不再追加,避免出现「默认 A;默认 A」的重复
            string defaultSuffix = defaultValue.Length > 0 && !description.Contains("默认", StringComparison.Ordinal)
                ? $";默认 {defaultValue}"
                : "";

            sb.AppendLine(
                $"| `{Inline(property.Name)}` "
                + $"| {Inline(ReadTypeLabel(property.Value))} "
                + $"| {(required.Contains(property.Name) ? "是" : "否")} "
                + $"| {Inline(description + defaultSuffix)} |");
        }
    }

    /// <summary>
    /// 读取参数的类型标签。
    /// </summary>
    /// <remarks>
    /// 可空参数在 JSON Schema 里是<b>数组形式</b>的联合类型(如 <c>["string","null"]</c>),
    /// 不是单一字符串。只认字符串形式会把 <c>format</c> / <c>points</c> / <c>color</c> 这类参数
    /// 一律渲染成 <c>-</c> —— 而它们恰恰是调用方最需要知道类型的参数。
    /// </remarks>
    private static string ReadTypeLabel(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.Object || !property.TryGetProperty("type", out var type))
        {
            return "-";
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return type.GetString() ?? "-";
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            // 只取第一个非 null 分支:联合类型里的 null 表达的是「可空」,而必填性已由 required 单独承载
            foreach (JsonElement item in type.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name && name != "null")
                {
                    return name;
                }
            }
        }

        return "-";
    }

    /// <summary>读取参数的默认值并渲染为文本;无默认值或默认值为 <c>null</c> 时返回空串。</summary>
    private static string DescribeDefault(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.Object
            || !property.TryGetProperty("default", out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString(),
        };
    }

    /// <summary>把工具的副作用标注渲染成一行可读文本(取自 MCP 工具声明的 annotations)。</summary>
    private static string DescribeAnnotations(ToolAnnotations? annotations)
    {
        if (annotations is null)
        {
            return "未声明";
        }

        string readOnly = annotations.ReadOnlyHint == true ? "只读" : "非只读";
        string destructive = annotations.DestructiveHint == true ? "破坏性" : "非破坏性";
        string idempotent = annotations.IdempotentHint == true ? "幂等" : "非幂等";

        return $"{readOnly} / {destructive} / {idempotent}";
    }

    /// <summary>读取 JSON 对象中的字符串字段;字段缺失或非字符串时返回 <c>null</c>。</summary>
    private static string? ReadString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    /// <summary>去除首尾空白与尾部斜杠,使拼接路径时不会出现双斜杠。</summary>
    private static string NormalizeBaseUrl(string baseUrl) => (baseUrl ?? "").Trim().TrimEnd('/');

    /// <summary>
    /// 渲染表格单元格内的单行文本:换行折叠为空格、竖线转义,避免撑破 Markdown 表格结构。
    /// </summary>
    private static string Inline(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        return text.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();
    }

    /// <summary>
    /// 转义 PowerShell 双引号字符串中的特殊字符(反引号、双引号、美元符号)。
    /// </summary>
    /// <remarks>
    /// 技能名与描述目前不含这些字符,但脚本正文由字符串拼接而成:一旦描述里出现 <c>$</c> 或 <c>"</c>,
    /// 未转义就会让生成的脚本语法错误 —— 而报错发生在用户终端里,排查成本远高于此处一次替换。
    /// </remarks>
    private static string EscapePowerShell(string value)
        => (value ?? "").Replace("`", "``").Replace("\"", "`\"").Replace("$", "`$");
}
