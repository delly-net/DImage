namespace DImage.Api.Imaging;

/// <summary>
/// 双精度二维点。
/// </summary>
/// <remarks>
/// <para>
/// <b>全层唯一的坐标约定:像素 <c>(x, y)</c> 的中心位于 <c>(x + 0.5, y + 0.5)</c>。</b>
/// 几何坐标一律按此约定与「像素方框 <c>[x, x+1] × [y, y+1]</c>」求交。
/// </para>
/// <para>
/// 该约定必须显式写下,不能靠默会:漏掉它会让整个抗锯齿结果<b>错位半个像素</b> ——
/// 图仍能画出来、肉眼近乎无感,但覆盖率断言全错,且极难归因到「少了 0.5」。
/// 因此坐标到像素的换算(<c>+ 0.5</c>)只在光栅化入口与扫描线求交处各写一次,
/// <b>不在 5 种图形里各写一遍</b>。
/// </para>
/// <para>
/// 使用 <c>double</c> 而非 <c>float</c>:画布边长可达 32768,单精度在该量级下的小数分辨率
/// 已不足以表达亚像素偏移(有效位数只剩约 3 位十进制),会让 AA 覆盖率出现可观测的抖动。
/// </para>
/// </remarks>
/// <param name="X">横坐标。可为亚像素值,也可落在画布外(越界是裁剪语义,不是错误)。</param>
/// <param name="Y">纵坐标。向下为正,与屏幕坐标系一致。</param>
public readonly record struct PointD(double X, double Y)
{
    /// <summary>坐标平移。</summary>
    public static PointD operator +(PointD left, PointD right) => new(left.X + right.X, left.Y + right.Y);

    /// <summary>坐标平移(反向)。</summary>
    public static PointD operator -(PointD left, PointD right) => new(left.X - right.X, left.Y - right.Y);

    /// <summary>坐标等比缩放。</summary>
    public static PointD operator *(PointD value, double factor) => new(value.X * factor, value.Y * factor);

    /// <summary>二维叉积 <c>a.X * b.Y - a.Y * b.X</c>,用于判定方向与共线。</summary>
    public static double Cross(PointD left, PointD right) => left.X * right.Y - left.Y * right.X;

    /// <summary>二维点积。</summary>
    public static double Dot(PointD left, PointD right) => left.X * right.X + left.Y * right.Y;
}

/// <summary>
/// 轴对齐包围盒,以 <c>double</c> 表示。
/// </summary>
/// <param name="MinX">最小横坐标。</param>
/// <param name="MinY">最小纵坐标。</param>
/// <param name="MaxX">最大横坐标。</param>
/// <param name="MaxY">最大纵坐标。</param>
public readonly record struct BoundsD(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>包围盒宽度。</summary>
    public double Width => MaxX - MinX;

    /// <summary>包围盒高度。</summary>
    public double Height => MaxY - MinY;
}

/// <summary>
/// 一条折线子路径:一串顶点,加上「是否闭合」。
/// </summary>
/// <remarks>
/// <para>
/// 这是算法层内部与光栅化器之间的唯一几何货币:5 种图形<b>全部</b>先转成折线子路径,再交给
/// 统一的光栅化器。形状只描述几何,像素的活一概由光栅化器承担 —— 于是「填充规则、抗锯齿、
/// 裁剪、band 循环」各只有一份实现,5 种图形自动共享它们。
/// </para>
/// <para>
/// <b>闭合语义</b>:闭合子路径的线段数为顶点数(最后一条边由末顶点回到首顶点),
/// 非闭合子路径为顶点数减一。因此<b>顶点列表不应重复首顶点</b>来「手工闭合」——
/// 那会多出一条零长边,并让线段计数出现难以察觉的偏差。
/// </para>
/// </remarks>
public sealed class PathFigure
{
    private readonly PointD[] _points;

