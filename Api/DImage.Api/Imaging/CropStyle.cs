namespace DImage.Api.Imaging;

/// <summary>
/// 一次图像裁切请求的样式:填充规则、抗锯齿开关与输出画布尺寸模式。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类型是纯值对象</b>,与 <see cref="DrawStyle"/> / <see cref="CompositeStyle"/> 同构:
/// 不含 <see cref="ImageBuffer"/> 引用、不读配置、不注入 <c>IOptions&lt;T&gt;</c>。
/// 裁切请求因此可以表达为「源 Id + 什么区域 + 什么样式」三样纯数据,
/// 从而让注册表方法保持 <c>Crop(id, region, style)</c> 这一形态,<b>不需要把裸缓冲区交给工具层</b>。
/// </para>
/// <para>
/// <b>默认值一律写在主构造函数的参数默认值上</b>,与 <see cref="CompositeStyle"/> 同一手法 ——
/// 这里的三个默认值全是编译期常量(<see cref="FillRule.NonZero"/> / <c>true</c> / <c>false</c>),
/// 故不需要 <see cref="DrawStyle"/> 那样的显式构造函数。
/// </para>
/// <para>
/// <b><see cref="ToBounds"/> 的两种语义</b>:
/// <list type="bullet">
///   <item><description><c>false</c>(默认)—— 输出画布<b>保持源图尺寸</b>,裁切区域落在画布内的部分被保留、
///   其余位置置 0。此时输出的坐标原点与源图一致,可与其他按源图坐标绘制的工具直接叠加;</description></item>
///   <item><description><c>true</c> —— 输出画布<b>收缩到裁切区域的包围盒</b>(原点取 <c>floor</c>、尺寸取 <c>ceil</c> 后之差),
///   即「把裁出来的那块单独拿出来」。包围盒<b>不与源图求交</b>:区域超出源图时输出照常包含该部分,
///   只是那部分像素恒为 0 —— 规则统一,不引入「空交集怎么办」这一本不存在的错误分支。</description></item>
/// </list>
/// </para>
/// <para>
/// <b><see cref="FillRule"/> 与 <see cref="Antialias"/> 只对路径区域有意义</b>:
/// 矩形区域的坐标在工具层被强制为整数,像素中心恰好落在区间内或外,
/// 覆盖率恒为 0 或 1,与填充规则无关、也无中间值可供抗锯齿。给矩形传这两个参数不会报错,
/// 但也不产生任何效果 —— 这与「开放一个永远无效的参数」不同,它们是同一套区域描述手段的公共参数。
/// </para>
/// <para>
/// <b><c>default(CropStyle)</c> 不是「默认样式」</b> —— 这是本项目第二次踩到 <c>record struct</c>
/// 的零值陷阱(<see cref="CompositeStyle"/> 的 <c>ScaleX</c> 是第一次,<see cref="ImageRegistryLimits"/>
/// 是同一族缺陷):<c>record struct</c> 永远带一个编译器合成的<b>无参构造函数</b>,
/// <c>new CropStyle()</c> 与 <c>default(CropStyle)</c> 命中的是<b>它</b>,不是主构造函数 ——
/// 于是得到的不是「nonzero + 抗锯齿开 + 保持原尺寸」,而是 <see cref="Antialias"/> 被静默置为 <c>false</c>。
/// </para>
/// <para>
/// 这一颗雷比 <see cref="CompositeStyle"/> 的那一颗更隐蔽:后者的零值会<b>立刻</b>被
/// <c>Validate</c> 以 <see cref="InvalidScaleException"/> 拒绝(失败得很响),
/// 而本类型的零值<b>完全合法</b>,只是悄悄关掉了抗锯齿 —— 对矩形区域毫无影响、
/// 只让路径区域的边缘变成锯齿,调用方无从察觉。
/// 故<b>要「默认样式」请写 <see cref="Default"/>,不要写 <c>new CropStyle()</c></b>;
/// 只给部分参数时(如 <c>new CropStyle(ToBounds: true)</c>)命中主构造函数,其余取参数默认值,符合直觉。
/// </para>
/// </remarks>
/// <param name="FillRule">路径区域的填充规则;默认 <see cref="FillRule.NonZero"/>。</param>
/// <param name="Antialias">路径区域是否抗锯齿;默认 <c>true</c>。关闭后区域边界上的覆盖率只有 0 或 1。</param>
/// <param name="ToBounds">是否把输出画布收缩到裁切区域的包围盒;默认 <c>false</c>(保持源图尺寸)。</param>
public readonly record struct CropStyle(
    FillRule FillRule = FillRule.NonZero,
    bool Antialias = true,
    bool ToBounds = false)
{
    /// <summary>
    /// 「不写参数时想要的那个默认值」:nonzero 填充规则 + 抗锯齿开 + 保持源图尺寸。
    /// </summary>
    /// <remarks>
    /// 存在的唯一理由是把 <c>default(CropStyle)</c> 的落差抹平(见类型注释中的零值说明)——
    /// 后者的 <see cref="Antialias"/> 是 <c>false</c>,会让路径区域的边缘悄悄退化为锯齿。
    /// 这与 <see cref="CompositeStyle.Identity"/> 是同一处置。
    /// </remarks>
    public static CropStyle Default { get; } = new(FillRule.NonZero, true, false);
}
