namespace DImage.Api.Imaging;

/// <summary>
/// 把 <see cref="SvgPathParser"/> 产出的命令序列扁平化为折线子路径。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有这一层</b>:抗锯齿、填充规则、裁剪都只需要一种几何输入 —— 折线。
/// 若让贝塞尔曲线与椭圆弧直接进入光栅化器,「填充」与「描边」就得各自再实现一遍曲线求交与距离场,
/// 且两条路径的正确性判据会彼此分叉。全部压成折线后,光栅化器只面对「线段集合 + 闭合标志」。
/// </para>
/// <para>
/// <b>细分是自适应的</b>:按「控制点到弦的最大距离小于容差」递归折半,而不是按固定段数采样。
/// 固定段数对短曲线浪费、对长曲线不足,且段数一固定就失去了「容差」这一可验收的语义。
/// 容差取 <see cref="DrawingLimits.BezierFlatnessTolerance"/>,远小于抗锯齿的 1 像素过渡带,
/// 故「折线逼近」不会改变覆盖率的结果。
/// </para>
/// <para>
/// <b>所有递归都有深度上限</b>(<see cref="DrawingLimits.MaxSubdivisionDepth"/>):
/// 容差判据在控制点退化时可能迟迟不成立(如三点几乎共线但被浮点噪声判为不平),
/// 而递归分叉是指数级的。深度上限把最坏情况钉死,再由
/// <see cref="DrawingLimits.MaxFlattenedSegments"/> 的总预算收口 —— 两道防线缺一不可:
/// 前者挡住单条曲线的深度爆炸,后者挡住「一万条曲线各细分一千段」的横向放大。
/// </para>
/// </remarks>
internal static class PathFlattener
{
    /// <summary>扁平化命令序列。</summary>
    /// <param name="commands">绝对坐标的命令序列。</param>
    /// <param name="tolerance">曲线逼近的容差(像素)。</param>
    /// <returns>折线子路径集合;退化到不足以构成线段的子路径被丢弃。</returns>
    /// <exception cref="PathSyntaxException">命令序列缺少起笔命令。</exception>
    /// <exception cref="DrawingLimitExceededException">扁平化后的线段总数超过上限。</exception>
    /// <exception cref="InvalidGeometryException">采样点非有限或超出量级上限。</exception>
    internal static List<PathFigure> Flatten(IReadOnlyList<SvgPathCommand> commands, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(commands);

        var figures = new List<PathFigure>();
        var points = new List<PointD>();
        var budget = new SegmentBudget(DrawingLimits.MaxFlattenedSegments);

        PointD current = default;
        PointD subpathStart = default;

        // 平滑命令(S/T)的隐含控制点来自「前一曲线控制点的镜像」,故必须跨命令保持这两个状态。
        // 用 null 而非「上一个点」表示「前一命令不是对应曲线」,是为了让 SVG 的
        // 「否则取当前点」这条规则显式落在取值处,而不是靠一个碰巧相等的坐标隐式成立。
        PointD? cubicControl = null;
        PointD? quadControl = null;

        bool hasCurrent = false;

        // 收束当前累积的顶点:闭合时去掉与首点重复的末点 ——
        // PathFigure 的闭合语义本就包含「末顶点回到首顶点」,重复书写会多出一条零长边
        void Flush(bool closed)
        {
            if (points.Count >= 2)
            {
                if (closed && points[^1] == points[0])
                {
                    points.RemoveAt(points.Count - 1);
                }

                if (points.Count >= 2)
                {
                    figures.Add(new PathFigure(points.ToArray(), closed));
                }
            }

            points.Clear();
        }

        void Require()
        {
            if (!hasCurrent)
            {
                throw new PathSyntaxException("path 命令出现在起笔命令 M/m 之前:路径必须先从一次「移动到」开始。");
            }
        }

        foreach (SvgPathCommand command in commands)
        {
            switch (command)
            {
                case SvgMoveTo move:
                    // 新的起笔点意味着上一条子路径到此为止(且不闭合)
                    Flush(closed: false);
                    current = move.Target;
                    subpathStart = current;
                    hasCurrent = true;
                    points.Add(current);
                    cubicControl = null;
                    quadControl = null;
                    break;

                case SvgLineTo line:
                    Require();
                    current = line.Target;
                    points.Add(current);
                    cubicControl = null;
                    quadControl = null;
                    break;

                case SvgCubicTo cubic:
                    Require();
                    AppendCubic(points, current, cubic.Control1, cubic.Control2, cubic.Target, tolerance, budget);
                    current = cubic.Target;
                    cubicControl = cubic.Control2;
                    quadControl = null;
                    break;

                case SvgSmoothCubicTo smooth:
                {
                    Require();

                    // 前一命令是 C/S 时,第一控制点取前一第二控制点关于当前点的镜像;
                    // 否则(前一命令不是三次曲线)隐含控制点就是当前点,曲线退化为从当前点出发的形状
                    PointD control1 = cubicControl is { } previous ? Reflect(previous, current) : current;
                    AppendCubic(points, current, control1, smooth.Control2, smooth.Target, tolerance, budget);
                    current = smooth.Target;
                    cubicControl = smooth.Control2;
                    quadControl = null;
                    break;
                }

                case SvgQuadTo quad:
                    Require();
                    AppendQuad(points, current, quad.Control, quad.Target, tolerance, budget);
                    current = quad.Target;
                    quadControl = quad.Control;
                    cubicControl = null;
                    break;

                case SvgSmoothQuadTo smooth:
                {
                    Require();
                    PointD control = quadControl is { } previous ? Reflect(previous, current) : current;
                    AppendQuad(points, current, control, smooth.Target, tolerance, budget);
                    current = smooth.Target;
                    quadControl = control;
                    cubicControl = null;
                    break;
                }

                case SvgArcTo arc:
                    Require();
                    AppendArc(points, current, arc, tolerance, budget);
                    current = arc.Target;
                    cubicControl = null;
                    quadControl = null;
                    break;

                case SvgClose:
                    Require();
                    Flush(closed: true);

                    // 闭合后笔位回到子路径起点;若后面还有命令,它们从起点继续画一条新子路径
                    current = subpathStart;
                    points.Add(current);
                    cubicControl = null;
                    quadControl = null;
                    break;

                default:
                    throw new PathSyntaxException($"未支持的 path 命令类型 {command.GetType().Name}。");
            }
        }

        Flush(closed: false);
        return figures;
    }

