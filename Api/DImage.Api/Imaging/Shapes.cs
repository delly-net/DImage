namespace DImage.Api.Imaging;

/// <summary>
/// 可绘制形状的基类:只描述<b>几何</b>,不碰像素。
/// </summary>
/// <remarks>
/// <para>
/// <b>形状与光栅化分离</b>是这一层的核心分工:形状负责「参数校验 + 转成折线子路径」,
/// 像素的活全部交给 <see cref="ShapeRasterizer"/>。于是「填充规则、抗锯齿、裁剪、band 循环」
/// 各只有一份实现,5 种图形自动共享它们 —— 否则每加一种图形就要再写一遍这四件事,
/// 而任何一处写漏都只会表现为「某个图形看起来有点怪」。
/// </para>
/// <para>
/// <b>形状是纯值输入</b>:不持有 <see cref="ImageBuffer"/>、不读配置、不注入服务。
/// 这让注册表的绘制方法得以保持 <c>Draw(string id, Shape shape, DrawStyle style)</c> 这一形态,
/// <b>裸缓冲区不出注册表</b>的既定约束因此自动成立。
/// </para>
/// <para>
/// <b>调用约定</b>:<see cref="Validate"/> 必须先于 <see cref="ToFigures"/> 调用
/// (由 <see cref="ImageDraw.Draw"/> 统一保证)。<see cref="ToFigures"/> 假定参数已合法,
/// 它不做校验也不负责产生可读的参数错误。
/// </para>
/// <para>
/// <b>基类刻意不提供便利的</b><c>Draw</c><b>方法</b>:那会让形状反过来依赖光栅化器与缓冲区,
/// 把两个本可独立演进的层焊死在一处。
/// </para>
/// </remarks>
public abstract class Shape
{
    /// <summary>
    /// 校验几何参数。
    /// </summary>
    /// <remarks>
    /// 实现方须保证:校验失败时抛出异常,且<b>不产生任何副作用</b> —— 绘制路径依赖
    /// 「校验先于写入」来保证参数非法时图像零改动。
    /// </remarks>
    /// <exception cref="InvalidGeometryException">几何参数非法。</exception>
    public abstract void Validate();

    /// <summary>
    /// 把形状转换为折线子路径。
    /// </summary>
    /// <remarks>
    /// 只应在 <see cref="Validate"/> 通过之后调用。返回的集合可安全遍历,
    /// 调用方不应修改其中的子路径。
    /// </remarks>
    /// <returns>折线子路径集合;退化到不足以构成线段的形状返回空集合。</returns>
    public abstract IReadOnlyList<PathFigure> ToFigures();
}

/// <summary>
/// 直线或折线:2 个顶点即直线,n 个顶点即折线。
/// </summary>
/// <remarks>折线<b>不闭合</b>:末点不会自动回到首点。需要闭合请用 <see cref="PolygonShape"/>。</remarks>
public sealed class LineShape : Shape
{
    private readonly PointD[] _points;

    /// <summary>以顶点序列构造。</summary>
    /// <param name="points">顶点序列,至少 2 个。数组会被复制,构造后修改原数组不影响本对象。</param>
    /// <exception cref="ArgumentNullException"><paramref name="points"/> 为 <c>null</c>。</exception>
    public LineShape(params PointD[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        _points = (PointD[])points.Clone();
    }

    /// <summary>顶点数量。</summary>
    public int PointCount => _points.Length;

    /// <inheritdoc/>
    /// <exception cref="InvalidGeometryException">顶点少于 2 个,或存在非有限 / 超量级坐标。</exception>
    public override void Validate()
    {
        if (_points.Length < 2)
        {
            throw new InvalidGeometryException(
                $"直线/折线至少需要 2 个顶点(2 点即直线、n 点即折线),实际 {_points.Length} 个。");
        }

        PathGeometry.ValidatePoints(_points, "points");
    }

    /// <inheritdoc/>
    public override IReadOnlyList<PathFigure> ToFigures() => [new PathFigure(_points, isClosed: false)];
}

/// <summary>
/// 轴对齐矩形:左上角坐标加宽高。
/// </summary>
/// <remarks>
/// <b>不提供旋转</b>:旋转矩形属于仿射变换,是另一个独立的能力面(见本任务的「明确不做」清单)。
/// 需要任意四边形请用 <see cref="PolygonShape"/>。
/// </remarks>
public sealed class RectShape(double x, double y, double width, double height) : Shape
{
    /// <summary>左上角横坐标。</summary>
    public double X { get; } = x;

    /// <summary>左上角纵坐标。</summary>
    public double Y { get; } = y;

    /// <summary>宽度,须大于 0。</summary>
    public double Width { get; } = width;

    /// <summary>高度,须大于 0。</summary>
    public double Height { get; } = height;

