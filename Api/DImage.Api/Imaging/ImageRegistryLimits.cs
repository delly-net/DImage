namespace DImage.Api.Imaging;

/// <summary>
/// 内存图像注册表的上限配置:对象数、总字节数与空闲回收时长。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="ImageLimits"/> 的分工:<see cref="ImageLimits"/> 约束<b>单个缓冲区</b>的几何尺寸,
/// 本类型约束<b>整个注册表</b>的累计占用。二者缺一不可 —— 单个 256 MiB 的缓冲区完全合法,
/// 但四个这样的对象就会吃掉 1 GiB,「单图上限」对此无能为力。
/// </para>
/// <para>
/// <b>上限是安全要求,不是性能调优</b>:<c>POST /mcp</c> 暴露在公网,每个请求都会创建一个
/// 常驻内存对象,而删除它们全凭调用方自觉。<b>没有上限的注册表等于一个无认证的内存放大器</b>。
/// </para>
/// <para>
/// 默认值<b>必须写在主构造函数的参数默认值上</b>,不能只写在 <c>appsettings.json</c> 里:
/// 配置文件缺失、键名写错或部署时漏挂都会让上限静默失效,而带默认值的构造函数保证
/// 「无论配置如何,上限总是存在」。
/// </para>
/// </remarks>
/// <param name="MaxImageCount">注册表内允许同时存在的图像对象数上限。</param>
/// <param name="MaxTotalBytes">注册表内全部图像的有效像素字节数之和的上限。</param>
/// <param name="IdleTtl">对象的空闲回收时长;超过该时长未被访问的对象由后台服务回收。</param>
public readonly record struct ImageRegistryLimits(
    int MaxImageCount = ImageRegistryLimits.DefaultMaxImageCount,
    long MaxTotalBytes = ImageRegistryLimits.DefaultMaxTotalBytes,
    TimeSpan? IdleTtl = null)
{
    /// <summary>对象数上限的默认值。</summary>
    /// <remarks>
    /// 取 64:MCP 客户端的典型用法是「创建 → 逐点写入 → 导出 → 释放」的短链条,
    /// 同时在手的工作图通常个位数。64 为多轮对话中的漏掉释放留足缓冲,
    /// 又能在 512 MiB 字节上限内被单图上限(256 MiB)兜住。
    /// </remarks>
    public const int DefaultMaxImageCount = 64;

    /// <summary>总字节上限的默认值,512 MiB。</summary>
    /// <remarks>
    /// 取 512 MiB:为「若干张中等尺寸工作图并存」留出余量(如 8 张 8192×8192 灰度图),
    /// 同时把最坏情况的内存放大限制在一个可预期的量级内,不至于因单个请求就打满宿主内存。
    /// </remarks>
    public const long DefaultMaxTotalBytes = 536_870_912;

    /// <summary>空闲回收时长的默认值,30 分钟。</summary>
    /// <remarks>
    /// 取 30 分钟:远大于单次「创建 → 写入 → 导出」的耗时(通常秒级),
    /// 不会误伤正在进行中的多轮编辑;又能在一个可预期的时间窗内回收被遗忘的对象。
    /// </remarks>
    public static readonly TimeSpan DefaultIdleTtl = TimeSpan.FromMinutes(30);

    /// <summary>空闲回收时长,把「未显式指定」解析为 <see cref="DefaultIdleTtl"/>。</summary>
    /// <remarks>
    /// 用可空类型而非直接给定 <see cref="TimeSpan"/> 默认值:后者会让「未指定」与「显式指定
    /// <see cref="TimeSpan.Zero"/>」无法区分,而 Zero 意味着「对象一经创建即可被回收」,
    /// 是一个合法但极具破坏性的取值,不该成为缺省行为。
    /// </remarks>
    public TimeSpan EffectiveIdleTtl => IdleTtl ?? DefaultIdleTtl;

    /// <summary>以全部默认值构造一套上限。</summary>
    /// <remarks>
    /// 请一律经本属性或显式传参构造,<b>不要使用 <c>default(ImageRegistryLimits)</c></b>:
    /// <c>record struct</c> 的 <c>default</c> 是全零值,主构造函数的参数默认值<b>不会</b>生效,
    /// 于是得到 <see cref="MaxImageCount"/>=0 —— 任何创建都会以「容量超限」失败。
    /// 好在这是<b>失败关闭</b>而非失败开放:全零配置不会放开任何资源,只会拒绝服务。
    /// </remarks>
    public static ImageRegistryLimits Default => new();
}
