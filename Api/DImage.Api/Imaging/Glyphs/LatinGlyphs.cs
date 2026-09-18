using System.Globalization;
using System.Text;

namespace DImage.Api.Imaging.Glyphs;

/// <summary>
/// 拉丁字库:Hershey Roman Simplex(JHF 格式)的解码器。
/// </summary>
/// <remarks>
/// <para>
/// <b>数据与算法都照搬上游,不做任何「优化」。</b>JHF 记录的字段宽度是定长且无分隔符的,
/// 任意一处偏移差一位,当天就能得到一整套「看起来还行」的字形 —— 少一笔、多一条斜线、
/// 最后一个坐标被吃掉 —— 而不会有任何异常。<b>本类型的存在意义就是让这段解码只有一份实现。</b>
/// </para>
/// <para>
/// <b>记录格式</b>(逐字对应 <c>hershey@2.1.7/src/index.js</c> 的 <c>parseCharacterDescriptor</c>):
/// <list type="number">
///   <item><description>列 <c>0:5</c> —— 字符号(十进制);</description></item>
///   <item><description>列 <c>5:8</c> —— 顶点数,<b>该值含「左右手位」这一个伪顶点,故实际顶点数为它减一</b>;</description></item>
///   <item><description>列 <c>8</c> —— 左手位、列 <c>9</c> —— 右手位(即左右边距,两者之差决定步进);</description></item>
///   <item><description>其后每 2 个字符为一对坐标,<c>值 = 字符 − 'R'</c>;</description></item>
///   <item><description>坐标 <c>" R"</c>(即 <c>x = −50</c>、<c>y = 0</c>)是<b>抬笔哨兵</b>,表示开始新的一笔。</description></item>
/// </list>
/// 抬笔哨兵漏判会把两笔连成一条斜线;<b>哨兵值在字形外,不可当普通坐标</b>。
/// </para>
/// <para>
/// <b>y 轴方向是「向下为正」,基线在 <c>y = 9</c> 而非 0。</b>该结论由逗号/下划线/下降部字母
/// 三条独立证据交叉确认,详见 <see cref="TextLimits.LatinBaselineJhfUnits"/>。
/// 上游 JS 在放置时对 y 取负,那是为它自己的 y 向上渲染上下文服务的 ——
/// <b>本项目的 <see cref="PointD"/> 以 y 向下为正,故此处不取负</b>,
/// 只做「基线对齐 + 缩放」。照抄那个负号会让整段文字上下颠倒而依然是一个可辨认的形状。
/// </para>
/// <para>
/// <b>字形数据是逐字节等于上游的 <c>rowmans.jhf</c></b>,可用一条 <c>curl</c> 直接 diff 复核;
/// 「ASCII → 字符号」映射同样由生成脚本从上游 <c>characterNumbers.js</c> 解析得到,
/// <b>不按经验公式推算</b> —— 该字体的编号毫无规律(A–Z = 501–526、a–z = 601–626、
/// 数字 = 700–709、标点散落 710–2273),公式化推算会静默错位整段字母表。
/// </para>
/// </remarks>
internal static class LatinGlyphs
{
    /// <summary>内嵌的 JHF 字体文件名。</summary>
    private const string JhfFileName = "latin-rowmans.jhf";

    /// <summary>内嵌的「ASCII → 字符号」映射文件名。</summary>
    private const string MapFileName = "latin-ascii-map.txt";

    /// <summary>坐标值的基准字符:所有坐标都是「该字符 − 'R'」。</summary>
    private const char CoordinateBase = 'R';

    /// <summary>抬笔哨兵的 x 分量(即 <c>' ' − 'R'</c>)。</summary>
    private const int PenUpX = -50;

    /// <summary>抬笔哨兵的 y 分量(即 <c>'R' − 'R'</c>)。</summary>
    private const int PenUpY = 0;

    /// <summary>
    /// 校验「大写高度基准字形」时使用的字符。
    /// </summary>
    /// <remarks>
    /// 取 'H' 而非「A–Z 的整体跨度」:'H' 上下均为平头、无出锋,其跨度恰为大写高度;
    /// 而 A–Z 的整体跨度会被 'Q' 的尾部(下探 2 单位)污染,实测会得到 23 而非 21 ——
    /// 那会让全部字形统一缩小约 9%,字仍能画、只是整体偏小。
    /// </remarks>
    private const char MetricReferenceChar = 'H';