    /// <inheritdoc/>
    /// <exception cref="InvalidGeometryException">宽高非正,或坐标非有限 / 超量级。</exception>
    public override void Validate()
    {
        PathGeometry.ValidatePoint(new PointD(X, Y), "矩形左上角");
        PathGeometry.ValidateCoordinate(Width, "矩形宽度");
        PathGeometry.ValidateCoordinate(Height, "矩形高度");

        // 用「不大于 0」而非「小于等于 0」表述,使 NaN 也落入拒绝分支(尽管上面已拦下 NaN)
        if (!(Width > 0))
        {
            throw new InvalidGeometryException($"矩形宽度必须大于 0,实际 {Width}。");
        }

        if (!(Height > 0))
        {
            throw new InvalidGeometryException($"矩形高度必须大于 0,实际 {Height}。");
        }

        // 右下角同样要过量级校验:宽高各自合法、相加后却越出上限的组合是存在的,
        // 而右下角才是真正进入包围盒的那个值
        PathGeometry.ValidateCoordinate(X + Width, "矩形右下角.x");
        PathGeometry.ValidateCoordinate(Y + Height, "矩形右下角.y");
    }

    /// <inheritdoc/>
    public override IReadOnlyList<PathFigure> ToFigures()
    {
        PointD[] corners =
        [
            new(X, Y),
            new(X + Width, Y),
            new(X + Width, Y + Height),
            new(X, Y + Height)
        ];

        return [new PathFigure(corners, isClosed: true)];
    }
}

/// <summary>
/// 椭圆;<c>Rx == Ry</c> 即正圆,给定起止角即为扇形。
/// </summary>
/// <remarks>
/// <para>
/// <b>角度约定</b>:单位为<b>度</b>,<c>0°</c> 指向 <c>+x</c> 轴,角度<b>增大方向为顺时针</b> ——
/// 与 y 轴向下的屏幕坐标系一致,与 Canvas / SVG 相同,而与数学课本的逆时针习惯<b>相反</b>。
/// 该约定极易写反,且写反后图形仍是「一个形状」,只是方向不同,不报错。
/// </para>
/// <para>
/// <b>起止角要么都给、要么都不给</b>:都不给是整椭圆;都给则沿椭圆从起始角走到结束角,
/// 再<b>闭合到圆心</b>形成扇形(扇形的两条半径因此存在,可被描边画出)。
/// 只给一个角是自相矛盾的输入,直接报错而不是猜一个默认值。
/// </para>
/// </remarks>
public sealed class EllipseShape(
    double cx,
    double cy,
    double rx,
    double ry,
    double? startAngleDegrees = null,
    double? endAngleDegrees = null) : Shape
{
    /// <summary>中心横坐标。</summary>
    public double Cx { get; } = cx;

    /// <summary>中心纵坐标。</summary>
    public double Cy { get; } = cy;

    /// <summary>横半轴,须大于 0。</summary>
    public double Rx { get; } = rx;

    /// <summary>纵半轴,须大于 0。</summary>
    public double Ry { get; } = ry;

    /// <summary>起始角(度);<c>null</c> 表示整椭圆。</summary>
    public double? StartAngleDegrees { get; } = startAngleDegrees;

    /// <summary>结束角(度);<c>null</c> 表示整椭圆。</summary>
    public double? EndAngleDegrees { get; } = endAngleDegrees;

    /// <summary>是否绘制为扇形(而非整椭圆)。</summary>
    public bool IsSector => StartAngleDegrees.HasValue && EndAngleDegrees.HasValue;

    /// <inheritdoc/>
    /// <exception cref="InvalidGeometryException">半径非正、起止角只给其一,或坐标非有限 / 超量级。</exception>
    public override void Validate()
    {
        PathGeometry.ValidatePoint(new PointD(Cx, Cy), "椭圆中心");
        PathGeometry.ValidateCoordinate(Rx, "椭圆横半轴 rx");
        PathGeometry.ValidateCoordinate(Ry, "椭圆纵半轴 ry");

        if (!(Rx > 0))
        {
            throw new InvalidGeometryException($"椭圆横半轴 rx 必须大于 0,实际 {Rx}。");
        }

        if (!(Ry > 0))
        {
            throw new InvalidGeometryException($"椭圆纵半轴 ry 必须大于 0,实际 {Ry}。");
        }

        if (StartAngleDegrees.HasValue != EndAngleDegrees.HasValue)
        {
            throw new InvalidGeometryException(
                "start_angle 与 end_angle 必须同时给出(扇形)或同时省略(整椭圆),"
                + $"实际 start_angle={(StartAngleDegrees.HasValue ? StartAngleDegrees.Value.ToString() : "null")}、"
                + $"end_angle={(EndAngleDegrees.HasValue ? EndAngleDegrees.Value.ToString() : "null")}。");
        }

        // 用模式匹配取值而非 .HasValue / .Value:两个角度是两个独立的可空属性,
        // 编译器无法从一个的 HasValue 推出另一个非空(尽管上面刚断言过二者同有同无),
        // 这样写既消掉了可空警告,也把「两个都取到了」这件事写进了控制流本身
        if (StartAngleDegrees is { } startAngle && EndAngleDegrees is { } endAngle)
        {
            PathGeometry.ValidateCoordinate(startAngle, "start_angle");
            PathGeometry.ValidateCoordinate(endAngle, "end_angle");
        }
    }

    /// <inheritdoc/>
    public override IReadOnlyList<PathFigure> ToFigures()
    {
        double tolerance = DrawingLimits.BezierFlatnessTolerance;

        if (StartAngleDegrees is not { } startDegrees || EndAngleDegrees is not { } endDegrees)
        {
            // 整椭圆:采样点不重复首点,闭合由 PathFigure 的闭合语义承担
            int steps = Math.Max(8, PathFlattener.StepsForSweep(Math.Max(Rx, Ry), tolerance, 2 * Math.PI));
            var points = new PointD[steps];

            for (int i = 0; i < steps; i++)
            {
                points[i] = Evaluate(2 * Math.PI * i / steps);
            }

            return [new PathFigure(points, isClosed: true)];
        }

        double sweep = (endDegrees - startDegrees) * Math.PI / 180.0;
        int segments = Math.Max(2, PathFlattener.StepsForSweep(Math.Max(Rx, Ry), tolerance, sweep));

        var sector = new List<PointD>(segments + 2);
        for (int i = 0; i <= segments; i++)
        {
            sector.Add(Evaluate(startDegrees * Math.PI / 180.0 + sweep * i / segments));
        }

        // 末点收到圆心:闭合后即形成「弧 + 两条半径」的扇形边界
        sector.Add(new PointD(Cx, Cy));

        return [new PathFigure(sector.ToArray(), isClosed: true)];
    }

    /// <summary>按参数角求出椭圆上的点。</summary>
    private PointD Evaluate(double theta)
        => new(Cx + Rx * Math.Cos(theta), Cy + Ry * Math.Sin(theta));
}

/// <summary>
/// 任意顶点多边形,自动闭合。
/// </summary>
/// <remarks>
/// 与 <see cref="LineShape"/> 的唯一差别就是闭合:多边形的末边由末顶点回到首顶点,
/// 而折线不画这一条边。需要「看起来闭合但不填充」的折线请显式重复首顶点。
/// </remarks>
public sealed class PolygonShape : Shape
{
    private readonly PointD[] _points;

