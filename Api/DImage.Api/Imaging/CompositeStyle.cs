namespace DImage.Api.Imaging;

/// <summary>
/// 一次图像合成请求的样式:源图落点、双向缩放倍数与不透明度。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类型是纯值对象</b>,与 <see cref="DrawStyle"/> 同构:不含 <see cref="ImageBuffer"/> 引用、
/// 不读配置、不注入 <c>IOptions&lt;T&gt;</c>。合成请求因此可以表达为「目标 Id + 源 Id + 什么样式」
/// 三样纯数据,从而让注册表方法保持 <c>Composite(targetId, sourceId, style)</c> 这一形态,
/// <b>不需要把裸缓冲区交给工具层</b>。
/// </para>
/// <para>
/// <b>默认值一律写在构造函数的参数默认值上</b>(与 <see cref="PixelColor"/> 同一手法):
/// 这里全部默认值都是编译期常量,故可以直接用主构造函数 ——
/// 这正是 <see cref="DrawStyle"/> 不得不改用显式构造函数的原因反证:它要默认的
/// <see cref="PixelColor.Black"/> 是 <c>static readonly</c>,写不进参数默认值。
/// </para>
/// <para>
/// <b><see cref="Opacity"/> 为 0 是合法的空操作,不是错误</b>:与 <see cref="DrawStyle"/> 中
/// 「既不描边也不填充」同理 —— 它什么也不改、被覆盖像素数为 <c>0</c>,却仍返回成功。
/// 拒绝它只会逼调用方为了「临时关掉一层」而改写整段调用代码。
/// </para>
/// <para>
/// <b>参数默认值只在「至少写了一个具名/位置参数」时才生效</b> —— 这是执行期实测到的坑,
/// 与 <see cref="ImageRegistryLimits"/> 的零值缺陷同类,但更隐蔽:
/// <c>record struct</c> 永远带一个编译器合成的无参构造函数,而 <c>new CompositeStyle()</c>
/// 与 <c>default(CompositeStyle)</c> 命中的是<b>它</b>,不是主构造函数 ——
/// 于是得到的不是「缩放 1、不透明度 1」,而是<b>全零</b>,<see cref="ScaleX"/> 与
/// <see cref="ScaleY"/> 都是 <c>0</c>,必然被 <see cref="Validate"/> 以
/// <see cref="InvalidScaleException"/> 拒绝。
/// </para>
/// <para>
/// 好在它<b>失败得很响</b>(抛异常,而非静默画出一张错图),故不为此改变取值语义
/// ——<c>scale = 0</c> 必须继续被拒,那是 A12 守着的断言。
/// 但<b>要「默认样式」请写 <see cref="Identity"/>,不要写字面量为空的 <c>new CompositeStyle()</c></b>;
/// 只给部分参数时(如 <c>new CompositeStyle(Opacity: 0.5)</c>)命中主构造函数,其余取参数默认值,符合直觉。
/// </para>
/// </remarks>
/// <param name="X">源图左上角在目标图中的横坐标,允许亚像素;默认 0。</param>
/// <param name="Y">源图左上角在目标图中的纵坐标,允许亚像素;默认 0。</param>
/// <param name="ScaleX">横向缩放倍数,须为有限值且大于 0;默认 1。</param>
/// <param name="ScaleY">纵向缩放倍数,须为有限值且大于 0;默认 1。</param>
/// <param name="Opacity">不透明度,取值 <c>[0, 1]</c>(含端点);默认 1。</param>
public readonly record struct CompositeStyle(
    double X = 0,
    double Y = 0,
    double ScaleX = 1,
    double ScaleY = 1,
    double Opacity = 1)
{
    /// <summary>
    /// 「什么都不改」的样式:落点原点、原尺寸、完全不透明 —— 即把源图 1:1 贴到目标左上角。
    /// </summary>
    /// <remarks>
    /// <b>这是「不写参数时想要的那个默认值」,而不是 <c>default(CompositeStyle)</c>。</b>
    /// 后者的 <see cref="ScaleX"/> / <see cref="ScaleY"/> 是 <c>0</c>(见类型注释中的零值说明),
    /// 一用就会被拒;本属性存在的唯一理由就是把这个落差抹平。
    /// </remarks>
    public static CompositeStyle Identity { get; } = new(0, 0, 1, 1, 1);

    /// <summary>
    /// 校验样式参数。非法时抛出,且<b>不触碰任何像素</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 落点复用 <see cref="PathGeometry.ValidateCoordinate"/> 而不是在此另写一遍有限性检查:
    /// 除了「有限性」,它还给出一致的<b>量级上限</b>与一致的错误措辞 ——
    /// 两处各判一次,必然逐渐分叉,而分叉的那天没有告警。
    /// </para>
    /// <para>
    /// <b>缩放只判「有限且大于 0」,不设上限</b>:上限在此无法起到保护作用 ——
    /// 无论源图被放大多少倍,实际写入范围都会被裁剪到目标画布之内,
    /// 故最坏耗时与「整张画布被填满」同阶(既有 8192×8192 满画布填充实测约 29.6 秒)。
    /// 立一个拍脑袋的上限,只会让「把 1×1 放大到铺满 4096×4096」这类完全正当的用法被拒。
    /// 而 <b>0 与负数必须拒绝</b>:前者会让落点计算中的除法产生 <c>±Infinity</c>,
    /// 后者会<b>镜像翻转</b>源图 —— 一个静默的、看起来「也能用」的错误结果。
    /// </para>
    /// <para>
    /// <b><see cref="Opacity"/> 越界必须拒绝而非钳制</b>:传入 <c>1.5</c> 的调用方想表达的
    /// 几乎不可能是「等于 1」,钳制会让它拿到一个自己没有要求过的结果且无从察觉。
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidGeometryException">落点非有限值或超出量级上限。</exception>
    /// <exception cref="InvalidScaleException">缩放倍数非有限值,或小于等于 0。</exception>
    /// <exception cref="InvalidOpacityException">不透明度非有限值,或落在 <c>[0, 1]</c> 之外。</exception>
    public void Validate()
    {
        PathGeometry.ValidateCoordinate(X, "x");
        PathGeometry.ValidateCoordinate(Y, "y");

        ValidateScale(ScaleX, "scale_x");
        ValidateScale(ScaleY, "scale_y");

        if (double.IsNaN(Opacity) || double.IsInfinity(Opacity))
        {
            throw new InvalidOpacityException(
                $"不透明度必须是有限值(NaN 与 ±Infinity 均不接受),实际 {Opacity}。");
        }

        if (Opacity < 0 || Opacity > 1)
        {
            throw new InvalidOpacityException($"不透明度必须落在 0 到 1 之间(含端点),实际 {Opacity}。");
        }
    }

    /// <summary>校验单个缩放分量。</summary>
    private static void ValidateScale(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new InvalidScaleException(
                $"缩放倍数 {name} 必须是有限值(NaN 与 ±Infinity 均不接受),实际 {value}。");
        }

        if (value <= 0)
        {
            throw new InvalidScaleException(
                $"缩放倍数 {name} 必须大于 0(0 会产生除零、负数会镜像翻转源图),实际 {value}。");
        }
    }
}
