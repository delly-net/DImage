namespace DImage.Api.Imaging;

/// <summary>
/// 与像素格式无关的归一化颜色值(RGBA)。
/// </summary>
/// <remarks>
/// <para>
/// 5 种像素格式若各自暴露裸字节,调用方必须自行处理通道序(RGB / BGR)与 Alpha 的有无;
/// 统一收敛到本类型后,<c>ImageBuffer.GetPixel</c>/<c>SetPixel</c> 才真正表达「格式语义」,
/// 上层算法也因此与具体格式解耦。
/// </para>
/// <para>
/// <paramref name="A"/> 的默认值 <c>255</c> <b>必须写在主构造函数参数上</b>:
/// <c>record struct</c> 的默认值只对主构造函数参数生效,写在属性上不会起作用。
/// 注意 <c>default(PixelColor)</c> 仍然是全 0(即 <c>A=0</c>),需要不透明黑请显式使用 <see cref="Black"/>。
/// </para>
/// </remarks>
/// <param name="R">红通道。</param>
/// <param name="G">绿通道。</param>
/// <param name="B">蓝通道。</param>
/// <param name="A">Alpha 通道,默认 255(不透明)。</param>
public readonly record struct PixelColor(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>不透明黑。</summary>
    public static readonly PixelColor Black = new(0, 0, 0);

    /// <summary>不透明白。</summary>
    public static readonly PixelColor White = new(255, 255, 255);

    /// <summary>完全透明黑。</summary>
    public static readonly PixelColor Transparent = new(0, 0, 0, 0);
}
