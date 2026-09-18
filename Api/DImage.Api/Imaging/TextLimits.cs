namespace DImage.Api.Imaging;

/// <summary>
/// 文字绘制能力的上限与度量常量集中定义,与 <see cref="ImageLimits"/> /
/// <see cref="ImageRegistryLimits"/> / <see cref="DrawingLimits"/> 同属「一个能力域一个常量类」的粒度。
/// </summary>
/// <remarks>
/// <para>
/// <b>文字引入的是与 <see cref="DrawingLimits"/> 不同性质的一条放大链。</b>
/// <see cref="DrawingLimits.MaxPathTextLength"/> 约束的是调用方直接给出的 <c>d</c> 字符串;
/// 而文字的长度与「每个字符会展开出多少笔画与顶点」<b>都不受其约束</b> ——
/// 一段 4096 字的文本按每字 10 笔画 × 12 点计会产生约 49 万个点。两者相乘足以把 CPU 打满,
/// 而 <c>POST /mcp</c> 暴露在公网,故每一条都必须有独立上界。
/// </para>
/// <para>
/// <b>超限一律报错,绝不静默截断</b>(沿 <see cref="DrawingLimits"/> 的既有立场),
/// 且在<b>排版过程中增量计数、超限即中断</b>:先排完再检查等于先让请求方把 CPU 耗完。
/// </para>
/// </remarks>
public static class TextLimits
{
    /// <summary>
    /// em 网格的刻度数:1024 单位 = 1 em。
    /// </summary>
    /// <remarks>
    /// 取 1024 而非 1 或 1000,是为了与汉字字库数据自身的 1024×1024 网格一致 ——
    /// 于是汉字字形的归一化恒等(1 数据单位 = 1 em 单位),只有 Hershey 一侧需要换算。
    /// 换算只在字库层发生一次,排版层只见 em 单位,不再关心数据来源。
    /// </remarks>
    public const int EmUnitsPerEm = 1024;

    /// <summary>
    /// 文本字符数上限,4096(含换行符)。
    /// </summary>
    /// <remarks>
    /// 该上限必须在取字形之前就先判定:它是唯一一个「不需要任何字库数据即可判定」的上限,
    /// 因而也是唯一能在解压资源之前挡住恶意请求的上限。
    /// </remarks>
    public const int MaxTextLength = 4_096;

    /// <summary>
    /// 字形数上限,4096。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="MaxTextLength"/> 同量级但不相等:制表符会展开为 4 个空格、
    /// 缺字会替换为占位字形,故「字形数 ≥ 字符数」,需要各自独立设限。
    /// </remarks>
    public const int MaxTextGlyphs = 4_096;

    /// <summary>
    /// 整段文本累计折线点数上限,262144。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="DrawingLimits.MaxFlattenedSegments"/> 同量级,因为二者约束的是同一件事:
    /// 交给光栅化器的折线总规模。<b>单字笔画再多也挡不住「四千个字各二十笔」</b>,
    /// 故必须按整段文本累计而非逐字判定。
    /// </remarks>
    public const int MaxTextPoints = 262_144;

    /// <summary>
    /// 单个字形的笔画数上限,256。
    /// </summary>
    /// <remarks>
    /// 防御性上限:实测 GB2312 一级字的笔画数最大为 24、拉丁字最大为 5。
    /// 取 256 是留足余量后的「数据异常时快速失败」—— 字库资源损坏或解码错位会表现为
    /// 笔画数爆炸,该上限让它在解码当步就报错,而不是排完版才超总点数。
    /// </remarks>
    public const int MaxGlyphStrokes = 256;

    /// <summary>
    /// 未显式给出 <c>line_spacing</c> 时的行高倍率,1.2。
    /// </summary>
    /// <remarks>
    /// 1.2 em 是排版惯例的「单倍行距」。该常量的存在是为了让 <c>line_spacing</c> 得以用
    /// <b>可空类型</b>区分「未指定」与「显式指定 0」—— 用 <c>default 0</c> 当哨兵会让
    /// 「显式指定 0」退化为「整段文字的行全部叠在一起」,是一个不报错的静默错误。
    /// </remarks>
    public const double DefaultLineHeightRatio = 1.2;