    /// <summary>
    /// 求一段圆弧(或椭圆弧)按给定容差采样所需的段数。
    /// </summary>
    /// <remarks>
    /// 依据是「弦与弧的最大偏差」:<c>r * (1 - cos(半角)) &lt;= 容差</c>,
    /// 反解出单段可覆盖的圆心角,再拿总扫过角去除。半径小于容差时退化为极粗的采样
    /// (整圆两段),这与「半径本身已小于一个像素的容差」的事实一致 —— 再多段也不会有视觉差异。
    /// 返回值恒落在 <c>[1, MaxArcSegments]</c>。
    /// </remarks>
    /// <param name="maxRadius">该弧的最大半径(<c>max(rx, ry)</c>)。</param>
    /// <param name="tolerance">逼近容差(像素)。</param>
    /// <param name="sweepRadians">扫过的圆心角(弧度),取绝对值参与计算。</param>
    internal static int StepsForSweep(double maxRadius, double tolerance, double sweepRadians)
    {
        double ratio = Math.Clamp(1.0 - tolerance / Math.Max(maxRadius, tolerance), -1.0, 1.0);
        double step = 2.0 * Math.Acos(ratio);

        if (double.IsNaN(step) || step <= 0)
        {
            step = Math.PI / 4.0;
        }

        int steps = (int)Math.Ceiling(Math.Abs(sweepRadians) / step);
        return Math.Clamp(steps, 1, DrawingLimits.MaxArcSegments);
    }

    /// <summary>求点 <paramref name="point"/> 关于 <paramref name="center"/> 的镜像点。</summary>
    private static PointD Reflect(PointD point, PointD center)
        => new(2 * center.X - point.X, 2 * center.Y - point.Y);

    /// <summary>求两点的中点。</summary>
    private static PointD Midpoint(PointD left, PointD right)
        => new((left.X + right.X) / 2.0, (left.Y + right.Y) / 2.0);

