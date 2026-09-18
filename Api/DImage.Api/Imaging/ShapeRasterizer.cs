namespace DImage.Api.Imaging;

/// <summary>
/// 统一的光栅化器:把折线子路径按给定的 <see cref="DrawStyle"/> 落到 <see cref="ImageBuffer"/> 上。
/// </summary>
/// <remarks>
/// <para>
/// <b>两类覆盖率来源</b>:
/// <list type="bullet">
///   <item><description><b>填充</b> —— 逐扫描线求与全部边的交点,按 <see cref="FillRule"/> 生成填充区间,
///   区间内部像素覆盖率为 1、边界像素按与区间的小数重叠给出部分覆盖率;</description></item>
///   <item><description><b>描边</b> —— 按解析距离场 <c>clamp(0.5 + 线宽/2 - 到折线的距离, 0, 1)</c> 给出覆盖率。</description></item>
/// </list>
/// 两者<b>累积到同一个缓冲</b>(先填充、后描边),最后每个 band 统一混合落笔一次。
/// </para>
/// <para>
/// <b>两条来源都受 <see cref="DrawStyle.Antialias"/> 支配</b>:关闭后一律退化为 0/1 判定
/// (描边看「距离是否在半线宽内」、填充看「像素中心是否落在区间内」)。两条都必须如此 ——
/// 只改一条会让「关闭抗锯齿」这个开关在半数几何上悄悄失效,而调用方无从察觉。
/// </para>
/// <para>
/// <b>为什么填充不逐像素做「点在多边形内」判定</b>:那种判定的结果只有 0 或 1,
/// <b>天然无法抗锯齿</b> —— 边缘永远是锯齿。扫描线求交则天然带着「边界的小数部分」这一信息,
/// 它正是抗锯齿需要的覆盖率。
/// </para>
/// <para>
/// <b>为什么描边用距离场而不是几何求交</b>:单一距离场就同时覆盖了直线、折线、多边形与路径,
/// 无需为每个顶点单独做连接补片。代价是端点与拐角呈现为<b>圆头</b>(round cap / round join),
/// 而非 butt cap / miter join —— <b>这是有意的选择,不是遗漏</b>:miter join 需要按顶点夹角
/// 做凸角补片并处理斜接长度上限,是一条完全独立的风险面;而本服务面向模型绘图调用,
/// 圆角观感不构成质量缺陷。请勿「顺手改成 miter」。
/// </para>
/// <para>
/// <b>像素中心约定</b>:像素 <c>(x, y)</c> 的中心在 <c>(x + 0.5, y + 0.5)</c>。
/// 该约定只在两处出现:扫描线的 <c>bandStart + row + 0.5</c>,与描边循环里的 <c>x + 0.5</c>。
/// </para>
/// </remarks>
internal static class ShapeRasterizer
{
    /// <summary>把折线子路径光栅化到图像上,返回被覆盖的像素数。</summary>
    /// <remarks>
    /// 调用方须保证 <paramref name="style"/> 与几何参数已通过校验:本方法不做参数校验,
    /// 也不产生任何「半成品」状态 —— 它要么按裁剪语义正常绘制,要么在几何进入循环边界之前就已失败。
    /// </remarks>
    /// <param name="image">目标缓冲区。</param>
    /// <param name="figures">折线子路径集合。</param>
    /// <param name="style">绘制样式(须已校验)。</param>
    /// <returns>被覆盖(覆盖率大于 0)的像素数。</returns>
    internal static int Rasterize(ImageBuffer image, IReadOnlyList<PathFigure> figures, DrawStyle style)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(figures);

        if (figures.Count == 0)
        {
            return 0;
        }

        BoundsD bounds = PathGeometry.GetBounds(figures);
        double halfWidth = style.StrokeWidth / 2.0;

        // 描边的覆盖率过渡带恰好 1 像素,故外扩「半线宽 + 1」即覆盖全部非零覆盖率;
        // 只填充时不外扩。包围盒在此之后才用于决定循环边界,而它已被校验为有限且有界。
        double expansion = style.Stroke ? halfWidth + 1.0 : 0.0;

