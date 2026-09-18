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

    /// <summary>
    /// 单次上传的 PNG 原始字节上限,8 MiB。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>取值依据</b>:<see cref="MaxByteLength"/> 的 256 MiB 是「解码<b>输出</b>」的上限
    /// (8192×8192 RGBA 未压缩即 256 MiB),而 PNG 是压缩容器,正常场景下压缩后在百 KB~数 MB 量级。
    /// 8 MiB 覆盖正常使用,同时使 base64 后的请求体约 10.7 MiB,仍落在宿主请求体上限之内。
    /// </para>
    /// <para>
    /// <b>它不是炸开防护的闸门</b>:deflate 压缩比可达约 1000:1,一段 8 MiB 的输入理论上能解出 8 GB。
    /// 真正挡住炸开的是「<b>声明宽高先行校验</b>」—— 在分配任何像素内存之前,用文件头声明的宽高
    /// 去撞 <see cref="ImageLimits"/> 与注册表容量。本常量是<b>外层闸门</b>:
    /// 挡住的是「输入本身就大」这一形态,与前者职责不同,缺一不可。
    /// </para>
    /// </remarks>
    public const int MaxInputByteLength = 8 * 1024 * 1024;

    /// <summary>
    /// base64 编码串的长度上限,由 <see cref="MaxInputByteLength"/> 按 4/3 派生。
    /// </summary>
    /// <remarks>
    /// <b>刻意写成派生表达式而不是另写一个字面量</b>:两个可独立漂移的常量迟早会分叉,
    /// 而分叉的后果是「串长校验通过、解码后字节数超限」这一不一致的判定路径。
    /// 编码公式为每 3 字节一组产出 4 个字符,不足一组的按整组补齐,故需先向上取整。
    /// 长度校验发生在<b>解码之前</b>,先解码再判长度等于闸门形同虚设。
    /// </remarks>
    public const int MaxBase64Length = ((MaxInputByteLength + 2) / 3) * 4;
}
