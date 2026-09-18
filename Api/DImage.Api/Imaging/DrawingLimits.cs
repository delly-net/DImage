namespace DImage.Api.Imaging;

/// <summary>
/// 绘制能力的上限集中定义,与 <see cref="ImageLimits"/> / <see cref="ImageRegistryLimits"/>
/// 同属「常量集中定义」原则的落地 —— 数值只在此处出现一次,不在算法实现内散落字面量。
/// </summary>
/// <remarks>
/// <para>
/// <b>这些上限是安全要求,不是性能调优</b>。绘制引入了一条与「创建图像」性质不同的新攻击面:
/// 图像的尺寸受 <see cref="ImageLimits"/> 约束,而 <b>输入文本的复杂度不受任何约束</b> ——
/// 一条 SVG path 的 <c>d</c> 字符串可以是数兆字符、可含上万条命令,
/// 而每条贝塞尔曲线在扁平化后又会膨胀一到两个数量级。三者相乘足以让一次请求把 CPU 打满。
/// <c>POST /mcp</c> 暴露在公网,故每一条都必须有明确上界。
/// </para>
/// <para>
/// <b>超限一律报错,绝不静默截断</b>:截断会让调用方拿到一张「画了一半」的图却收到成功响应,
/// 而它无从判断缺失的是哪一段 —— 静默的部分成功比立即失败昂贵得多。
/// </para>
/// </remarks>
public static class DrawingLimits
{
    /// <summary>
    /// <c>d</c> 字符串的最大长度(字符),64 Ki。
    /// </summary>
    /// <remarks>
    /// 取 64 Ki:足以容纳任何人工编写或程序生成的实用路径(常见图标路径在数百字符量级),
    /// 同时把「先把整串读进来再解析」的内存放大锁在一个可预期的量级内。
    /// </remarks>
    public const int MaxPathTextLength = 65_536;

    /// <summary>
    /// 单条路径的最大命令数,16384。
    /// </summary>
    /// <remarks>
    /// 命令数在<b>解析过程中增量计数并即时中断</b>,而不是解析完再检查 ——
    /// 后者等于先让攻击者把 CPU 耗完,再告诉他「超限了」。上限的作用是限制工作量,不是限制结果。
    /// </remarks>
    public const int MaxPathCommands = 16_384;

    /// <summary>
    /// 扁平化后允许的最大线段总数,262144。
    /// </summary>
    /// <remarks>
    /// 曲线细化后的段数是命令数的放大版:一条三次贝塞尔按 0.1 像素容差细分可达数十段。
    /// 该上限约束的是<b>全部曲线共享的总预算</b>,而非单条曲线 —— 逐条设限挡不住「一万条曲线各细分十段」。
    /// </remarks>
    public const int MaxFlattenedSegments = 262_144;

    /// <summary>
    /// 曲线自适应细分的递归深度上限,16 层。
    /// </summary>
    /// <remarks>
    /// 段数预算之外的第二道兜底:控制点退化(三点几乎共线却又因浮点噪声判定为不平)时,
    /// 依赖容差的收敛判据可能迟迟不成立,而递归分叉是指数级的。
    /// 有了深度上限,最坏情况被钉死在 <c>2^16</c> 段,再交由段数预算收口。
    /// </remarks>
    public const int MaxSubdivisionDepth = 16;

    /// <summary>
    /// 椭圆弧按角度采样时单条弧的段数上限,4096。
    /// </summary>
    /// <remarks>
    /// 弧度采样的段数由「半径 / 容差」推出,虽已是有限值,但极大半径配极小容差仍可算出百万级段数;
    /// 该上限把它钉死,超出部分由像素级容差本身承担(此时单段远小于一个像素,再多也无视觉差异)。
    /// </remarks>
    public const int MaxArcSegments = 4_096;

    /// <summary>
    /// 覆盖率累积缓冲的分块行高,128 行。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这是绘制路径上最要紧的一个数值。</b>抗锯齿要求「先把整幅图形的覆盖率累积起来、再统一混合落笔」,
    /// 而最朴素的实现是给整幅图分配一个 <c>float[]</c> 覆盖率缓冲:在 <c>8192×8192</c> 的画布上
    /// 就是 <c>67M × 4B = 268 MiB</c> —— <b>已经超过单图 256 MiB 的上限</b>,
    /// 并成为一条真实的 DoS 面(公网请求即可触发)。
    /// </para>
    /// <para>
    /// 故覆盖率缓冲<b>按固定行高分块</b>,峰值恒为 <c>MaxDimension × CoverageBandHeight × 4B</c>
    /// = <c>32768 × 128 × 4</c> = <b>16 MiB</b>,与图形尺寸、画布尺寸<b>均无关地有界</b>。
    /// 分块对两种覆盖率来源都成立:描边按线段包围盒 ∩ band 遍历,填充按 band 内的扫描线求交,
    /// 且一个像素只属于一个 band,故「每像素只混合一次」的语义不受影响。
    /// </para>
    /// </remarks>
    public const int CoverageBandHeight = 128;

    /// <summary>
    /// 曲线扁平化的容差(像素),0.1 像素。
    /// </summary>
    /// <remarks>
    /// 取 0.1:远小于抗锯齿的 1 像素过渡带,故折线逼近带来的径向误差不会改变覆盖率的结果;
    /// 同时不至于让细分段数失控。该值同时用于贝塞尔细分判据与椭圆弧的角度采样步长。
    /// </remarks>
    public const double BezierFlatnessTolerance = 0.1;

    /// <summary>
    /// 单个坐标分量的绝对值上限,<c>1e7</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 坐标本身允许落在画布外(越界是<b>裁剪</b>语义,不是错误),但仍必须是有界的有限值:
    /// 包围盒会直接决定循坏边界,而 <c>1e300</c> 这样的坐标会让「线段包围盒 ∩ 画布」这一步
    /// 退化成一次天文数字量级的遍历 —— 请求挂死,且不产生任何错误信息。
    /// </para>
    /// <para>
    /// <c>1e7</c> 比最大边长(<c>32768</c>)大两个数量级,足以容纳「形状整体在画布外」的常见用法
    /// (如把图形平移到画布外做动画),又能保证所有尺寸乘积在 <c>double</c> 下毫无精度压力。
    /// </para>
    /// </remarks>
    public const double MaxCoordinateMagnitude = 1e7;

    /// <summary>
    /// 线宽上限,4096 像素。
    /// </summary>
    /// <remarks>
    /// 线宽会按「线宽 / 2 + 1」膨胀每条线段的遍历包围盒,取 <see cref="ImageLimits.MaxDimension"/> 的
    /// 八分之一:足以画满任意画布(粗线可用填充表达),又不至于让单条线段的遍历范围失去约束。
    /// </remarks>
    public const double MaxStrokeWidth = 4_096;
}
