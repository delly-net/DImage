namespace DImage.Api.Skills;

/// <summary>
/// 技能随附分发的文件(与 <c>SKILL.md</c> 落在<b>同一目录</b>)。
/// </summary>
/// <param name="Name">文件名,即落盘名,同时是内容分发接口 <c>{file}</c> 段的查询值。</param>
/// <param name="Description">文件用途说明(供管理端清单展示)。</param>
public sealed record SkillFile(string Name, string Description);

/// <summary>
/// 对外可下载的 Skill 清单,集中定义技能 key / 名称 / 描述 / 随附文件。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="Auth.AuthConstants"/>、<see cref="Mcp.McpProtocol"/>、<c>ImageLimits</c> 同属「常量集中定义」原则的落地:
/// 安装脚本、技能内容分发(<c>GET /skill/install/{skill}/content</c>)与管理端清单
/// (<c>GET /api/v1/skills/install-command</c>)三处共用本表,新增技能只需在此加一行并补一个内容生成方法。
/// </para>
/// <para>
/// <b>Key 与 Name 是两个不同用途的标识</b>:<c>Key</c> 是 content 接口的查询值(URL 片段,须为 URL 安全字符),
/// <c>Name</c> 是技能在用户项目中的落盘目录名(<c>.claude/skills/{Name}/</c>)。本技能两者同名,但刻意不合并 ——
/// 一旦某个技能的下载 key 与目录名不再一致(例如改名后保留旧 key 兼容),合并字段就必须再拆开。
/// </para>
/// </remarks>
public static class SkillCatalog
{
    /// <summary>「小D图像 MCP 能力总览」技能 key(供 content 接口按 key 分发)。</summary>
    public const string DimageOnKey = "dimage-on";

    /// <summary>「小D图像 MCP 能力总览」技能名(落盘目录名 <c>.claude/skills/{Name}/</c>)。</summary>
    public const string DimageOnName = "dimage-on";

    /// <summary>「小D图像 MCP 能力总览」技能描述(供管理端清单与安装脚本展示)。</summary>
    /// <remarks>
    /// 用词为<b>「加载到」而非「输出到」</b>:技能正文被注入上下文,但<b>不在对话中回显</b>
    /// (唯一可见输出是 <see cref="SkillService.DimageOnLoadedNotice"/>)。描述里写「输出」会与
    /// 技能的实际行为相悖 —— 而本串同时进 SKILL.md frontmatter,正是客户端判断「何时该用本技能」的依据。
    /// </remarks>
    public const string DimageOnDescription = "将小D图像服务的全部图像处理 MCP 能力加载到对话上下文";

    /// <summary><c>dimage-down.py</c> 的用途说明(供管理端技能清单展示)。</summary>
    public const string DimageDownFileDescription = "对接 MCP 图像下载能力,把内存图像按 Id 直接落盘的命令行脚本";

    /// <summary>
    /// 项目 Skill 定义清单(key / 技能名 / 描述 / 随附文件),供安装脚本与管理端清单复用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 顺序即安装脚本的执行顺序;本表是技能清单的<b>唯一事实源</b>,禁止在端点或脚本模板内另写一份。
    /// </para>
    /// <para>
    /// <b>随附文件也进本表</b>:安装脚本、文件分发接口与管理端清单三处都从 <c>Files</c> 派生,
    /// 于「某个技能带哪几个文件」这件事上同样只有一处答案 —— 否则新增一个文件要改三处,
    /// 漏掉文件分发分支的表现是「清单里有、下载 404」。
    /// </para>
    /// </remarks>
    public static (string Key, string Name, string Description, SkillFile[] Files)[] Definitions { get; } =
    [
        (Key: DimageOnKey, Name: DimageOnName, Description: DimageOnDescription,
            Files: [new SkillFile(DimageDownScript.FileName, DimageDownFileDescription)]),
    ];

    /// <summary>取某技能随附的文件清单;key 未知时返回空数组(不抛异常,由调用方翻译为 404)。</summary>
    public static SkillFile[] FilesFor(string skillKey)
        => Definitions.FirstOrDefault(definition => definition.Key == skillKey).Files ?? [];

    /// <summary>按技能 key 与文件名定位随附文件;未命中返回 <c>null</c>。</summary>
    public static SkillFile? FindFile(string skillKey, string fileName)
        => FilesFor(skillKey).FirstOrDefault(file => file.Name == fileName);
}
