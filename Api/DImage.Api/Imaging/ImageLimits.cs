namespace DImage.Api.Imaging;

/// <summary>
/// 图像尺寸上限的集中定义,沿用 <c>AuthConstants</c> / <c>McpProtocol</c> 的「常量集中定义」原则 ——
/// 数值只在此处出现一次,不在 <c>ImageBuffer</c> 内散落字面量。
/// </summary>
/// <remarks>
/// <para>
/// 上限是<b>安全要求</b>而非性能调优:本服务面向 MCP 对外提供绘图能力,<c>width</c>/<c>height</c>
/// 直接来自外部请求。在 <c>int</c> 下 <c>width * height * bytesPerPixel</c> 极易回绕成一个小正值,
/// 绕过校验后按回绕值分配数组、却按真实宽高计算行偏移,最终表现为越界读写。
/// </para>
/// <para>
/// 取值依据:单边 32768 覆盖常见扫描件与分块切片量级;像素总数取 64M(等价 8192×8192);
/// 字节数上限 256 MiB 与「64M 像素 × 4 字节」对应。三者共同保证任何通过校验的构造都不会溢出,
/// 也不会产生不可控的巨额分配。
/// </para>
/// </remarks>
public static class ImageLimits
{
    /// <summary>单边最大边长(像素)。</summary>
    public const int MaxDimension = 32_768;

    /// <summary>最大像素总数,64M(等价 8192×8192)。</summary>
    public const long MaxPixelCount = 67_108_864;

    /// <summary>单个缓冲区最大字节数,256 MiB。</summary>
    public const long MaxByteLength = 268_435_456;
}