    /// <summary>以顶点数组构造折线子路径。</summary>
    /// <param name="points">顶点数组,至少 2 个;本对象持有该数组,调用方不应再修改它。</param>
    /// <param name="isClosed">是否闭合(末顶点回到首顶点)。</param>
    /// <exception cref="ArgumentNullException"><paramref name="points"/> 为 <c>null</c>。</exception>
    /// <exception cref="InvalidGeometryException">顶点数少于 2。</exception>
    public PathFigure(PointD[] points, bool isClosed)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Length < 2)
        {
            throw new InvalidGeometryException(
                $"折线子路径至少需要 2 个顶点,实际 {points.Length} 个。");
        }

        _points = points;
        IsClosed = isClosed;
    }

    /// <summary>是否闭合(末顶点回到首顶点)。</summary>
    public bool IsClosed { get; }

    /// <summary>顶点数量。</summary>
    public int PointCount => _points.Length;

    /// <summary>顶点集合。</summary>
    public ReadOnlySpan<PointD> Points => _points;

    /// <summary>线段数量:闭合时等于顶点数,否则等于顶点数减一。</summary>
    public int SegmentCount => IsClosed ? _points.Length : _points.Length - 1;

    /// <summary>取第 <paramref name="segment"/> 条线段的起点。</summary>
    /// <param name="segment">线段序号,取值范围 <c>[0, SegmentCount)</c>。</param>
    public PointD GetSegmentStart(int segment) => _points[segment];

    /// <summary>取第 <paramref name="segment"/> 条线段的终点(闭合时末线段回到首顶点)。</summary>
    /// <param name="segment">线段序号,取值范围 <c>[0, SegmentCount)</c>。</param>
    public PointD GetSegmentEnd(int segment) => _points[(segment + 1) % _points.Length];
}

/// <summary>
/// 平面几何工具:坐标校验、点线距离与包围盒。
/// </summary>
/// <remarks>
/// 所有中间量一律以 <c>double</c> 计算。任何<b>将要进入循环边界或包围盒</b>的值,
/// 都必须先经 <see cref="ValidatePoint"/> 完成「有限性 + 量级」双重校验 ——
/// 这是执行规范中「几何中间量不得把 NaN / 无穷大 / 天文数字带进循环」的落地点。
/// </remarks>
public static class PathGeometry
{
    /// <summary>
    /// 校验一个坐标分量的<b>有限性</b>与<b>量级</b>。
    /// </summary>
    /// <remarks>
    /// 两道检查缺一不可,且都不能靠钳制代替:
    /// <list type="bullet">
    ///   <item><description><c>NaN</c> / <c>±Infinity</c> 会让所有比较恒为 <c>false</c>,包围盒循环要么空转要么不终止;</description></item>
    ///   <item><description>量级检查挡住的是「有限但天文数字」,它不会让循环空转,而是让它跑上一次不可能跑完的遍历。</description></item>
    /// </list>
    /// 越界(落在画布外)是<b>合法</b>的,由光栅化器裁剪,不在此处报错。
    /// </remarks>
    /// <param name="value">待校验的坐标分量。</param>
    /// <param name="name">字段名,用于错误消息定位。</param>
    /// <exception cref="InvalidGeometryException">值非有限,或绝对值超过 <see cref="DrawingLimits.MaxCoordinateMagnitude"/>。</exception>
    public static void ValidateCoordinate(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new InvalidGeometryException(
                $"坐标 {name} 必须是有限值(NaN 与 ±Infinity 均不接受),实际 {value}。");
        }

