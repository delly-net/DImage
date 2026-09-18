using DImage.Api.Imaging.Glyphs;

namespace DImage.Api.Imaging;

/// <summary>
/// 文本形状:一段文字 + 字号 + 字距/行距 + 锚点 + 仿射变换。
/// </summary>
/// <remarks>
/// <para>
/// <b>整段文字是「一个形状」,尽管它的折线子路径有上千条。</b>若把它拆成多个形状分别绘制,
/// 笔画交叉处会被混合两次(覆盖率 <c>c</c> 两次落笔得到 <c>1 − (1 − c)² &gt; c</c>),
/// 汉字的撇捺交叉点会出现肉眼可见的暗斑 —— 图能画出来、结构也对,唯独颜色不对。
/// 故本类型<b>只暴露一个 <see cref="ToFigures"/></b>,把「一次绘制」这件事从类型形态上钉死。
/// </para>
/// <para>
/// <b>坐标系与字号<see cref="Size"/>的含义:</b>字号即 1 em 的像素高度。拉丁字形按
/// 「大写字母高度 = 1 em」归一化(故 <c>size</c> 与常见矢量字体工具的 <c>font-size</c> 口径接近),
/// 汉字按上游 <c>1024×1024</c> 网格恒等映射(故 <c>size</c> 是汉字的设计框边长)。
/// 两条基线都落在 <c>y</c> 上、<b>方向均为「向下为正」</b> —— 与 <see cref="PointD"/> 及屏幕坐标系一致。
/// </para>
/// <para>
/// <b>本类型是纯值输入</b>,与 <see cref="LineShape"/> / <see cref="PathShape"/> 同族:
/// 不持有 <see cref="ImageBuffer"/>、不读配置、不注入服务。<see cref="Font"/> 是一个纯查询对象
/// (字库数据随程序集发布),不是服务。
/// </para>
/// <para>
/// <b><see cref="Validate"/> 刻意只做低成本检查,这是与 <see cref="PathShape"/> 的一处有意分歧。</b>
/// <see cref="PathShape"/> 的 <c>Validate</c> 直接跑一遍解析,代价低且能被复用;
/// 而文本的排版要解码数千个字形,<b>同样的工作会被做两遍</b>。故此处把校验切成两段:
/// 参数(文本长度、字号、字距、行距、矩阵分量)在 <see cref="Validate"/> 中就地判掉,
/// 逐字形/逐点数的预算则在 <see cref="ToFigures"/> 的排版过程中<b>增量计数、超限即中断</b>。
/// 两段都在写入任何像素之前完成,故「参数非法时图像零改动」这条性质不因此松动。
/// </para>
/// </remarks>
public sealed class TextShape(
    string text,
    double x,
    double y,
    double size,
    double letterSpacing = 0,
    double? lineSpacing = null,
    TextAnchor anchor = TextAnchor.Left,
    AffineTransform? transform = null,
    StrokeFont? font = null) : Shape
{
    /// <summary>待绘制的文本。</summary>
    /// <remarks>
    /// 换行用 <c>\n</c>;<c>\r\n</c> 与孤立的 <c>\r</c> 会被归一化为 <c>\n</c>。
    /// 字库未收录的字符(中文标点、emoji、<c>^</c>、<c>`</c> 等)以豆腐块占位,<b>不报错</b>。
    /// </remarks>
    public string Text { get; } = text ?? throw new ArgumentNullException(nameof(text));

    /// <summary>文本原点的横坐标;具体锚在哪一侧由 <see cref="Anchor"/> 决定。</summary>
    public double X { get; } = x;

    /// <summary>文本原点的纵坐标,即<b>首行基线</b>的位置。</summary>
    public double Y { get; } = y;

    /// <summary>字号,即 1 em 的像素高度。</summary>
    public double Size { get; } = size;

    /// <summary>字距(像素),加在<b>每个字形之后(含末字形)</b>;可为负值以收紧字距。</summary>
    public double LetterSpacing { get; } = letterSpacing;

    /// <summary>行距(像素);为 <c>null</c> 时取 <see cref="TextLimits.DefaultLineHeightRatio"/> × <see cref="Size"/>。</summary>
    public double? LineSpacing { get; } = lineSpacing;

    /// <summary>水平对齐方式,<b>逐行</b>生效。</summary>
    public TextAnchor Anchor { get; } = anchor;

    /// <summary>施加在「像素坐标」之上的仿射变换,默认单位矩阵。</summary>
    /// <remarks>
    /// 变换发生在字号缩放<b>之后</b>、且绕文本原点进行,故 <c>e</c> / <c>f</c> 的平移量与
    /// <see cref="X"/> / <see cref="Y"/> 是<b>叠加</b>关系而非互斥关系。
    /// </remarks>
    public AffineTransform Transform { get; } = transform ?? AffineTransform.Identity;

    /// <summary>字库,默认 <see cref="StrokeFont.Default"/>。</summary>
    public StrokeFont Font { get; } = font ?? StrokeFont.Default;

    /// <summary>单行时的行高(像素),等于 <see cref="LineSpacing"/> 或默认行距。</summary>
    public double EffectiveLineSpacing => LineSpacing ?? (TextLimits.DefaultLineHeightRatio * Size);

    /// <inheritdoc/>
    /// <exception cref="DrawingLimitExceededException">文本长度或字号超限。</exception>
    /// <exception cref="InvalidGeometryException">原点/字号/字距/行距非法,或矩阵分量非法。</exception>
    public override void Validate()
    {
        if (Text.Length > TextLimits.MaxTextLength)
        {
            throw new DrawingLimitExceededException(
                $"文本长度 {Text.Length} 字符超过上限 {TextLimits.MaxTextLength}。");
        }

        PathGeometry.ValidatePoint(new PointD(X, Y), "文本原点");
        PathGeometry.ValidateCoordinate(Size, "字号");
        PathGeometry.ValidateCoordinate(LetterSpacing, "字距");
        Transform.Validate();

        // 用「不大于 0」而非「小于等于 0」表述,使 NaN 也落入拒绝分支(与 RectShape 同款理由)
        if (!(Size > 0))
        {
            throw new InvalidGeometryException($"字号必须大于 0,实际 {Size}。");
        }

        // 字号同时是坐标的缩放系数,故它受坐标量级上限约束的理由与 MaxStrokeWidth 一致
        if (Size > TextLimits.MaxFontSize)
        {
            throw new DrawingLimitExceededException(
                $"字号 {Size} 超过上限 {TextLimits.MaxFontSize}。");
        }

        if (LineSpacing is { } lineSpacing)
        {
            PathGeometry.ValidateCoordinate(lineSpacing, "行距");

            // 行距为 0 会让全部行叠在同一基线上、为负则让行序倒置,两者都只会是笔误
            if (!(lineSpacing > 0))
            {
                throw new InvalidGeometryException($"行距必须大于 0(省略则取默认行距),实际 {lineSpacing}。");
            }
        }

        // 未定义的枚举值必须在此失败,而不是等到排版时才发现「不知道锚在哪一侧」。
        // 调用方(工具层)也用它做入参白名单,故这一步是低成本且必要的
        _ = Anchor.ToWireName();
    }

    /// <inheritdoc/>
    /// <exception cref="DrawingLimitExceededException">字形数或累计折线点数超限。</exception>
    public override IReadOnlyList<PathFigure> ToFigures() => Layout().Figures;

    /// <summary>
    /// 执行一次完整排版,返回折线子路径与若干计数。
    /// </summary>
    /// <remarks>
    /// 这是 <see cref="ToFigures"/> 的实现,独立暴露是为了让「最长行宽」「缺字数」可被断言 ——
    /// 它们<b>不进入对外契约</b>(MCP 工具仍只返回 <c>{ok,id,covered}</c>),故只能从 C# 侧观察。
    /// </remarks>
    /// <returns>排版结果。</returns>
    public TextLayoutResult Layout() => TextLayout.Flatten(this);
}