    private static readonly Lazy<Dictionary<int, StrokeGlyph>> Cache =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>取一个 ASCII 码点对应的字形。</summary>
    /// <param name="codePoint">Unicode 码点。</param>
    /// <param name="glyph">命中的字形;未命中时为 <c>null</c>。</param>
    /// <returns>是否命中。</returns>
    internal static bool TryGet(int codePoint, out StrokeGlyph glyph) => Cache.Value.TryGetValue(codePoint, out glyph!);

    /// <summary>已解码的 ASCII 字形数量,供覆盖率断言使用。</summary>
    internal static int Count => Cache.Value.Count;

    /// <summary>解码整套字库:解析 JHF 与映射表 → 定标 → 归一到 em 网格。</summary>
    private static Dictionary<int, StrokeGlyph> Load()
    {
        if (Cache.IsValueCreated)
        {
            // 不可达(Load 只在首次求值时执行一次),写在这里是为了标明该不变量
            return Cache.Value;
        }

        Dictionary<int, RawGlyph> raw = ParseJhf(ReadText(JhfFileName));
        Dictionary<int, int> map = ParseAsciiMap(ReadText(MapFileName));

        if (!map.TryGetValue(MetricReferenceChar, out int referenceNumber)
            || !raw.TryGetValue(referenceNumber, out RawGlyph? reference))
        {
            throw new InvalidOperationException(
                $"拉丁字库缺少度量基准字形 '{MetricReferenceChar}' —— 无法确定基线与大写高度。"
                + "该情况只可能源于字体文件或映射表被替换。");
        }

        int capTop = int.MaxValue;
        int baseline = int.MinValue;
        foreach (int[] stroke in reference.Strokes)
        {
            // 笔画坐标是交错的 [x, y, x, y, …],故步长为 2 而非 1:
            // 取奇数下标即全部的 y(此处只关心纵向范围)
            for (int i = 1; i < stroke.Length; i += 2)
            {
                capTop = Math.Min(capTop, stroke[i]);
                baseline = Math.Max(baseline, stroke[i]);
            }
        }

        int capHeight = baseline - capTop;
        if (baseline != TextLimits.LatinBaselineJhfUnits || capHeight != TextLimits.LatinCapHeightJhfUnits)
        {
            throw new InvalidOperationException(
                $"拉丁字库的度量基准与约定不符:'{MetricReferenceChar}' 实测基线 y={baseline}、大写高度={capHeight},"
                + $"而 <see cref=\"TextLimits\"/> 约定为 {TextLimits.LatinBaselineJhfUnits} 与 {TextLimits.LatinCapHeightJhfUnits}。"
                + "字体数据只能经 tools/glyphgen/generate.py 生成,请勿手工替换。");
        }

        // 大写高度 → 1 em。至此 size 参数在拉丁一侧精确等于大写字母的像素高度
        double scale = TextLimits.EmUnitsPerEm / (double)capHeight;
        var result = new Dictionary<int, StrokeGlyph>(map.Count);

        foreach ((int codePoint, int number) in map)
        {
            if (!raw.TryGetValue(number, out RawGlyph? glyph))
            {
                throw new InvalidOperationException(
                    $"映射表把码点 {codePoint}('{char.ConvertFromUtf32(codePoint)}')指向字符号 {number},"
                    + "但字体中没有该字符号。");
            }

            var strokes = new List<PointD[]>(glyph.Strokes.Count);
            foreach (int[] stroke in glyph.Strokes)
            {
                var points = new PointD[stroke.Length / 2];
                for (int i = 0; i < points.Length; i++)
                {
                    // 交错坐标:[x, y, x, y, …],第 i 个点是 (stroke[2i], stroke[2i+1])
                    points[i] = new PointD(
                        (stroke[i * 2] - glyph.Left) * scale,
                        (stroke[(i * 2) + 1] - baseline) * scale);
                }

                strokes.Add(NormalizeSinglePoint(points));
            }

            result[codePoint] = new StrokeGlyph(
                [.. strokes],
                (glyph.Right - glyph.Left) * scale);
        }

        return result;
    }

    /// <summary>
    /// 把「只有一个顶点的笔画」补成零长两点的退化折线。
    /// </summary>
    /// <remarks>
    /// 孤点无法构成线段,但它在字形里是**有意义的**:句点、间隔号就是「落笔的一个点」。
    /// 补成零长线段后,光栅化器的「点到线段的最短距离」会退化为「到该点的距离」,
    /// 再配合 round cap,该笔画自然呈现为一个直径等于线宽的圆点 —— 正是原意。
    /// 丢弃它则句点直接消失,而文字仍然排得整齐,是个不报错的缺笔。
    /// </remarks>
    private static PointD[] NormalizeSinglePoint(PointD[] points)
        => points.Length == 1 ? [points[0], points[0]] : points;