    /// <summary>
    /// 字号上限,4096 像素(1 em 的像素高度)。
    /// </summary>
    /// <remarks>
    /// 取 <see cref="DrawingLimits.MaxStrokeWidth"/> 同量级:字号乘以 em 单位即为像素坐标,
    /// 而字符最远可离基线约 1.25 em(含下降部与括号),故 4096 的字号仍会把坐标推到
    /// <see cref="DrawingLimits.MaxCoordinateMagnitude"/> 之内。超出后由坐标量级校验兜底。
    /// </remarks>
    public const double MaxFontSize = 4_096;

    /// <summary>
    /// Hershey Roman Simplex 的大写高度,21 个 JHF 单位。
    /// </summary>
    /// <remarks>
    /// <b>该值由字库实测得出,不是设定值。</b>实测 'H' 的纵向跨度恰为 <c>[−12, 9]</c> 共 21 单位,
    /// 且 A–Z 中除 'Q'(尾部下探至 11)外全部取到同一跨度。归一化把大写高度映射为 1 em,
    /// 于是 <c>size</c> 参数在拉丁一侧精确等于大写字母的像素高度。
    /// </remarks>
    public const int LatinCapHeightJhfUnits = 21;

    /// <summary>
    /// Hershey Roman Simplex 的基线所在行,<c>y = 9</c>(JHF 单位)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>JHF 解码后的 y 是「向下为正」,基线在 <c>y = +9</c>,而不是 0。</b>
    /// 该结论由三条独立证据交叉确认,不能靠「通常 y 向上为正」的直觉推断:
    /// <list type="bullet">
    ///   <item><description>逗号 <c>,</c> 跨 <c>[7, 13]</c> —— 落在基线下方,是标点的正确姿态;</description></item>
    ///   <item><description>下划线 <c>_</c> 在 <c>y = 11</c> —— 位于基线之下 2 单位;</description></item>
    ///   <item><description>下降部字母 <c>g / j / p / q / y</c> 取到 <c>y = 16</c>,而 <c>o</c> 止于 9 ——
    ///   下降部必须比基线更「靠下」,若 y 向上为准则下降部反而高过大写字母,自相矛盾。</description></item>
    /// </list>
    /// 反证:若误按 y 向上为正归一化(取负),整段文字会<b>上下颠倒</b> ——
    /// 仍是一个可辨认的形状,不报错,只是镜像了。
    /// </para>
    /// </remarks>
    public const int LatinBaselineJhfUnits = 9;

    /// <summary>
    /// 汉字设计盒的上沿,<c>+900</c>(em 单位,基线之上为正)。
    /// </summary>
    /// <remarks>
    /// 汉字字库数据的原生网格为 <c>1024×1024</c>、y 向上为正、基线在 <c>y = 0</c>,
    /// 字身实际占据约为 <c>y ∈ [−124, 900]</c> —— 即基线下方 0.12 em、上方 0.88 em,
    /// 与「汉字底部略低于拉丁基线」的排版惯例天然吻合,故<b>无需额外基线补偿</b>。
    /// 该设计盒同时用作缺字占位方框的几何。
    /// </remarks>
    public const int HanziBoxTop = 900;

    /// <summary>
    /// 汉字设计盒的下沿,<c>−124</c>(em 单位,基线之下为负)。
    /// </summary>
    /// <remarks>即 0.12 em 的下降量,见 <see cref="HanziBoxTop"/>。</remarks>
    public const int HanziBoxBottom = -124;

    /// <summary>
    /// 缺字占位方框相对汉字设计盒的内缩量(em 单位)。
    /// </summary>
    /// <remarks>
    /// 占位方框沿用汉字设计盒并四边内缩,使「豆腐块」在视觉上不贴边、也不与相邻字粘连。
    /// <b>占位字形的步进恒为 1 em</b> —— 缺字既不报错也<b>不省略宽度</b>,
    /// 否则整行文字的后续字位会左移,行宽随之缩短,而调用方无从察觉。
    /// </remarks>
    public const int MissingGlyphInset = 124;
}