    /// <summary>三次贝塞尔的 de Casteljau 折半细分。</summary>
    private static void AppendCubic(
        List<PointD> points,
        PointD start,
        PointD control1,
        PointD control2,
        PointD end,
        double tolerance,
        SegmentBudget budget)
        => SubdivideCubic(points, start, control1, control2, end, tolerance, 0, budget);

    private static void SubdivideCubic(
        List<PointD> points,
        PointD p0,
        PointD p1,
        PointD p2,
        PointD p3,
        double tolerance,
        int depth,
        SegmentBudget budget)
    {
        if (depth >= DrawingLimits.MaxSubdivisionDepth
            || (PathGeometry.DistanceToLine(p1, p0, p3) <= tolerance
                && PathGeometry.DistanceToLine(p2, p0, p3) <= tolerance))
        {
            budget.Consume(1);
            points.Add(p3);
            return;
        }

        PointD p01 = Midpoint(p0, p1);
        PointD p12 = Midpoint(p1, p2);
        PointD p23 = Midpoint(p2, p3);
        PointD p012 = Midpoint(p01, p12);
        PointD p123 = Midpoint(p12, p23);
        PointD midpoint = Midpoint(p012, p123);

        SubdivideCubic(points, p0, p01, p012, midpoint, tolerance, depth + 1, budget);
        SubdivideCubic(points, midpoint, p123, p23, p3, tolerance, depth + 1, budget);
    }

    /// <summary>二次贝塞尔的 de Casteljau 折半细分。</summary>
    private static void AppendQuad(
        List<PointD> points,
        PointD start,
        PointD control,
        PointD end,
        double tolerance,
        SegmentBudget budget)
        => SubdivideQuad(points, start, control, end, tolerance, 0, budget);

    private static void SubdivideQuad(
        List<PointD> points,
        PointD p0,
        PointD p1,
        PointD p2,
        double tolerance,
        int depth,
        SegmentBudget budget)
    {
        if (depth >= DrawingLimits.MaxSubdivisionDepth
            || PathGeometry.DistanceToLine(p1, p0, p2) <= tolerance)
        {
            budget.Consume(1);
            points.Add(p2);
            return;
        }

        PointD p01 = Midpoint(p0, p1);
        PointD p12 = Midpoint(p1, p2);
        PointD midpoint = Midpoint(p01, p12);

        SubdivideQuad(points, p0, p01, midpoint, tolerance, depth + 1, budget);
        SubdivideQuad(points, midpoint, p12, p2, tolerance, depth + 1, budget);
    }