        int firstRow = Math.Max(0, (int)Math.Floor(bounds.MinY - expansion));
        int lastRow = Math.Min(image.Height, (int)Math.Ceiling(bounds.MaxY + expansion));
        int firstColumn = Math.Max(0, (int)Math.Floor(bounds.MinX - expansion));
        int lastColumn = Math.Min(image.Width, (int)Math.Ceiling(bounds.MaxX + expansion));

        // 形状整体落在画布外:这是<b>裁剪</b>语义,不是错误 —— 直接返回 0 个被覆盖像素
        if (firstRow >= lastRow || firstColumn >= lastColumn)
        {
            return 0;
        }

        int bandHeight = Math.Min(DrawingLimits.CoverageBandHeight, image.Height);
        var coverage = new ScanlineCoverage(image.Width, bandHeight);
        int covered = 0;

        for (int bandStart = firstRow; bandStart < lastRow; bandStart += bandHeight)
        {
            int rows = Math.Min(bandHeight, lastRow - bandStart);

            // 缓冲在 band 之间复用,不重复分配(见扫描线覆盖率类型的分块说明)
            coverage.BeginBand(rows);

            if (style.Fill)
            {
                AccumulateFill(coverage, figures, style, bandStart, rows);
            }

            if (style.Stroke)
            {
                AccumulateStroke(coverage, figures, style, bandStart, rows);
            }

            covered += Blend(image, coverage, style, bandStart, rows, firstColumn, lastColumn);
        }

