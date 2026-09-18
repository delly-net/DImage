namespace DImage.Api.Imaging;

/// <summary>
/// 一次绘制请求的样式:前景色、线宽、描边/填充开关、填充规则与抗锯齿开关。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类型是纯值对象</b>:不含 <see cref="ImageBuffer"/> 引用、不读配置、不注入
/// <c>IOptions&lt;T&gt;</c>。绘制请求因此可以表达为「哪个 Id + 什么形状 + 什么样式」三样纯数据,
/// 从而让注册表方法保持 <c>Draw(string id, Shape shape, DrawStyle style)</c> 这一形态,
/// <b>不需要把裸缓冲区交给工具层</b>。
/// </para>
/// <para>
/// <b>默认值一律写在构造函数的参数默认值上</b>,与 <see cref="ImageRegistryLimits"/> 同理:
/// 依赖对象初始化器或调用方逐个赋值,会让「漏传一个参数」表现为一整类静默的绘制差异
/// (如忘记开描边 → 什么都没画出来却返回成功)。
/// </para>
/// <para>
/// <b>为什么 <see cref="Color"/> 是可空的</b>:<c>record struct</c> 的构造参数默认值必须是编译期常量,
/// 而 <see cref="PixelColor.Black"/> 是 <c>static readonly</c> 字段,无法写进参数默认值;
/// 若改用 <c>default(PixelColor)</c> 作默认,得到的却是<b>全透明</b>黑 —— 形状会「画成功但看不见」,
/// 是典型的静默失败。故改以 <c>null</c> 表示「未指定」,由 <see cref="EffectiveColor"/>
/// 解析为不透明黑。这与 <see cref="ImageRegistryLimits.IdleTtl"/> 用可空类型区分
/// 「未指定」与「显式指定」是同一手法。
/// </para>
/// </remarks>
public readonly record struct DrawStyle
{
    /// <summary>以显式参数构造绘制样式。</summary>
    /// <param name="color">前景色;<c>null</c> 表示未指定,按不透明黑处理。</param>
    /// <param name="strokeWidth">线宽(像素),须大于 0 且不超过 <see cref="DrawingLimits.MaxStrokeWidth"/>。</param>
    /// <param name="stroke">是否描边。</param>
    /// <param name="fill">是否填充。</param>
    /// <param name="fillRule">填充规则,决定自相交区域的内部判定。</param>
    /// <param name="antialias">是否抗锯齿。关闭后覆盖率只取 0 或 1,像素颜色非前景即背景。</param>
    public DrawStyle(
        PixelColor? color = null,
        double strokeWidth = 1,
        bool stroke = true,
        bool fill = false,
        FillRule fillRule = FillRule.NonZero,
        bool antialias = true)
    {
        Color = color;
        StrokeWidth = strokeWidth;
        Stroke = stroke;
        Fill = fill;
        FillRule = fillRule;
        Antialias = antialias;
    }

    /// <summary>前景色;<c>null</c> 表示未指定。</summary>
    public PixelColor? Color { get; init; }

    /// <summary>线宽(像素)。仅当 <see cref="Stroke"/> 为 <c>true</c> 时参与绘制,但<b>无论是否描边都必须合法</b>。</summary>
    public double StrokeWidth { get; init; }

    /// <summary>是否描边。端点为圆头、拐角为圆角(见 <see cref="ImageDraw"/> 的类型注释)。</summary>
    public bool Stroke { get; init; }

    /// <summary>是否填充。自相交区域是否算内部由 <see cref="FillRule"/> 决定。</summary>
    public bool Fill { get; init; }

    /// <summary>填充规则。</summary>
    public FillRule FillRule { get; init; }

    /// <summary>
    /// 是否抗锯齿。
    /// </summary>
    /// <remarks>
    /// 关闭后覆盖率只取 0 或 1,落在像素中心点上的几何被整体着色 —— 这既是「硬边」语义,
    /// 也是验收时能把结果与逐像素黄金样本直接比对的唯一模式。
    /// </remarks>
    public bool Antialias { get; init; }

    /// <summary>解析后的前景色:未指定时按不透明黑处理。</summary>
    public PixelColor EffectiveColor => Color ?? PixelColor.Black;

    /// <summary>
    /// 校验样式参数。非法时抛出,且<b>不触碰任何像素</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 线宽与填充规则<b>无条件校验</b>,不因「本次没开描边 / 没开填充」而跳过:
    /// 让校验结果依赖另一个开关的组合,是「同一份输入在不同调用路径下时好时坏」的经典来源;
    /// 而一个非法取值被静默接受,会在调用方下次打开开关时突然爆发。
    /// </para>
    /// <para>
    /// <b>既不描边也不填充不是错误</b>:它是一个合法的空操作(什么都不画,被覆盖像素数为 0),
    /// 而不是一个需要调用方处理的失败。拒绝它只会逼调用方写无意义的开关组合。
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidStrokeWidthException">线宽非有限、小于等于 0,或超过 <see cref="DrawingLimits.MaxStrokeWidth"/>。</exception>
    /// <exception cref="InvalidFillRuleException">填充规则不是已定义值。</exception>
    public void Validate()
    {
        if (double.IsNaN(StrokeWidth) || double.IsInfinity(StrokeWidth))
        {
            throw new InvalidStrokeWidthException(
                $"线宽必须是有限值(NaN 与 ±Infinity 均不接受),实际 {StrokeWidth}。");
        }

        if (StrokeWidth <= 0)
        {
            throw new InvalidStrokeWidthException($"线宽必须大于 0,实际 {StrokeWidth}。");
        }

        if (StrokeWidth > DrawingLimits.MaxStrokeWidth)
        {
            throw new InvalidStrokeWidthException(
                $"线宽不得超过 {DrawingLimits.MaxStrokeWidth},实际 {StrokeWidth}。");
        }

        if (FillRule is not (FillRule.NonZero or FillRule.EvenOdd))
        {
            throw new InvalidFillRuleException(
                $"填充规则只能是 nonzero(0)或 evenodd(1),实际 {(int)FillRule}。");
        }
    }
}
