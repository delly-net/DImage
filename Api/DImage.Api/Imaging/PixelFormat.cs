namespace DImage.Api.Imaging;

/// <summary>
/// 本服务支持的像素格式。
/// </summary>
/// <remarks>
/// 只覆盖最常用的 5 种;格式之间的<b>相互转换</b>属于后续任务,不在本类型职责内。
/// 枚举值采用<b>显式编号</b>:这些取值将成为后续图像算法与对外接口的契约基线,
/// 显式写死可避免日后在枚举中间插入新格式时,悄无声息地改变既有格式的数值。
/// </remarks>
public enum PixelFormat
{
    /// <summary>8 位灰度,1 字节/像素,通道序 <c>Y</c>。</summary>
    Gray8 = 0,

    /// <summary>24 位真彩,3 字节/像素,通道序 <c>R,G,B</c>。</summary>
    Rgb24 = 1,

    /// <summary>24 位真彩,3 字节/像素,通道序 <c>B,G,R</c>。</summary>
    Bgr24 = 2,

    /// <summary>32 位带 Alpha,4 字节/像素,通道序 <c>R,G,B,A</c>。</summary>
    Rgba32 = 3,

    /// <summary>32 位带 Alpha,4 字节/像素,通道序 <c>B,G,R,A</c>。</summary>
    Bgra32 = 4
}

/// <summary>
/// <see cref="PixelFormat"/> 的派生元数据查询(每像素字节数、通道数、是否含 Alpha)。
/// </summary>
/// <remarks>
/// 所有查询对<b>未定义的枚举值</b>一律抛出 <see cref="ArgumentOutOfRangeException"/>。
/// C# 枚举可被强转为任意整数,若此处使用 <c>default: return 4</c> 之类的兜底分支,
/// <c>(PixelFormat)99</c> 会被静默地按「4 字节/像素」处理,进而在像素寻址时写出越界数据 ——
/// 静默错误远比立即失败昂贵,因此宁可抛异常也不能兜底。
/// </remarks>
public static class PixelFormatExtensions
{
    /// <summary>查询每像素占用的字节数:Gray8=1、Rgb24/Bgr24=3、Rgba32/Bgra32=4。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> 不是已定义的像素格式。</exception>
    public static int GetBytesPerPixel(this PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => 1,
        PixelFormat.Rgb24 => 3,
        PixelFormat.Bgr24 => 3,
        PixelFormat.Rgba32 => 4,
        PixelFormat.Bgra32 => 4,
        // 未定义值必须立即失败:静默兜底会让 (PixelFormat)99 按 4 字节寻址并越界读写
        _ => throw new ArgumentOutOfRangeException(
            nameof(format), format, $"未知的像素格式({(int)format}),无法确定每像素字节数。")
    };

    /// <summary>查询颜色通道数量:Gray8=1、Rgb24/Bgr24=3、Rgba32/Bgra32=4。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> 不是已定义的像素格式。</exception>
    public static int GetChannelCount(this PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => 1,
        PixelFormat.Rgb24 => 3,
        PixelFormat.Bgr24 => 3,
        PixelFormat.Rgba32 => 4,
        PixelFormat.Bgra32 => 4,
        _ => throw new ArgumentOutOfRangeException(
            nameof(format), format, $"未知的像素格式({(int)format}),无法确定通道数量。")
    };

    /// <summary>查询格式是否携带 Alpha 通道:仅 Rgba32/Bgra32 为 <c>true</c>。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> 不是已定义的像素格式。</exception>
    public static bool HasAlpha(this PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => false,
        PixelFormat.Rgb24 => false,
        PixelFormat.Bgr24 => false,
        PixelFormat.Rgba32 => true,
        PixelFormat.Bgra32 => true,
        _ => throw new ArgumentOutOfRangeException(
            nameof(format), format, $"未知的像素格式({(int)format}),无法确定是否含 Alpha 通道。")
    };
}
