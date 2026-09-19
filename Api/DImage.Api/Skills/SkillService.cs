using System.Text;
using System.Text.Json;
using DImage.Api.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DImage.Api.Skills;

/// <summary>
/// 对外安装文本生成服务:两类安装脚本(MCP 客户端接入配置、Skill 下载)的命令与正文,以及各技能的 <c>SKILL.md</c> 内容。
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
    /// <summary>Skill 安装脚本的下载路径(根路径,匿名访问)。</summary>
    public const string InstallScriptPath = "/skill/install";

    /// <summary>
    /// <c>dimage-on</c> 技能执行时的**唯一可见输出**(一字不差,前后不附加任何内容)。
    /// </summary>
    /// <remarks>
    /// 单独抽成常量而非内联进生成语句,是为了让它成为**可断言的事实源**:技能改造的核心契约
    /// 就是「输出只有这一行」,而契约一旦只存在于一段拼接字符串里,验收只能靠模糊匹配。
    /// </remarks>
    public const string DimageOnLoadedNotice = "DImage工具包加载完成";

    /// <summary>
    /// <c>dimage-on</c> 技能约定的临时文件目录,**相对项目根**。
    /// </summary>
    /// <remarks>
    /// 抽成常量而非在正文各节内联:该串出现在技能正文的「执行要求」「配套脚本」「临时文件」三节,
    /// 且验收要按它断言「产物里确实写死了这条约束」。与 <see cref="DimageOnLoadedNotice"/> 同一处置 ——
    /// 契约文本一旦散落在多段拼接里,就只能靠模糊匹配守住。
    /// </remarks>
    public const string SkillTempDirectory = ".claude/temp";

    /// <summary>技能内容的下载路径前缀(根路径,匿名访问),后接 <c>{skillKey}/content</c>。</summary>
    public const string SkillContentPathPrefix = "/skill/install";

    /// <summary>MCP 安装脚本的下载路径(根路径,匿名访问)。</summary>
    /// <remarks>
    /// 与 <see cref="InstallScriptPath"/> 对称:<c>/skill/install</c> 装技能,<c>/mcp/install</c> 装 MCP 客户端接入配置。
    /// 挂在根路径而非 <c>/api/v1</c> 下的理由与 Skill 安装脚本完全相同 —— 二者都由用户在终端执行,没有登录态可携带。
    /// </remarks>
    public const string McpInstallPath = "/mcp/install";

    /// <summary>
    /// 生成用户复制到终端执行的安装命令(PowerShell)。
    /// </summary>
    /// <param name="baseUrl">服务对外根地址(环境变量 <c>API_BASE_URL</c>,兜底配置 <c>Service:BaseUrl</c>),允许带尾部斜杠。</param>
    /// <remarks>
    /// <c>irm</c> 是 <c>Invoke-RestMethod</c> 的别名;对 <c>text/plain</c> 响应它返回响应体字符串,
    /// 管道给 <c>iex</c>(<c>Invoke-Expression</c>)即在本会话中执行该脚本 —— 故脚本里的 <c>$PWD</c>
    /// 是<b>用户执行命令时所在的目录</b>,技能因此装到用户当前项目而非全局目录。
    /// </remarks>
    public static string GenerateInstallCommand(string baseUrl)
        => $"irm {NormalizeBaseUrl(baseUrl)}{InstallScriptPath} | iex";

    /// <summary>
    /// 生成 PowerShell 安装脚本:循环下载清单中每个技能的 <c>SKILL.md</c> 与其随附文件,写入执行命令所在目录的
    /// <c>.claude\skills\{技能名}\</c>(UTF-8)。
    /// </summary>
    /// <param name="baseUrl">服务对外根地址(环境变量 <c>API_BASE_URL</c>,兜底配置 <c>Service:BaseUrl</c>)。</param>
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
    /// <para>
    /// <b>随附文件清单在脚本里只存文件名,不存说明</b>:说明是管理端清单的展示字段,安装脚本用不到;
    /// 而把「哈希表套哈希表数组」写进 <c>@{ }</c> 字面量会踩 PowerShell 单元素数组被解包的老坑
    /// (<c>foreach</c> 到一个哈希表本身而非其元素)。存字符串数组则两种版本下语义都确定。
    /// </para>
    /// </remarks>
    public static string GenerateInstallScript(string baseUrl, string serviceName, string serviceVersion)
    {
        var root = NormalizeBaseUrl(baseUrl);

        // 以 , 分隔相邻元素、末项不带尾逗号:PowerShell 的 @(...) 内不允许尾逗号,否则解析报错
        var skillLines = string.Join(",\n", SkillCatalog.Definitions.Select(s =>
            $"    @{{ Name = \"{EscapePowerShell(s.Name)}\"; "
            + $"Key = \"{EscapePowerShell(s.Key)}\"; "
            + $"Desc = \"{EscapePowerShell(s.Description)}\"; "
            + $"Files = @({string.Join(", ", s.Files.Select(f => $"\"{EscapePowerShell(f.Name)}\""))}) }}"));

        var script = $$"""
            # 小D图像 Skill 下载安装脚本(由服务端动态生成,请勿手工修改)
            # 服务:{{serviceName}} {{serviceVersion}}
            # 用法:irm {{root}}{{InstallScriptPath}} | iex

            $baseUrl = "{{EscapePowerShell(root)}}"
            $skills = @(
            {{skillLines}}
            )

            $skillCount = 0
            $fileCount = 0

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
                $skillCount++

                # 随附文件与 SKILL.md 同目录落盘(如 dimage-down.py),编码口径完全一致
                foreach ($f in $s.Files) {
                    $fileUrl = "$baseUrl{{SkillContentPathPrefix}}/$($s.Key)/files/$f"
                    $fileContent = (Invoke-WebRequest -Uri $fileUrl -UseBasicParsing).Content

                    $filePath = Join-Path $targetDir $f
                    [System.IO.File]::WriteAllText($filePath, $fileContent, (New-Object System.Text.UTF8Encoding($false)))
                    $fileCount++

                    Write-Host "  已安装:$($s.Name)/$f"
                }

                Write-Host "  已安装:$($s.Name) - $($s.Desc)"
            }

            Write-Host ""
            Write-Host "[小D图像] Skill 安装完成" -ForegroundColor Green
            Write-Host "  服务:$baseUrl"
            Write-Host "  位置:$PWD\.claude\skills"
            Write-Host "  技能 $skillCount 个,随附文件 $fileCount 个"
            Write-Host ""
            """;

        return ToLf(script);
    }

    /// <summary>
    /// 生成用户复制到终端执行的 MCP 安装命令(PowerShell)。
    /// </summary>
    /// <param name="baseUrl">服务对外根地址(环境变量 <c>API_BASE_URL</c>,兜底配置 <c>Service:BaseUrl</c>)。</param>
    public static string GenerateMcpInstallCommand(string baseUrl)
        => $"irm {NormalizeBaseUrl(baseUrl)}{McpInstallPath} | iex";

    /// <summary>
    /// 生成 MCP 安装脚本:把本服务写入执行命令所在目录的 <c>.mcp.json</c>。
    /// </summary>
    /// <param name="baseUrl">服务对外根地址(环境变量 <c>API_BASE_URL</c>,兜底配置 <c>Service:BaseUrl</c>)。</param>
    /// <param name="serviceName">服务名,仅用于脚本注释与结束提示。</param>
    /// <param name="serviceVersion">服务版本,仅用于脚本注释与结束提示。</param>
    /// <remarks>
    /// <para>
    /// <b>失败路径一律 <c>Write-Host</c> / <c>Write-Warning</c> + <c>return</c>,禁止 <c>throw</c> 与 <c>exit</c></b>。
    /// 本脚本由用户 <c>irm ... | iex</c> 在**自己的交互式会话**中执行:<c>throw</c> 会把 PowerShell 异常栈糊到用户脸上,
    /// 而 <c>exit</c> 更严重 —— 它会**直接终止用户的终端会话**(`iex` 不是子进程)。故「中止」在此只有一个合法形态:说清楚原因,然后 <c>return</c>。
    /// </para>
    /// <para>
    /// <b>合并而非覆盖</b>:用户的 <c>.mcp.json</c> 往往已配置了别的 MCP 服务(含各自凭据),
    /// 整份重写等于凭一次安装把别人的配置删干净。故仅在解析成功时按 key 合并;解析失败即中止且**不落盘**。
    /// </para>
    /// <para>
    /// <b><c>-Depth</c> 必须显式给足 10</b>:<c>ConvertTo-Json</c> 默认深度为 2,会把 <c>headers</c> 这类嵌套结构
    /// 静默降级成字符串 —— 产物仍是合法 JSON、也能写进文件,但 MCP 客户端读到的 <c>headers</c> 不再是对象,
    /// 表现为「配置看着有、鉴权就是不生效」,排查成本极高。
    /// </para>
    /// <para>
    /// <b>落盘编码固定 UTF-8 无 BOM</b>,沿用 Skill 安装脚本的约定。
    /// </para>
    /// </remarks>
    public static string GenerateMcpInstallScript(string baseUrl, string serviceName, string serviceVersion)
    {
        var root = NormalizeBaseUrl(baseUrl);

        var script = $$"""
            # 小D图像 MCP 安装脚本(由服务端动态生成,请勿手工修改)
            # 服务:{{serviceName}} {{serviceVersion}}
            # 用法:irm {{root}}{{McpInstallPath}} | iex
            #
            # 作用:把 dimage 服务写入「执行命令所在目录」的 .mcp.json(即当前项目,而非用户全局目录)。
            # 合并:.mcp.json 已存在时仅新增或更新 mcpServers.{{McpClientConfig.ServerKey}},其他 MCP 服务原样保留。
            #      注意 JSON 重新序列化会重排键序与缩进,合并后文件字节与原文不同(键值语义不变)。
            # TOKEN:优先读环境变量 DIMAGE_TOKEN,未设置则提示输入;脚本正文不含任何真实凭据。

            $baseUrl = "{{EscapePowerShell(root)}}"
            $mcpUrl = "$baseUrl{{McpProtocol.EndpointPath}}"
            $serverKey = "{{McpClientConfig.ServerKey}}"
            $target = Join-Path $PWD ".mcp.json"

            # ———— 取 TOKEN:环境变量优先,缺失则交互输入 ————
            $token = $env:DIMAGE_TOKEN
            if ([string]::IsNullOrWhiteSpace($token)) {
                # 非交互环境下 Read-Host 会失败,按「未取得 TOKEN」处理,不让异常逃逸到用户终端
                try {
                    $secure = Read-Host -Prompt "请输入 MCP TOKEN(输入内容不回显)" -AsSecureString
                    $token = [System.Net.NetworkCredential]::new("", $secure).Password
                }
                catch {
                    $token = $null
                }
            }

            if ([string]::IsNullOrWhiteSpace($token)) {
                Write-Host ""
                Write-Warning "未取得 TOKEN,已中止安装:$target 未做任何改动。"
                Write-Warning "请先设置环境变量 DIMAGE_TOKEN 后重试,或在提示时输入 TOKEN。"
                return
            }

            # ———— 解析既有配置:只有存在且合法才合并 ————
            $config = $null
            if (Test-Path $target) {
                $raw = Get-Content -Path $target -Raw -Encoding UTF8
                if (-not [string]::IsNullOrWhiteSpace($raw)) {
                    try {
                        $config = $raw | ConvertFrom-Json
                    }
                    catch {
                        Write-Host ""
                        Write-Warning "已有 .mcp.json 不是合法 JSON,已中止安装:$target 未做任何改动。"
                        Write-Warning "请修正该文件后重试;如不再需要原内容,可先备份改名再重新执行本脚本。"
                        return
                    }

                    if ($null -eq $config -or -not ($config -is [PSCustomObject])) {
                        Write-Host ""
                        Write-Warning "已有 .mcp.json 的顶层不是 JSON 对象,已中止安装:$target 未做任何改动。"
                        return
                    }
                }
            }

            if ($null -eq $config) {
                $config = New-Object PSObject
            }

            # ———— 合并:mcpServers 缺失则补建,同名项被更新 ————
            if (@($config.PSObject.Properties.Name) -notcontains "mcpServers" -or $null -eq $config.mcpServers) {
                $config | Add-Member -NotePropertyName "mcpServers" -NotePropertyValue (New-Object PSObject) -Force
            }

            $entry = [ordered]@{
                type    = "{{McpClientConfig.TransportType}}"
                url     = $mcpUrl
                headers = [ordered]@{ Authorization = "Bearer $token" }
            }

            $config.mcpServers | Add-Member -NotePropertyName $serverKey -NotePropertyValue $entry -Force
            $others = @($config.mcpServers.PSObject.Properties.Name | Where-Object { $_ -ne $serverKey })

            # 深度必须给足:默认 2 会把 headers 截断成字符串,产物「合法但不可用」
            $json = $config | ConvertTo-Json -Depth 10
            [System.IO.File]::WriteAllText($target, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))

            # ———— 回执 ————
            Write-Host ""
            Write-Host "[小D图像] MCP 接入配置已写入" -ForegroundColor Green
            Write-Host "  文件:$target"
            Write-Host "  服务:$mcpUrl"
            if ($others.Count -gt 0) {
                Write-Host "  已保留的其他 MCP 服务:$($others -join ', ')"
            }
            Write-Host ""
            """;

        return ToLf(script);
    }

    /// <summary>
    /// 按技能 key 生成对应的 <c>SKILL.md</c> 内容。
    /// </summary>
    /// <param name="skillKey">技能 key(取自 <see cref="SkillCatalog.Definitions"/>)。</param>
    /// <param name="tools">当前服务注册的全部 MCP 工具(取自 <c>McpServerOptions.ToolCollection</c>)。</param>
    /// <param name="baseUrl">服务对外根地址(环境变量 <c>API_BASE_URL</c>,兜底配置 <c>Service:BaseUrl</c>)。</param>
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
    /// 按技能 key 与文件名生成随附文件的正文。
    /// </summary>
    /// <param name="skillKey">技能 key(取自 <see cref="SkillCatalog.Definitions"/>)。</param>
    /// <param name="fileName">随附文件名(取自该技能定义项的 <c>Files</c>)。</param>
    /// <returns>文件正文;技能或文件未知时返回 <c>null</c>(由端点层翻译为 404)。</returns>
    /// <remarks>
    /// <b>以「清单里声明过」为唯一准入判据</b>:分发接口不接受清单外的文件名,故这里查不到即拒绝,
    /// 而不是去磁盘上找文件。落盘内容与清单声明因此永远一致 —— 清单是事实源,磁盘不是。
    /// </remarks>
    public static string? TryGenerateSkillFileContent(string skillKey, string fileName)
    {
        if (SkillCatalog.FindFile(skillKey, fileName) is null)
        {
            return null;
        }

        return fileName == DimageDownScript.FileName ? ToLf(DimageDownScript.Content) : null;
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
    /// <para>
    /// <b>技能的唯一职责是把服务能力注入对话上下文,且不在对话中回显</b>。技能被调用时,
    /// 客户端会把本 <c>SKILL.md</c> <b>正文整体注入上下文</b> —— 于是正文本身就是数据源,
    /// 技能执行时不需要调用任何工具、不读任何文件;模型只需回一行
    /// <see cref="DimageOnLoadedNotice"/>。
    /// </para>
    /// <para>
    /// 因此正文只承载<b>能改变调用方式的信息</b>:「服务接入」「能力清单」两节是 MCP 信息本体,
    /// 「配套脚本」一节回答「拿到 id 之后怎么把图取回来」—— 后者无法由工具说明承载(工具只返回图像内容块,
    /// 把结果交给模型等于把一段 base64 背进上下文);「临时文件」一节回答「产物落到哪」——
    /// 该约束同样无法由工具说明承载(工具说明只描述单次调用),却是使用本技能时最易污染宿主项目的点。
    /// 早先的「典型工作流」「注意事项」两节已移除:
    /// 它们不进上下文也无损能力,却让每次加载都多背一段与当前任务无关的长文。
    /// </para>
    /// <para>
    /// 工具清单与参数表全部由 <paramref name="tools"/> 派生,且<b>不含生成时间戳</b> ——
    /// 同一服务版本必须产出逐字节相同的文本,否则「下载内容与接口返回一致」这类断言将失去意义。
    /// </para>
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
        sb.AppendLine("此技能把 DImage 服务当前提供的**全部图像处理 MCP 能力**一次性加载到对话上下文");
        sb.AppendLine("(服务端工具/参数已随本技能注入,无需再查),便于确认「这台服务现在能画什么、每项能力怎么调、参数怎么填」。");
        sb.AppendLine();

        sb.AppendLine("## 使用方法");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine($"/{SkillCatalog.DimageOnName}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("或在聊天中输入「加载图像工具包」触发。");
        sb.AppendLine();

        sb.AppendLine("## 执行要求(务必照做)");
        sb.AppendLine();
        sb.AppendLine("1. **本技能正文即数据源,已被自动注入上下文**:不要调用任何 MCP 工具、不要读取本地文件、不要联网检索 ——");
        sb.AppendLine("   下文「服务接入」「能力清单」两节就是被加载的全部内容。");
        sb.AppendLine("2. **不要在对话中复述本技能正文**:不列工具名、不列参数表、不贴配置片段、不给摘要或统计表格。");
        sb.AppendLine("3. **只输出下面这一行,前后不附加任何其他文字**(不加工具数量、不加快照说明、不加引导语):");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(DimageOnLoadedNotice);
        sb.AppendLine("```");
        sb.AppendLine("4. 无论以何种方式触发(显式调用或聊天触发),本技能的输出恒为上述一行。");
        sb.AppendLine($"5. **本技能产生的一切临时文件一律落在 `{{项目根目录}}/{SkillTempDirectory}`**,"
            + "不得落到项目根目录、当前工作目录或其他位置 —— 完整规则见下文「临时文件」一节。");
        sb.AppendLine();

        // —————————————— 服务接入 ——————————————
        sb.AppendLine("## 服务接入");
        sb.AppendLine();
        sb.AppendLine("| 项 | 值 |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| 服务 | {Inline(serviceName)} {Inline(serviceVersion)} |");
        sb.AppendLine($"| MCP 端点 | `{root}{McpProtocol.EndpointPath}` |");
        sb.AppendLine("| 传输方式 | Streamable HTTP(无状态,不返回 Mcp-Session-Id) |");
        sb.AppendLine("| 鉴权 | 请求头 `Authorization: Bearer <TOKEN>`(静态凭据,无过期时间) |");
        sb.AppendLine($"| 协议版本 | {McpProtocol.ProtocolVersion} |");
        sb.AppendLine();
        sb.AppendLine("客户端接入配置(`.mcp.json`,置于项目根目录):");
        sb.AppendLine();
        sb.AppendLine("```json");
        // 形状取自 McpClientConfig —— 与 MCP 安装脚本实际写入的内容同源,不在此手写第二份
        sb.Append(McpClientConfig.BuildConfigJson(root));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"> 也可用一条命令自动写入:在项目根目录执行 `irm {root}{McpInstallPath} | iex`。");
        sb.AppendLine("> 该脚本会合并保留既有 `.mcp.json` 中的其他 MCP 服务,TOKEN 取自环境变量 `DIMAGE_TOKEN` 或终端提示输入。");
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

        // —————————————— 配套脚本 ——————————————
        // 这一节是本技能唯一「工具清单之外」的内容:它回答的是「拿到 id 之后怎么把图取回来」,
        // 而这件事恰恰不能靠工具说明解决 —— image_download_png 以图像内容块返回,
        // 直接把结果交给模型等于把一段 base64 背进上下文。故此处点名配套脚本的存在与用法。
        sb.AppendLine("## 配套脚本");
        sb.AppendLine();
        sb.AppendLine($"与本技能同目录还下载了 `{DimageDownScript.FileName}`(仅用 Python 标准库,免安装依赖):"
            + "把服务端内存中的图像**按 Id 直接落盘**,不必把 base64 搬进对话上下文。");
        sb.AppendLine();
        sb.AppendLine("```bash");
        sb.AppendLine($"python .claude/skills/{SkillCatalog.DimageOnName}/{DimageDownScript.FileName} <图像 id> {SkillTempDirectory}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"- 服务地址与 TOKEN 取自项目根 `.mcp.json` 的 `{McpClientConfig.ServerKey}` 条目;"
            + $"未配置时先执行 `irm {root}{McpInstallPath} | iex`");
        sb.AppendLine($"- 目标地址按临时文件约定**一律给 `{SkillTempDirectory}`**,并**先确保该目录已存在**(见下文「临时文件」);"
            + "给已存在的目录或以 `/`、`\\` 结尾时才自动命名为 `<id>.png`,"
            + "**否则该路径被当作文件写出** —— 本脚本不会自动建目录");
        sb.AppendLine("- 退出码:0 成功 / 1 用法或配置错误 / 2 服务端报错、网络不通或写盘失败");
        sb.AppendLine();

        // —————————————— 临时文件 ——————————————
        // 放在正文末尾而不是「执行要求」之后,是刻意选择:① 顶部「执行要求」1–4 条全是**可见输出契约**,
        // 把「文件落盘」塞进去会稀释该节语义,故那里只留一条强约束 + 指向本节;② 技能正文里唯一会产出
        // 本地文件的手段就是紧邻其上的 dimage-down.py,规则与它相邻;③ 位于正文最末 = 模型开始行动前
        // 最后读到的内容,对指令遵循有利。
        // 本节的「项目根目录判定」取「技能自身位置向上三级」而非「找 .mcp.json」:后者在未安装接入配置的
        // 项目里根本找不到根(本仓库根 .mcp.json 即无 dimage 条目),而技能自身位置恒存在。
        sb.AppendLine("## 临时文件");
        sb.AppendLine();
        sb.AppendLine($"本技能产生的一切**临时文件**(下载的图像、调试图、中间产物、脚本输出等)"
            + $"一律存放在 `{{项目根目录}}/{SkillTempDirectory}` 目录中,"
            + "**不得落在项目根目录、当前工作目录或任何其他位置**。");
        sb.AppendLine();
        sb.AppendLine($"- **项目根目录的判定**:本技能安装在 `{{项目根目录}}/.claude/skills/{SkillCatalog.DimageOnName}/`,"
            + $"故自本技能所在目录**向上三级**即项目根目录;在项目根目录下操作时,该目录即 `{SkillTempDirectory}`。");
        // 「不自动建目录」这一条不是提醒而是**实测出来的坑**:dimage-down.py 的 resolve_target 只在目标
        // 「已是目录」或「以 / 或 \ 结尾」时才补 <id>.png;`.claude/temp` 不存在又没带尾分隔符时,它把该路径
        // 当**文件**接受(父目录 `.claude\` 因技能已安装而必然存在,故那道父目录校验拦不住),
        // 结果写出一个名为 temp 的文件。故本节必须把「先建目录」写成动作要求,而非可选的容错提示。
        sb.AppendLine($"- **运行脚本前先创建该目录**:`mkdir -p {SkillTempDirectory}`"
            + $"(PowerShell:`New-Item -ItemType Directory -Force -Path {SkillTempDirectory} | Out-Null`)。"
            + "上文「配套脚本」的 `dimage-down.py` **不会自动建目录**:目标目录不存在且路径未以 `/`、`\\` 结尾时,"
            + "它会把该路径当**文件**写出(得到一个名为 `temp` 的文件,而非目录)。");
        sb.AppendLine("- **例外**:用户明确指定了落点、或产物本身就是交付物时,按用户指定的位置落盘,不受本节约束。");
        sb.AppendLine($"- `{SkillTempDirectory}/` 属临时产物、无须入库,可在宿主项目的 `.gitignore` 中忽略。");
        sb.AppendLine();

        // 刻意不生成「典型工作流」「注意事项」两节:它们不进上下文也无损能力,
        // 却让每次技能加载都多背一段与当前任务无关的长文。工具用法一律以工具自身的 description 为准。

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
