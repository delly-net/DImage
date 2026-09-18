using System.Text;
using DImage.Api.Imaging.Glyphs;

namespace DImage.Api.Imaging;

/// <summary>
/// 一次文本排版的产物。
/// </summary>
/// <remarks>
/// 除折线本身外的计数项<b>不进入对外契约</b>(MCP 工具仍只返回 <c>{ok,id,covered}</c>),
/// 它们的存在是为了让「行宽」「缺字数」这类量可以被断言,而不必靠反推像素。
/// </remarks>
/// <param name="Figures">全部折线子路径:每个字形笔画一条,<b>整段文本合成为一个集合</b>。</param>
/// <param name="MaxLineWidthPixels">最长一行的宽度(像素),含字距与末尾字距。</param>
/// <param name="GlyphCount">累计字形数(含缺字占位)。</param>
/// <param name="PointCount">累计折线点数。</param>
/// <param name="MissingGlyphCount">缺字(走了占位方框)的数量。</param>
public sealed record TextLayoutResult(
    IReadOnlyList<PathFigure> Figures,
    double MaxLineWidthPixels,
    int GlyphCount,
    int PointCount,
    int MissingGlyphCount);

/// <summary>
/// 文本排版:把「文本 + 字号 + 字距 + 行距 + 锚点 + 仿射矩阵」变成折线子路径。
/// </summary>
/// <remarks>
/// <para>
/// <b>整段文本合成一个 <see cref="Shape"/>,是本模块最要紧的一条约束。</b>
/// 若改成「每个字形各调一次 <see cref="ImageDraw.Draw"/>,笔画交叉处会被混合两次,
/// 得到 <c>1 − (1 − c)² &gt; c</c> 的加深结果 —— 汉字的撇捺交叉点会出现暗斑。
/// 图能出、结构对、<b>只有颜色不对</b>,与任务 10 记录的那类静默错误同源。
/// 因此本类型的输出是<b>一个</b>折线集合,交给光栅化器<b>一次</b>完成覆盖率累积与混合。
/// </para>
/// <para>
/// <b>本类型是纯函数,不碰像素、不持有 <see cref="ImageBuffer"/>、不读配置。</b>
/// 输入全是值,输出全是值 —— 于是它可以被独立断言,而不必先造一张图。
/// </para>
/// <para>
/// <b>坐标系转换链只有一条,顺序固定:</b>
/// <c>字形 em 坐标 → (加笔位) → 乘 size/1024 得像素 → 左乘仿射矩阵</c>。
/// 于是平移与旋转<b>绕文本起点发生</b>、<c>e</c> / <c>f</c> 承担最终平移,
/// 而 <c>x</c> / <c>y</c> 参数承担锚点定位 —— 三者的分工因此不重叠。
/// </para>
/// <para>
/// <b>上限在排版过程中增量计数并即时中断</b>(沿 <see cref="DrawingLimits"/> 的既有立场):
/// 先排完再检查等于先让调用方把 CPU 耗完,再把「超限」告诉它。
/// </para>
/// </remarks>
public static class TextLayout
{
    /// <summary><c>\t</c> 展开成的空格数。</summary>
    private const int TabWidth = 4;

    /// <summary>排版一段文本。调用方须先执行 <see cref="TextShape.Validate"/>。</summary>
    /// <param name="shape">文本形状(纯值)。</param>
    /// <returns>折线子路径与若干计数。</returns>
    /// <exception cref="DrawingLimitExceededException">字形数或累计点数超限(在排版过程中即时抛出)。</exception>
    public static TextLayoutResult Flatten(TextShape shape)
    {
        string[] lines = Normalize(shape.Text).Split('\n');

        // em → 像素的比例。字号即 1 em 的像素高度,故 k = size / 1024
        double k = shape.Size / TextLimits.EmUnitsPerEm;
        double lineHeightPixels = shape.LineSpacing ?? (TextLimits.DefaultLineHeightRatio * shape.Size);

        var figures = new List<PathFigure>();
        var lineGlyphs = new List<StrokeGlyph>();

        double maxLineWidthPixels = 0;
        int glyphCount = 0;
        int pointCount = 0;
        int missingGlyphCount = 0;

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            lineGlyphs.Clear();
            double lineWidthPixels = 0;

            foreach (int codePoint in EnumerateCodePoints(lines[lineIndex]))
            {
                bool found = shape.Font.TryGetGlyph(codePoint, out StrokeGlyph glyph);
                if (!found)
                {
                    glyph = shape.Font.Placeholder;
                    missingGlyphCount++;
                }

                lineGlyphs.Add(glyph);

                // 计数与判定同处一行,「超限即中断」才成立
                glyphCount++;
                if (glyphCount > TextLimits.MaxTextGlyphs)
                {
                    throw new DrawingLimitExceededException(
                        $"文本字形数超过上限 {TextLimits.MaxTextGlyphs}(第 {lineIndex + 1} 行起超限)。");
                }

                pointCount += glyph.PointCount;
                if (pointCount > TextLimits.MaxTextPoints)
                {
                    throw new DrawingLimitExceededException(
                        $"文本累计折线点数超过上限 {TextLimits.MaxTextPoints}(第 {lineIndex + 1} 行起超限)。");
                }

                if (glyph.StrokeCount > TextLimits.MaxGlyphStrokes)
                {
                    throw new DrawingLimitExceededException(
                        $"第 {lineIndex + 1} 行出现笔画数为 {glyph.StrokeCount} 的字形,超过单字上限 {TextLimits.MaxGlyphStrokes}。");
                }

                // 字距加在<b>每个字形之后,含末字形</b>:末字右侧的留白因此可预测,
                // 而不是「末字后没有字距」这种让行宽随字数奇偶变化的规则
                lineWidthPixels += glyph.Advance * k + shape.LetterSpacing;
            }

            double startXPixels = shape.X + (shape.Anchor switch
            {
                TextAnchor.Left => 0,
                TextAnchor.Center => -lineWidthPixels / 2,
                TextAnchor.Right => -lineWidthPixels,
                // 未定义取值必须立即失败,理由同 FillRuleExtensions:兜底会让文字静默偏位
                _ => throw new ArgumentOutOfRangeException(
                    nameof(shape), (int)shape.Anchor, $"未知的文本对齐方式({(int)shape.Anchor}),无法确定行起点。")
            });

            double baselineYPixels = shape.Y + (lineIndex * lineHeightPixels);
            EmitLine(figures, lineGlyphs, shape, k, startXPixels, baselineYPixels);

            maxLineWidthPixels = Math.Max(maxLineWidthPixels, lineWidthPixels);
        }

