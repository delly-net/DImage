namespace DImage.Api.Imaging.Glyphs;

/// <summary>
/// 一个字形的单线笔画表示:若干条折线 + 一个步进宽度。
/// </summary>
/// <remarks>
/// <para>
/// <b>坐标系:笔画坐标以「笔位原点」为基准,单位是 em 网格刻度(<see cref="TextLimits.EmUnitsPerEm"/>
/// 分之一 em)。</b>所谓笔位原点,即「<c>penX = 0</c>、基线在 <c>y = 0</c>」那一点 ——
/// 于是排版层只需做 <c>笔位 + 字形内坐标</c> 一次加法,不必再关心字形的左边距,
/// 也不必关心两种字库各自的原始网格。
/// </para>
/// <para>
/// <c>y</c> <b>向下为正</b>,与 <see cref="PointD"/> 的全局约定一致:基线上方为负、下方为正。
/// 两套上游数据(y 方向相反)的翻转<b>全部在各自的字库加载器内完成</b>,
/// 字形对象本身不再携带任何来源侧的方向假设。
/// </para>
/// <para>
/// <b>刻意不提供 <c>Left</c>(左边距)。</b>归一化已把左边距折进坐标,再单列一个字段,
/// 它就必须恒等于「首笔的最小 x」—— 这是同一事实的第二个存储位置,一旦某次改动只更新一处,
/// 表现是字形整体右移若干像素,不报错。排版也不需要它:居中与右对齐按步进之和计算即可。
/// </para>
/// <para>
/// <b>字形是只读纯值:不持有 <see cref="ImageBuffer"/>、不注入服务、不读配置。</b>
/// 与 <see cref="Shape"/> 的约束同源 —— 底层数据对象不出 <see cref="ImageBufferStore"/> 的锁。
/// </para>
/// </remarks>
public sealed class StrokeGlyph
{
    private readonly PointD[][] _strokes;

    /// <summary>以笔画集合与步进构造字形。</summary>
    /// <param name="strokes">
    /// 笔画集合,每条为一串 em 单位顶点(至少 2 个,单点笔画须由调用方补成零长两点的退化折线)。
    /// 本对象持有该数组,调用方不应再修改它。
    /// </param>
    /// <param name="advance">步进宽度(em 单位),须为正;空白字形为「零笔画 + 正步进」。</param>
    /// <param name="isPlaceholder">是否为缺字占位字形(豆腐块)。</param>
    /// <exception cref="ArgumentNullException"><paramref name="strokes"/> 为 <c>null</c>。</exception>
    public StrokeGlyph(PointD[][] strokes, double advance, bool isPlaceholder = false)
    {
        ArgumentNullException.ThrowIfNull(strokes);

        _strokes = strokes;
        Advance = advance;
        IsPlaceholder = isPlaceholder;
    }

    /// <summary>笔画集合,每条笔画为一串 em 单位顶点。</summary>
    public IReadOnlyList<PointD[]> Strokes => _strokes;

    /// <summary>
    /// 步进宽度(em 单位)。
    /// </summary>
    /// <remarks>
    /// <b>空白字形(如空格)是「零笔画 + 正步进」,不是「没有字形」。</b>
    /// 若把空格当作缺字而走占位方框,一个空格就会画出一个豆腐块;
    /// 若把它的步进当 0,则所有含空格的行都会缩短。
    /// </remarks>
    public double Advance { get; }

    /// <summary>是否为缺字占位字形(豆腐块);仅用于诊断与断言,排版不据此分支。</summary>
    public bool IsPlaceholder { get; }

    /// <summary>笔画数,供 <see cref="TextLimits.MaxGlyphStrokes"/> 判定与断言使用。</summary>
    public int StrokeCount => _strokes.Length;

    /// <summary>顶点总数,供 <see cref="TextLimits.MaxTextPoints"/> 累计与断言使用。</summary>
    public int PointCount
    {
        get
        {
            int total = 0;
            foreach (PointD[] stroke in _strokes)
            {
                total += stroke.Length;
            }

            return total;
        }
    }
}
