namespace DImage.Api.Imaging;

/// <summary>
/// 二维仿射变换矩阵(2×3)。
/// </summary>
/// <remarks>
/// <para>
/// 映射关系与 SVG / Canvas 的 <c>matrix(a,b,c,d,e,f)</c> <b>逐字一致</b>:
/// <code>
/// x' = a·x + c·y + e
/// y' = b·x + d·y + f
/// </code>
/// 沿用一个既有约定而非自创字段顺序,是因为这个六元组会被调用方以数组形式传入 ——
/// 自创顺序没有任何视觉线索可供发现错误,而错序的结果是一个「歪了」但仍能辨认的图形。
/// </para>
/// <para>
/// <b>可表达旋转、缩放、镜像、错切与平移,不可表达透视。</b>透视需要 3×3 且要做齐次除法,
/// 那会让「仿射」这个词不再成立,也让坐标出现除零面;本类型刻意止步于此。
/// </para>
/// <para>
/// <b>纯值类型,不持有任何服务或缓冲区</b>,与 <see cref="DrawStyle"/> / <see cref="Shape"/>
/// 同属「纯值输入」一族:注册表方法因此得以保持
/// <c>Draw(string id, Shape shape, DrawStyle style)</c> 的形态,裸缓冲区不出注册表。
/// </para>
/// <para>
/// <b>校验必须在使用之前完成</b>:六个分量都会直接参与乘法,<c>NaN</c> 与天文数字一旦进入
/// 就会污染全部顶点,而 <c>NaN</c> 还会让包围盒的 <c>Math.Min</c> / <c>Math.Max</c> 全部返回
/// <c>NaN</c> —— 循环边界随之失去约束,请求挂死且没有错误信息。
/// </para>
/// </remarks>
/// <param name="A">x 的 x 系数。</param>
/// <param name="B">x 的 y 系数(即 <c>y'</c> 中 x 的系数)。</param>
/// <param name="C">y 的 x 系数(即 <c>x'</c> 中 y 的系数)。</param>
/// <param name="D">y 的 y 系数。</param>
/// <param name="E">x 方向平移量。</param>
/// <param name="F">y 方向平移量。</param>
public readonly record struct AffineTransform(double A, double B, double C, double D, double E, double F)
{
    /// <summary>单位矩阵:不改变任何坐标。</summary>
    public static AffineTransform Identity => new(1, 0, 0, 1, 0, 0);

    /// <summary>是否为单位矩阵。</summary>
    /// <remarks>
    /// 供调用方跳过「无变换」的整段乘法。判据是逐分量相等而非行列式等于 1 ——
    /// 行列式为 1 的矩阵包含全部旋转,那是个完全不同的命题。
    /// </remarks>
    public bool IsIdentity => A == 1 && B == 0 && C == 0 && D == 1 && E == 0 && F == 0;

    /// <summary>把一个点映射到变换后的位置。</summary>
    /// <param name="point">待变换的点。</param>
    /// <returns>变换后的点。</returns>
    public PointD Apply(PointD point)
        => new(A * point.X + C * point.Y + E, B * point.X + D * point.Y + F);

    /// <summary>
    /// 校验全部六个分量的有限性与量级。
    /// </summary>
    /// <remarks>
    /// 复用 <see cref="PathGeometry.ValidateCoordinate"/> 的量级上限,而不是另立一套:
    /// 矩阵分量与坐标在「会被乘进坐标」这件事上是同一类量,各自设限只会得到两个迟早分叉的阈值。
    /// <b>校验通过不代表变换后的坐标合法</b> —— 分量合法但相乘超限的组合是存在的,
    /// 那种情况由变换后的顶点校验兜底。
    /// </remarks>
    /// <exception cref="InvalidGeometryException">任一分量非有限值或绝对值超过量级上限。</exception>
    public void Validate()
    {
        PathGeometry.ValidateCoordinate(A, "transform.a");
        PathGeometry.ValidateCoordinate(B, "transform.b");
        PathGeometry.ValidateCoordinate(C, "transform.c");
        PathGeometry.ValidateCoordinate(D, "transform.d");
        PathGeometry.ValidateCoordinate(E, "transform.e");
        PathGeometry.ValidateCoordinate(F, "transform.f");
    }
}