        return new TextLayoutResult(figures, maxLineWidthPixels, glyphCount, pointCount, missingGlyphCount);
    }

    /// <summary>把一行的字形笔画展开成折线子路径。</summary>
    private static void EmitLine(
        List<PathFigure> figures,
        List<StrokeGlyph> glyphs,
        TextShape shape,
        double k,
        double startXPixels,
        double baselineYPixels)
    {
        double penXPixels = startXPixels;

        foreach (StrokeGlyph glyph in glyphs)
        {
            foreach (PointD[] stroke in glyph.Strokes)
            {
                if (stroke.Length < 2)
                {
                    // 单个顶点的笔画无法构成线段。字库层已把此类笔画补成零长两点折线,
                    // 故这里是数据异常的兜底 —— 丢弃会比「补一个点」更难排查,选择报错
                    throw new InvalidGeometryException(
                        $"字形笔画只有 {stroke.Length} 个顶点,无法构成线段;字库数据可能已损坏。");
                }

                var points = new PointD[stroke.Length];
                for (int i = 0; i < stroke.Length; i++)
                {
                    var pixel = new PointD(penXPixels + (stroke[i].X * k), baselineYPixels + (stroke[i].Y * k));
                    points[i] = shape.Transform.IsIdentity ? pixel : shape.Transform.Apply(pixel);
                }

                // 矩阵分量合法但相乘后超量级的组合是存在的,故校验必须在<b>变换之后</b>:
                // 这是坐标进入包围盒循环前的最后一道闸
                PathGeometry.ValidatePoints(points, "文本笔画顶点");

                figures.Add(new PathFigure(points, isClosed: false));
            }

            penXPixels += (glyph.Advance * k) + shape.LetterSpacing;
        }
    }

    /// <summary>
    /// 归一化换行与制表符。
    /// </summary>
    /// <remarks>
    /// <c>\r\n</c> 与 <c>\r</c> 都归为 <c>\n</c>:调用方从 Windows 剪贴板取来的文本常带 <c>\r\n</c>,
    /// 不归一化会让每一行末尾多出一个缺字方框。制表符展开为固定 4 个空格而非制表位对齐 ——
    /// 单线笔画字库没有「制表位」这一概念,实现制表位需要一个与字号、锚点都相关的状态,
    /// 而收益仅是几个空格的宽度。
    /// </remarks>
    private static string Normalize(string text)
    {
        if (!text.Contains('\r', StringComparison.Ordinal) && !text.Contains('\t', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                builder.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else if (c == '\t')
            {
                builder.Append(' ', TabWidth);
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 按 Unicode 码点遍历一行文本。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不按 <c>char</c> 遍历</b>:emoji 等增补平面字符是代理对,按 <c>char</c> 遍历会把一个字符
    /// 拆成两个孤立的代理项,于是<b>一个字符画出两个豆腐块</b>。孤立代理项(非法 UTF-16)
    /// 用 <c>−1</c> 表示,必定走缺字路径。
    /// </remarks>
    private static IEnumerable<int> EnumerateCodePoints(string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (!char.IsSurrogate(c))
            {
                // 控制字符(含 <c>\0</c>、<c>\a</c> 等)没有字形,一律走缺字路径,
                // 而不是被当作可打印字符去查表 —— 查不到再走缺字,结果相同,但意图不明
                yield return char.IsControl(c) ? -1 : c;
            }
            else if (char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]))
            {
                yield return char.ConvertToUtf32(c, line[i + 1]);
                i++;
            }
            else
            {
                yield return -1;
            }
        }
    }
}
