namespace DImage.Api.Skills;

/// <summary>
/// 对外可下载的 Skill 清单,集中定义技能 key / 名称 / 描述。
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

    /// <summary>
    /// 项目 Skill 定义清单(key / 技能名 / 描述),供安装脚本与管理端清单复用。
    /// </summary>
    /// <remarks>
    /// 顺序即安装脚本的执行顺序;本表是技能清单的<b>唯一事实源</b>,禁止在端点或脚本模板内另写一份。
    /// </remarks>
    public static (string Key, string Name, string Description)[] Definitions { get; } =
    [
        (Key: DimageOnKey, Name: DimageOnName, Description: DimageOnDescription),
    ];
}
