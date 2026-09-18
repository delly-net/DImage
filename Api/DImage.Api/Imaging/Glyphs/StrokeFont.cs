namespace DImage.Api.Imaging.Glyphs;

/// <summary>
/// 单线笔画字库的唯一入口:把「Unicode 码点」映射为「字形」。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类型只做纯查询,不含任何排版逻辑。</b>换行、字距、行距、锚点、仿射变换全在
/// <see cref="TextLayout"/>;字符集的优先级(先汉字后拉丁)是字库自身的事实,故留在这里。
/// 把两者混在一处会让「新增一种字库」与「调整排版规则」互相牵动。
/// </para>
/// <para>
/// <b>缺字不返回失败,而是返回占位字形。</b>这是决策 #75 的既定选择:一段中英混排文本里
/// 夹一个中文标点是很常见的,若整段因一个字符报错,调用方唯一能做的处置是自行过滤字符 ——
/// 那等于把「哪些字符有字形」这份知识复制到了调用方。<b>缺字以 1 em 的方框占位,宽度不省略</b>,
/// 于是行宽与后续字符的位置仍然可预测。
/// </para>
/// <para>
/// <b>空白字形(空格)不是缺字。</b>它有零条笔画和一个正步进,走的是正常命中路径 ——
/// 若把它当缺字,每个空格都会画出一个豆腐块。
/// </para>
/// </remarks>
public sealed class StrokeFont
{
    /// <summary>默认字库:汉字(GB2312 一级)优先,其次拉丁(Hershey Roman Simplex)。</summary>
    /// <remarks>
    /// 无构造参数、无配置项:字库是随程序集发布的固定数据,不是运行时可调项。
    /// 留一个可构造的类型形态是为了让断言可以换入受控字库,而不是为了线上可配。
    /// </remarks>
    public static StrokeFont Default { get; } = new();

    /// <summary>构造字库。字库数据本身在首次取字时惰性加载。</summary>
    public StrokeFont()
    {
        Placeholder = BuildPlaceholder();
    }

    /// <summary>缺字占位字形(豆腐块):一个内缩的方框,步进 1 em。</summary>
    public StrokeGlyph Placeholder { get; }

    /// <summary>
    /// 取一个码点对应的字形。
    /// </summary>
    /// <param name="codePoint">Unicode 码点。</param>
    /// <param name="glyph">命中的字形;未命中时为 <c>null</c>。</param>
    /// <returns>是否命中字库。<b>返回 <c>false</c> 时调用方应改用 <see cref="Placeholder"/></b>,而不是报错。</returns>
    public bool TryGetGlyph(int codePoint, out StrokeGlyph glyph)
    {
        // 先汉字后拉丁:GB2312 一级字全在 U+4E00 以上,与 ASCII 无交集,
        // 故顺序不影响结果,只影响「未来若引入重叠区间时谁优先」—— 汉字优先是更符合
        // 「全角字符占一个 em」直觉的选择
        if (HanziGlyphs.TryGet(codePoint, out glyph))
        {
            return true;
        }

        return LatinGlyphs.TryGet(codePoint, out glyph);
    }

    /// <summary>汉字字库收录的码点数(供覆盖率断言)。</summary>
    public static int HanziGlyphCount => HanziGlyphs.Count;

    /// <summary>拉丁字库收录的码点数(供覆盖率断言)。</summary>
    public static int LatinGlyphCount => LatinGlyphs.Count;

    /// <summary>汉字字库收录的码点(升序,供覆盖率断言)。</summary>
    public static ReadOnlySpan<int> HanziCodePoints => HanziGlyphs.CodePoints;

    /// <summary>
    /// 构造缺字占位字形:汉字设计盒四边内缩后的方框。
    /// </summary>
    /// <remarks>
    /// 方框落在汉字设计盒(<see cref="TextLimits.HanziBoxTop"/> / <see cref="TextLimits.HanziBoxBottom"/>)
    /// 之内并四边内缩 <see cref="TextLimits.MissingGlyphInset"/>,于是它<b>不贴边、也不与相邻字粘连</b>,
    /// 视觉上明确区别于任何真实字形。四条边写成一条<b>手工会闭合的折线</b>(末点回到首点)——
    /// 字形笔画一律是「不闭合折线」,靠 <see cref="PathFigure.IsClosed"/> 闭合是形状层的事,
    /// 字库层不掺进去。
    /// </remarks>
    private static StrokeGlyph BuildPlaceholder()
    {
        int inset = TextLimits.MissingGlyphInset;
        double left = inset;
        double right = TextLimits.EmUnitsPerEm - inset;
        double bottom = TextLimits.HanziBoxBottom + inset;   // 屏幕 y 向下为正,故为方框的下沿
        double top = -TextLimits.HanziBoxTop + inset;        // 上沿取负

        PointD[] box =
        [
            new(left, top),
            new(right, top),
            new(right, bottom),
            new(left, bottom),
            new(left, top)
        ];

        return new StrokeGlyph([box], TextLimits.EmUnitsPerEm, isPlaceholder: true);
    }
}