        if (Math.Abs(value) > DrawingLimits.MaxCoordinateMagnitude)
        {
            throw new InvalidGeometryException(
                $"坐标 {name} 的绝对值不得超过 {DrawingLimits.MaxCoordinateMagnitude},实际 {value}。");
        }
    }

    /// <summary>校验一个点的两个坐标分量。</summary>
    /// <param name="point">待校验的点。</param>
    /// <param name="name">点名,用于错误消息定位(错误消息中附加 <c>.x</c> / <c>.y</c>)。</param>
    /// <exception cref="InvalidGeometryException">任一分量非有限或超量级。</exception>
    public static void ValidatePoint(PointD point, string name)
    {
        ValidateCoordinate(point.X, name + ".x");
        ValidateCoordinate(point.Y, name + ".y");
    }

    /// <summary>校验整条折线子路径的每个顶点。</summary>
    /// <param name="points">顶点集合。</param>
    /// <param name="name">点集名,用于错误消息定位(错误消息中附加下标)。</param>
    /// <exception cref="InvalidGeometryException">任一顶点非法。</exception>
    public static void ValidatePoints(ReadOnlySpan<PointD> points, string name)
    {
        for (int i = 0; i < points.Length; i++)
        {
            ValidatePoint(points[i], $"{name}[{i}]");
        }
    }

    /// <summary>两点间距离。</summary>
    public static double Distance(PointD left, PointD right)
    {
        double dx = right.X - left.X;
        double dy = right.Y - left.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// 点到线段的最短距离(而非到其所在直线的距离)。
    /// </summary>
    /// <remarks>
    /// 投影参数被钳制到 <c>[0, 1]</c>,这正是描边端点呈现为<b>圆头</b>的来源:
    /// 端点处的覆盖率按「到端点的距离」衰减,等价于以端点为心画一个半径 <c>线宽 / 2</c> 的半圆。
    /// 零长线段(两端点重合)退化为到该点的距离。
    /// </remarks>
    /// <param name="point">被测量的点。</param>
    /// <param name="start">线段起点。</param>
    /// <param name="end">线段终点。</param>
    public static double DistanceToSegment(PointD point, PointD start, PointD end)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lengthSquared = dx * dx + dy * dy;

        if (lengthSquared <= 0)
        {
            return Distance(point, start);
        }

        double t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
        t = Math.Clamp(t, 0.0, 1.0);

        return Distance(point, new PointD(start.X + t * dx, start.Y + t * dy));
    }

    /// <summary>
    /// 点到<b>直线</b>的距离(不钳制投影),用于曲线细分的扁平度判据。
    /// </summary>
    /// <remarks>
    /// 两端点重合时「直线」退化,此时以到该点的距离作答 —— 否则会得到 <c>0/0 = NaN</c>,
    /// 而 <c>NaN &lt;= 容差</c> 恒为 <c>false</c>,细分将一路递归到深度上限才停下。
    /// </remarks>
    /// <param name="point">被测量的点。</param>
    /// <param name="start">直线上的第一点。</param>
    /// <param name="end">直线上的第二点。</param>
    public static double DistanceToLine(PointD point, PointD start, PointD end)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);

        if (length <= 0)
        {
            return Distance(point, start);
        }

        // 叉积的绝对值除以底边长即为点到直线的距离
        // (Cross 是 PointD 上的静态方法,本类型不继承它,故须写全限定名)
        return Math.Abs(PointD.Cross(new PointD(point.X - start.X, point.Y - start.Y), new PointD(dx, dy))) / length;
    }

    /// <summary>求一组折线子路径的轴对齐包围盒。</summary>
    /// <remarks>
    /// 包围盒以 <c>double</c> 计算,且要求调用方已通过 <see cref="ValidatePoint"/> 保证顶点有限 ——
    /// 含 <c>NaN</c> 的顶点会让 <c>Math.Min</c> / <c>Math.Max</c> 全部返回 <c>NaN</c>,
    /// 于是包围盒「不空但无意义」,后续的循环边界随之失去约束。
    /// 空的顶点集合返回全 0 的退化包围盒,由调用方据此直接判定「无像素可画」。
    /// </remarks>
    /// <param name="figures">子路径集合。</param>
    /// <exception cref="ArgumentNullException"><paramref name="figures"/> 为 <c>null</c>。</exception>
    public static BoundsD GetBounds(IReadOnlyList<PathFigure> figures)
    {
        ArgumentNullException.ThrowIfNull(figures);

        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;

        foreach (var figure in figures)
        {
            foreach (PointD point in figure.Points)
            {
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }
        }

        if (double.IsInfinity(minX))
        {
            return new BoundsD(0, 0, 0, 0);
        }

        return new BoundsD(minX, minY, maxX, maxY);
    }
}