    /// <summary>以顶点序列构造。</summary>
    /// <param name="points">顶点序列,至少 3 个。数组会被复制,构造后修改原数组不影响本对象。</param>
    /// <exception cref="ArgumentNullException"><paramref name="points"/> 为 <c>null</c>。</exception>
    public PolygonShape(params PointD[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        _points = (PointD[])points.Clone();
    }

    /// <summary>顶点数量。</summary>
    public int PointCount => _points.Length;

    /// <inheritdoc/>
    /// <exception cref="InvalidGeometryException">顶点少于 3 个,或存在非有限 / 超量级坐标。</exception>
    public override void Validate()
    {
        if (_points.Length < 3)
        {
            throw new InvalidGeometryException($"多边形至少需要 3 个顶点,实际 {_points.Length} 个。");
        }

        PathGeometry.ValidatePoints(_points, "points");
    }

    /// <inheritdoc/>
    public override IReadOnlyList<PathFigure> ToFigures() => [new PathFigure(_points, isClosed: true)];
}

/// <summary>
/// SVG path 形状:直接承载 <c>d</c> 字符串。
/// </summary>
/// <remarks>
/// <para>
/// 本类型是「字符串 → 折线」这条链路的入口,链路上有两处会失败:
/// 语法错误(<see cref="PathSyntaxException"/>,消息含字符偏移)与超出上限
/// (<see cref="DrawingLimitExceededException"/>,在解析过程中即中断)。
/// </para>
/// <para>
/// <b>刻意不缓存解析结果</b>:<see cref="Validate"/> 与 <see cref="ToFigures"/> 各自独立解析一次。
/// 缓存会让本类型带上可变状态,而绘制本就发生在注册表的条目锁内,一旦引入共享状态,
/// 「同一个形状对象被两次并发绘制」就从「不可能」变成「需要论证安全」。重复解析的代价
/// 远低于这个论证成本。
/// </para>
/// </remarks>
public sealed class PathShape(string d) : Shape
{
    /// <summary>SVG path 的 <c>d</c> 字符串。</summary>
    public string D { get; } = d ?? throw new ArgumentNullException(nameof(d));

    /// <inheritdoc/>
    /// <exception cref="PathSyntaxException">语法错误。</exception>
    /// <exception cref="DrawingLimitExceededException">长度、命令数或扁平化段数超限。</exception>
    /// <exception cref="InvalidGeometryException">坐标非有限值或超出量级上限。</exception>
    public override void Validate() => _ = Flatten();

    /// <inheritdoc/>
    public override IReadOnlyList<PathFigure> ToFigures() => Flatten();

    private List<PathFigure> Flatten() => PathFlattener.Flatten(
        SvgPathParser.Parse(D),
        DrawingLimits.BezierFlatnessTolerance);
}