    /// <summary>
    /// 把椭圆弧的端点参数化转换为圆心参数化,并按容差采样为折线点。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 转换公式取自 SVG 规范附录 F.6.5。必须处理的分支(缺一条都会得到「画出来了但形状不对」):
    /// 起终点重合(规范规定该弧段<b>不绘制</b>,而不是画一整圈)、
    /// 半径有一个为 0(退化为直线)、
    /// 半径不足以连接两端点(按规范<b>等比放大</b>到恰好够用)、
    /// 以及大弧标志 / 扫描标志的四种组合(决定取哪个椭圆中心、沿哪个方向扫)。
    /// </para>
    /// <para>
    /// 采样末点被<b>精确覆盖为命令给出的终点</b>:浮点累积误差在单条弧上看不见,
    /// 但路径通常由几十条弧首尾相接,误差会让接缝处出现亚像素级错位,描边上表现为毛刺。
    /// </para>
    /// </remarks>
    private static void AppendArc(
        List<PointD> points,
        PointD start,
        SvgArcTo arc,
        double tolerance,
        SegmentBudget budget)
    {
        // 负半径按规范取其绝对值(负号被忽略)
        double rx = Math.Abs(arc.Rx);
        double ry = Math.Abs(arc.Ry);

        // 起终点重合:规范规定该弧段不绘制。此处刻意<b>不</b>当成整圆,那是另一种图形
        if (start == arc.Target)
        {
            return;
        }

        // 半径为 0:退化为一条直线
        if (rx == 0 || ry == 0)
        {
            budget.Consume(1);
            points.Add(arc.Target);
            return;
        }

        double phi = arc.RotationDegrees * Math.PI / 180.0;
        double cosPhi = Math.Cos(phi);
        double sinPhi = Math.Sin(phi);

        double halfDx = (start.X - arc.Target.X) / 2.0;
        double halfDy = (start.Y - arc.Target.Y) / 2.0;

        double x1Prime = cosPhi * halfDx + sinPhi * halfDy;
        double y1Prime = -sinPhi * halfDx + cosPhi * halfDy;

        double rxSquared = rx * rx;
        double rySquared = ry * ry;
        double x1PrimeSquared = x1Prime * x1Prime;
        double y1PrimeSquared = y1Prime * y1Prime;

        // 半径不足以连接两端点时等比放大到恰好够用(规范 F.6.6 第 3 步)
        double lambda = x1PrimeSquared / rxSquared + y1PrimeSquared / rySquared;
        if (lambda > 1)
        {
            double scale = Math.Sqrt(lambda);
            rx *= scale;
            ry *= scale;
            rxSquared = rx * rx;
            rySquared = ry * ry;
        }

        double denominator = rxSquared * y1PrimeSquared + rySquared * x1PrimeSquared;
        double numerator = rxSquared * rySquared - rxSquared * y1PrimeSquared - rySquared * x1PrimeSquared;
        double factor = denominator <= 0 ? 0 : Math.Sqrt(Math.Max(0, numerator / denominator));

        // 规范:大弧标志与扫描标志相同时取负号,即两个椭圆中心中取另一个
        if (arc.LargeArc == arc.Sweep)
        {
            factor = -factor;
        }

        double centerXPrime = factor * rx * y1Prime / ry;
        double centerYPrime = factor * -ry * x1Prime / rx;

        double centerX = cosPhi * centerXPrime - sinPhi * centerYPrime + (start.X + arc.Target.X) / 2.0;
        double centerY = sinPhi * centerXPrime + cosPhi * centerYPrime + (start.Y + arc.Target.Y) / 2.0;

        double ux = (x1Prime - centerXPrime) / rx;
        double uy = (y1Prime - centerYPrime) / ry;
        double vx = (-x1Prime - centerXPrime) / rx;
        double vy = (-y1Prime - centerYPrime) / ry;

        double startAngle = Math.Atan2(uy, ux);
        double sweepAngle = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);

        // 把扫过角规整到与扫描标志一致的方向上
        if (!arc.Sweep && sweepAngle > 0)
        {
            sweepAngle -= 2 * Math.PI;
        }
        else if (arc.Sweep && sweepAngle < 0)
        {
            sweepAngle += 2 * Math.PI;
        }

        int steps = StepsForSweep(Math.Max(rx, ry), tolerance, sweepAngle);
        budget.Consume(steps);

        for (int i = 1; i <= steps; i++)
        {
            double theta = startAngle + sweepAngle * i / steps;
            double cosTheta = Math.Cos(theta);
            double sinTheta = Math.Sin(theta);

            PointD point = new(
                centerX + rx * cosTheta * cosPhi - ry * sinTheta * sinPhi,
                centerY + rx * cosTheta * sinPhi + ry * sinTheta * cosPhi);

            // 采样点同样要进包围盒,故同样要过量级校验:半径放大后可能越出坐标上限
            PathGeometry.ValidatePoint(point, "圆弧采样点");
            points.Add(point);
        }

        // 末点以命令给出的精确终点覆盖,消除浮点累积误差
        points[^1] = arc.Target;
    }

    /// <summary>
    /// 扁平化线段总数的共享预算。
    /// </summary>
    /// <remarks>
    /// 预算是<b>整条路径共享</b>而非「每条曲线各自限额」:后者挡不住「一万条曲线各细分十段」
    /// 这种横向放大,而攻击者恰恰可以自由构造命令数量。
    /// </remarks>
    private sealed class SegmentBudget(int limit)
    {
        private int _used;

        /// <summary>记入若干线段;超限立即抛出,不静默截断。</summary>
        internal void Consume(int count)
        {
            _used += count;

            if (_used > limit)
            {
                throw new DrawingLimitExceededException(
                    $"扁平化后的线段总数超过上限 {limit}(已用 {_used} 段)。");
            }
        }
    }
}