    /// <summary>读取一个内嵌文本资源(ASCII/UTF-8,按 UTF-8 解码以兼容映射表的注释)。</summary>
    private static string ReadText(string fileName)
    {
        byte[] bytes = EmbeddedResources.ReadAllBytes(fileName);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes);
    }

    /// <summary>解析 JHF 全文;每行一条记录。</summary>
    private static Dictionary<int, RawGlyph> ParseJhf(string text)
    {
        var result = new Dictionary<int, RawGlyph>();

        foreach (string rawLine in text.Split('\n'))
        {
            // 只裁掉行尾的 '\r':定长字段中首尾的空格<b>可能是数据</b>(空格字符本身是 −50),
            // 用 Trim() 会连数据一起裁掉
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            RawGlyph glyph = ParseJhfLine(line);
            result[glyph.Number] = glyph;
        }

        return result;
    }

    /// <summary>解析单条 JHF 记录。</summary>
    private static RawGlyph ParseJhfLine(string line)
    {
        const int headerLength = 10;

        if (line.Length < headerLength)
        {
            throw new InvalidOperationException($"JHF 记录长度不足 {headerLength} 字符:{Describe(line)}");
        }

        int number = ParseDigits(line.AsSpan(0, 5), "字符号");
        int declaredVertices = ParseDigits(line.AsSpan(5, 3), "顶点数");

        // 上游此处为「声明的顶点数 − 1」:那一个不产生坐标的伪顶点是记录尾部的左右手位
        int coordinateCount = declaredVertices - 1;
        int required = headerLength + coordinateCount * 2;
        if (coordinateCount < 0 || line.Length < required)
        {
            throw new InvalidOperationException(
                $"JHF 记录字符号 {number} 声明 {declaredVertices} 个顶点,需要 {required} 字符,实际 {line.Length} 字符:{Describe(line)}");
        }

        int left = line[8] - CoordinateBase;
        int right = line[9] - CoordinateBase;

        var strokes = new List<int[]>();
        var current = new List<int>();

        for (int i = 0; i < coordinateCount; i++)
        {
            int x = line[headerLength + i * 2] - CoordinateBase;
            int y = line[headerLength + i * 2 + 1] - CoordinateBase;

            if (x == PenUpX && y == PenUpY)
            {
                if (current.Count > 0)
                {
                    strokes.Add([.. current]);
                    current.Clear();
                }
            }
            else
            {
                current.Add(x);
                current.Add(y);
            }
        }

        if (current.Count > 0)
        {
            strokes.Add([.. current]);
        }

        return new RawGlyph(number, left, right, strokes);
    }

    /// <summary>解析「ASCII → 字符号」映射表:每行 <c>十进制码点=字符号</c>,<c>#</c> 起始为注释。</summary>
    private static Dictionary<int, int> ParseAsciiMap(string text)
    {
        var result = new Dictionary<int, int>();

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split('=');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int codePoint)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                throw new InvalidOperationException($"ASCII 映射表行无法解析(期望「码点=字符号」):{Describe(line)}");
            }

            result[codePoint] = number;
        }

        return result;
    }

    /// <summary>按十进制解析一段定长数字。</summary>
    /// <remarks>
    /// <b>定长字段是空格右对齐的,解析前必须去掉两端空白。</b>
    /// rowmans.jhf 的 <b>全部 96 条</b>记录都如此:字符号占 5 列(如 <c>"  699"</c>)、
    /// 顶点数占 3 列(如 <c>"  1"</c>),两者都是 <c>%5d%3d</c> 式的空格填充。
    /// 若要求「整段全是数字」,则一条记录都读不进来 —— 而这也正是上游
    /// <c>parseInt(descriptor[0:5])</c> 的实际行为(它会跳过前导空白)。
    /// </remarks>
    private static int ParseDigits(ReadOnlySpan<char> span, string field)
        => int.TryParse(span.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new InvalidOperationException($"JHF 记录的{field}字段不是十进制数字:{Describe(span.ToString())}");

    /// <summary>把内容渲染成便于定位的形态,避免异常消息里出现空白或不可见字符。</summary>
    private static string Describe(string value) => "\"" + value.Replace("\r", "\\r", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// 解码后的原始字形:仍是 JHF 的整数坐标与 y 向上/向下的原生约定,尚未归一化。
    /// </summary>
    /// <param name="Number">字符号。</param>
    /// <param name="Left">左手位。</param>
    /// <param name="Right">右手位。</param>
    /// <param name="Strokes">每条笔画的 <c>[x, y, x, y, …]</c> 交错坐标。</param>
    private sealed record RawGlyph(int Number, int Left, int Right, List<int[]> Strokes);
}