        return covered;
    }

    /// <summary>把 band 内累积的覆盖率混合落笔到图像,返回被覆盖的像素数。</summary>
    private static int Blend(
        ImageBuffer image,
        ScanlineCoverage coverage,
        DrawStyle style,
        int bandStart,
        int rows,
        int firstColumn,
        int lastColumn)
    {
        PixelColor foreground = style.EffectiveColor;
        int covered = 0;

        for (int row = 0; row < rows; row++)
        {
            int y = bandStart + row;

            for (int x = firstColumn; x < lastColumn; x++)
            {
                float value = coverage.Get(x, row);

                if (value <= 0f)
                {
                    continue;
                }

                covered++;

                // 复用 GetPixel / SetPixel:5 种格式的通道序与 Gray8 的 BT.601 折叠全部交给
                // ImageBuffer,此处零格式分支 —— 自行按格式换序正是红蓝反转这类静默错误的唯一来源。
                // 代价是 Gray8 下混合发生在折叠后的亮度上,连续半覆盖混合存在 8 位量化损失,
                // 且含 Alpha 格式的边缘像素 Alpha 会随之渐变(这是正确的抗锯齿表现,不是缺陷)。
                PixelColor destination = image.GetPixel(x, y);
                image.SetPixel(x, y, ImageBlend.Mix(destination, foreground, value > 1f ? 1f : value));
            }
        }

        return covered;
    }

    /// <summary>累积填充覆盖率。</summary>
    private static void AccumulateFill(
        ScanlineCoverage coverage,
        IReadOnlyList<PathFigure> figures,
        DrawStyle style,
        int bandStart,
        int rows)
    {
        int edgeCount = 0;
        foreach (PathFigure figure in figures)
        {
            edgeCount += figure.SegmentCount;
        }

        // 一次分配、逐行复用。不做池化:数量的上界由 MaxFlattenedSegments 约束,
        // 且每条扫描线实际用到的只是其中与该行相交的那一小部分,池化换来的收益不足以抵偿
        // 「租借数组可能带着脏数据」这一额外的推理负担。
        var crossings = new Crossing[Math.Max(edgeCount, 4)];

        for (int row = 0; row < rows; row++)
        {
            // 像素中心约定:第 y 行的扫描线取 y + 0.5
            double scanY = bandStart + row + 0.5;
            int count = 0;

            foreach (PathFigure figure in figures)
            {
                int segments = figure.SegmentCount;

                for (int segment = 0; segment < segments; segment++)
                {
                    PointD start = figure.GetSegmentStart(segment);
                    PointD end = figure.GetSegmentEnd(segment);

                    // 水平边与扫描线平行(或重合),不产生交点;重合边若被计入会让环绕数凭空翻倍
                    if (start.Y == end.Y)
                    {
                        continue;
                    }

                    double low = Math.Min(start.Y, end.Y);
                    double high = Math.Max(start.Y, end.Y);

                    // 半开区间 [low, high):顶点恰好落在扫描线上时只被一条边计入,
                    // 否则「上凸顶点」会被上下两条边各计一次,环绕数在顶点处瞬时翻倍并产生假区间
                    if (scanY < low || scanY >= high)
                    {
                        continue;
                    }

                    double t = (scanY - start.Y) / (end.Y - start.Y);

                    // 方向取自 y 的走向:向下为 +1、向上为 -1,nonzero 规则据此累计环绕数
                    crossings[count++] = new Crossing(
                        start.X + t * (end.X - start.X),
                        start.Y < end.Y ? 1 : -1);
                }
            }

            if (count < 2)
            {
                continue;
            }

            Array.Sort(crossings, 0, count);
            AccumulateScanline(coverage, crossings.AsSpan(0, count), row, style.FillRule, style.Antialias);
        }
    }

    /// <summary>把一条扫描线上的交点按填充规则转成填充区间。</summary>
    private static void AccumulateScanline(
        ScanlineCoverage coverage,
        ReadOnlySpan<Crossing> crossings,
        int row,
        FillRule fillRule,
        bool antialias)
    {
        int winding = 0;
        double? spanStart = null;

        for (int i = 0; i < crossings.Length; i++)
        {
            double x = crossings[i].X;
            int delta = 0;

            // 同一横坐标上的多个交点必须一次性处理:顶点处两条边同 x,
            // 逐个切换会先「离开」再「进入」,凭空产生一个零宽区间并污染覆盖率
            while (i < crossings.Length && crossings[i].X == x)
            {
                delta += crossings[i].Direction;
                i++;
            }

            i--;

            bool wasInside = IsInside(winding, fillRule);
            winding += delta;
            bool isInside = IsInside(winding, fillRule);

            if (!wasInside && isInside)
            {
                spanStart = x;
            }
            else if (wasInside && !isInside && spanStart is { } start)
            {
                AccumulateSpan(coverage, row, start, x, antialias);
                spanStart = null;
            }
        }
    }

    /// <summary>判定当前环绕数是否落在填充内部。</summary>
    /// <remarks>
    /// 两条规则共用同一个环绕数累计值:nonzero 看它是否非零,evenodd 看它的奇偶。
    /// 顶点处同横坐标的多个交点已先合成为 <c>delta</c>,故两种规则都不会因为「两个交点恰好同 x」
    /// 而产生与几何不符的结论。
    /// </remarks>
    private static bool IsInside(int winding, FillRule fillRule) => fillRule switch
    {
        FillRule.NonZero => winding != 0,
        FillRule.EvenOdd => (winding & 1) != 0,
        // 未定义值必须立即失败:静默兜底会让未知规则整幅图零覆盖率,且不产生任何错误信息
        _ => throw new InvalidFillRuleException(
            $"填充规则只能是 nonzero(0)或 evenodd(1),实际 {(int)fillRule}。")
    };

    /// <summary>把一个填充区间累积为覆盖率:抗锯齿按像素方框的重叠比例,硬边按像素中心的归属。</summary>
    /// <remarks>
    /// <para>
    /// 抗锯齿模式:区间<b>内部</b>的像素重叠比例为 1,<b>两端</b>的像素按小数部分给出部分覆盖率 ——
    /// 「边界像素部分覆盖」正是填充边缘得以抗锯齿的来源。
    /// </para>
    /// <para>
    /// 硬边模式<b>不能沿用重叠比例</b>:那会让 <c>antialias=false</c> 的填充边缘照样出现半覆盖像素,
    /// 于是「关闭抗锯齿后像素颜色非前景即背景」这一承诺只在描边上成立、在填充上失效,
    /// 而调用方无法从任何地方看出这个差别。故硬边改为「像素中心是否落在区间内」的 0/1 判定。
    /// 边界取<b>闭区间</b>,与描边的 <c>距离 &lt;= 半线宽</c> 同取闭边界 ——
    /// 否则同一几何在填充与描边两侧对边界像素的归属会不一致,而两者叠画时表现为一条细缝。
    /// </para>
    /// </remarks>
    private static void AccumulateSpan(
        ScanlineCoverage coverage,
        int row,
        double left,
        double right,
        bool antialias)
    {
        if (right <= left)
        {
            return;
        }

        // 先与画布求交再遍历:扫描线区间可以远在画布之外,
        // 让循环去跑那些必然被丢弃的像素是一条无谓的耗时路径
        int first = Math.Max((int)Math.Floor(left), 0);
        int last = Math.Min((int)Math.Ceiling(right), coverage.Width);

        for (int x = first; x < last; x++)
        {
            if (!antialias)
            {
                // 像素中心约定:x 列的中心在 x + 0.5
                double center = x + 0.5;

                if (center >= left && center <= right)
                {
                    coverage.Add(x, row, 1f);
                }

                continue;
            }

            double overlap = Math.Min(x + 1.0, right) - Math.Max(x, left);

            if (overlap <= 0)
            {
                continue;
            }

            coverage.Add(x, row, (float)(overlap > 1.0 ? 1.0 : overlap));
        }
    }

    /// <summary>累积描边覆盖率。</summary>
    /// <remarks>
    /// 每条线段只遍历「其包围盒膨胀半线宽加一个像素」与当前 band 的交集 ——
    /// 不做全画布遍历,否则一条短线段在 8192×8192 的画布上要跑 6700 万次距离计算。
    /// 膨胀量取 <c>半线宽 + 1</c>:覆盖率的过渡带恰为 1 像素宽,再远的像素覆盖率恒为 0。
    /// </remarks>
    private static void AccumulateStroke(
        ScanlineCoverage coverage,
        IReadOnlyList<PathFigure> figures,
        DrawStyle style,
        int bandStart,
        int rows)
    {
        double halfWidth = style.StrokeWidth / 2.0;
        double reach = halfWidth + 1.0;
        int bandEnd = bandStart + rows;

        foreach (PathFigure figure in figures)
        {
            int segments = figure.SegmentCount;

            for (int segment = 0; segment < segments; segment++)
            {
                PointD start = figure.GetSegmentStart(segment);
                PointD end = figure.GetSegmentEnd(segment);

                int rowFrom = Math.Max(bandStart, (int)Math.Floor(Math.Min(start.Y, end.Y) - reach));
                int rowTo = Math.Min(bandEnd, (int)Math.Ceiling(Math.Max(start.Y, end.Y) + reach) + 1);
                if (rowFrom >= rowTo)
                {
                    continue;
                }

                int columnFrom = Math.Max(0, (int)Math.Floor(Math.Min(start.X, end.X) - reach));
                int columnTo = Math.Min(coverage.Width, (int)Math.Ceiling(Math.Max(start.X, end.X) + reach) + 1);
                if (columnFrom >= columnTo)
                {
                    continue;
                }

                for (int y = rowFrom; y < rowTo; y++)
                {
                    double centerY = y + 0.5;
                    int row = y - bandStart;

                    for (int x = columnFrom; x < columnTo; x++)
                    {
                        double distance = PathGeometry.DistanceToSegment(new PointD(x + 0.5, centerY), start, end);

                        // 抗锯齿:覆盖率 = 半线宽 + 半个像素的过渡带 - 距离;
                        // 硬边模式:只按「是否落在半线宽内」取 0 或 1,不留任何中间值
                        float value = style.Antialias
                            ? (float)Math.Clamp(0.5 + halfWidth - distance, 0.0, 1.0)
                            : (distance <= halfWidth ? 1f : 0f);

                        if (value > 0f)
                        {
                            coverage.Add(x, row, value);
                        }
                    }
                }
            }
        }
    }

    /// <summary>扫描线与一条边的交点:横坐标 + 该边在交点处的走向。</summary>
    /// <param name="X">交点的横坐标。</param>
    /// <param name="Direction">边的走向:向下为 +1、向上为 -1。</param>
    private readonly record struct Crossing(double X, int Direction) : IComparable<Crossing>
    {
        /// <summary>按横坐标升序,供扫描线按从左到右的顺序消费。</summary>
        public int CompareTo(Crossing other) => X.CompareTo(other.X);
    }
}
