using System.Buffers.Binary;
using System.IO.Compression;

namespace DImage.Api.Imaging;

/// <summary>
/// 自研零依赖 PNG 编码器:把 <see cref="ImageBuffer"/> 编码为符合 PNG 规范的字节流。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么自研而非引入 ImageSharp / System.Drawing.Common</b>:本项目「后端零第三方业务依赖」
/// 是既定原则,图像编解码正属于该原则覆盖的核心业务能力。PNG 的无损编码路径本身很短
/// (签名 + 三个块 + 一个 deflate 流),引入数十 MB 的图形库只为写出无损 PNG 并不划算;
/// 真正需要自行实现的部分只有两个校验和:CRC-32(已抽至 <see cref="Crc32"/>,供编解码共用)
/// 与 Adler-32(留在本类型内,解码侧由 <c>ZLibStream</c> 负责),二者各约 20 行。
/// </para>
/// <para>
/// <b>为什么在 <see cref="DeflateStream"/> 之外还要手写 zlib 头与 Adler-32</b>:
/// PNG 的 <c>IDAT</c> 承载的是 <b>zlib 流</b>(RFC 1950),不是裸 deflate 流(RFC 1951)。
/// 而 .NET 的 <see cref="DeflateStream"/> 按定义<b>只产出裸 deflate 数据</b> ——
/// 既没有 zlib 的 2 字节头(<c>0x78 0x9C</c> 表示 deflate + 默认压缩级别窗口),也没有尾部的
/// 4 字节 Adler-32 校验。缺了任何一段,文件在任何解码器中都会被判为「损坏」。
/// 这是本类型最容易出错的地方:<b>整个流程走完不抛任何异常,产物却完全不可用</b>。
/// </para>
/// <para>
/// <b>Adler-32 累加的是「未压缩的扫描行数据」,不是压缩输出</b>:zlib 的 Adler-32 校验对象是
/// 压缩<b>之前</b>的字节序列(含每行的 filter 字节),而 <see cref="DeflateStream"/> 只会吐出压缩
/// <b>之后</b>的字节。二者长度都不同,绝无可能从压缩输出反推。因此本实现把 Adler-32 的更新
/// 放在<b>写入 <see cref="DeflateStream"/> 的同一个循环内</b> —— 写进去什么,就累加什么。
/// </para>
/// <para>
/// <b>为什么不缓冲未压缩数据</b>:逐行直接写入 <see cref="DeflateStream"/>,未压缩侧只驻留
/// 单行缓冲。若先把全部扫描行攒进 <see cref="MemoryStream"/> 再压缩,8192×8192 RGBA 会额外
/// 常驻约 256 MiB 的中间数据 —— 这正是「上限校验是安全要求」想要避免的那种内存放大。
/// </para>
/// </remarks>
public static class PngEncoder
{
    /// <summary>PNG 文件签名(8 字节),用于让解码器在读取任何块之前识别文件类型。</summary>
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>IHDR 块类型标识。</summary>
    private static readonly byte[] ChunkTypeIhdr = "IHDR"u8.ToArray();

    /// <summary>IDAT 块类型标识。</summary>
    private static readonly byte[] ChunkTypeIdat = "IDAT"u8.ToArray();

    /// <summary>IEND 块类型标识。</summary>
    private static readonly byte[] ChunkTypeIend = "IEND"u8.ToArray();

    /// <summary>
    /// 单个 IDAT 块承载的压缩数据上限(64 KiB)。
    /// </summary>
    /// <remarks>
    /// 规范未规定单块上限,但要求压缩数据可被拆分为连续的多个 IDAT 块。取 64 KiB 是工程折中:
    /// 远大于规范推荐的下限(数千字节),不会产生海量微小块;又远小于「整幅图一个块」,
    /// 避免下游解码器在单块上产生大额瞬时分配。
    /// </remarks>
    private const int MaxIdatChunkLength = 64 * 1024;

    /// <summary>PNG 位深,固定 8 位/通道(本服务五种格式的通道宽度均为 1 字节)。</summary>
    private const byte BitDepth = 8;

    /// <summary>压缩方法,PNG 规范中仅 0(deflate)有定义。</summary>
    private const byte CompressionMethodDeflate = 0;

    /// <summary>行过滤方法,0 表示无过滤(见 <see cref="FilterTypeNone"/>)。</summary>
    private const byte FilterMethodNone = 0;

    /// <summary>非交错(逐行顺序)扫描方式。</summary>
    private const byte InterlaceNone = 0;

    /// <summary>
    /// 行过滤类型 <c>None</c>:每行前置 1 字节,声明该行未做任何预测过滤。
    /// </summary>
    /// <remarks>
    /// PNG 允许对每行施加差分预测(Sub/Up/Average/Paeth)以提升压缩率,但过滤是可选的。
    /// 本实现恒用 <c>None</c>:无损编码的正确性不依赖过滤,而任何过滤都是可逆的有损风险面 ——
    /// 一旦预测/还原两侧不对称,像素数据就被静默改写。压缩率让位于正确性。
    /// </remarks>
    private const byte FilterTypeNone = 0;

    /// <summary>
    /// 把 <paramref name="image"/> 的有效像素编码为 PNG 字节流。
    /// </summary>
    /// <remarks>
    /// 只编码 <see cref="ImageBuffer.ByteLength"/> 范围内的有效像素,行尾填充不参与编码。
    /// </remarks>
    /// <param name="image">待编码的图像;其结果不依赖该对象的生存期,编码完成后可立即释放。</param>
    /// <returns>完整的 PNG 文件字节流。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> 为 <c>null</c>。</exception>
    /// <exception cref="ArgumentOutOfRangeException">图像格式不是已定义的像素格式。</exception>
    public static byte[] Encode(ImageBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);

        byte colorType = ResolveColorType(image.Format);
        bool swapRedBlue = NeedsRedBlueSwap(image.Format);
        int rowBytes = image.MinStride;

        // 先压出 deflate 数据再拼装文件:PNG 要求 IHDR 出现在最前,而 IHDR 中的尺寸字段
        // 与压缩无关,故顺序上无冲突;但 IDAT 的分块长度依赖压缩结果,必须压缩完才能确定。
        byte[] compressed = CompressScanlines(image, rowBytes, swapRedBlue);

        using var png = new MemoryStream(compressed.Length + 1024);

        png.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], image.Height);
        ihdr[8] = BitDepth;
        ihdr[9] = colorType;
        ihdr[10] = CompressionMethodDeflate;
        ihdr[11] = FilterMethodNone;
        ihdr[12] = InterlaceNone;
        WriteChunk(png, ChunkTypeIhdr, ihdr);

        // 压缩数据按固定上限切分为连续 IDAT 块。用 Span 切片而非 Substring/数组拷贝:
        // 切块不应带来与压缩输出等量的额外分配。
        var compressedSpan = compressed.AsSpan();
        for (int offset = 0; offset < compressedSpan.Length; offset += MaxIdatChunkLength)
        {
            int length = Math.Min(MaxIdatChunkLength, compressedSpan.Length - offset);
            WriteChunk(png, ChunkTypeIdat, compressedSpan.Slice(offset, length));
        }

        WriteChunk(png, ChunkTypeIend, ReadOnlySpan<byte>.Empty);

        return png.ToArray();
    }

    /// <summary>
    /// 把扫描行编码为 zlib 流:2 字节 zlib 头 + 裸 deflate 数据 + 4 字节 Adler-32 尾部。
    /// </summary>
    /// <remarks>
    /// Adler-32 与压缩在同一循环内同步推进 —— 校验的是「喂给压缩器的原始字节」,
    /// 而不是压缩器吐出的字节。详见类型注释。
    /// </remarks>
    private static byte[] CompressScanlines(ImageBuffer image, int rowBytes, bool swapRedBlue)
    {
        using var output = new MemoryStream();

        // zlib 头:CMF=0x78(deflate,32K 窗口)、FLG=0x9C(默认压缩级别,且使 CMF*256+FLG 能被 31 整除)。
        // 这两个字节不是可选的「元数据」,而是 zlib 流的组成部分;DeflateStream 不产出它们。
        output.WriteByte(0x78);
        output.WriteByte(0x9C);

        var adler = new Adler32();

        // 行缓冲:1 字节 filter type + 有效像素区。只驻留一行,与图像高度无关。
        var row = new byte[1 + rowBytes];
        row[0] = FilterTypeNone;

        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < image.Height; y++)
            {
                // 只取前 MinStride 字节:GetRowSpan 恒返回 Stride 长度,行尾填充(带填充缓冲区或
                // Slice 视图的共同特征)不属于像素内容。整行复制会把填充字节当成像素写进 PNG,
                // 表现为图像右侧出现一条来源不明的杂色带。
                ReadOnlySpan<byte> source = image.GetRowSpan(y)[..rowBytes];
                CopyRow(row.AsSpan(1), source, image.BytesPerPixel, swapRedBlue);

                deflate.Write(row, 0, row.Length);

                // 与上一步同循环:压缩前的原始字节(含 filter 字节)才是 Adler-32 的累加对象
                adler.Update(row);
            }
        }

        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, adler.Value);
        output.Write(checksum);

        return output.ToArray();
    }

    /// <summary>
    /// 把一行有效像素复制到编码缓冲,必要时逐像素交换 R/B 通道。
    /// </summary>
    /// <remarks>
    /// <b>为什么 Bgr 系必须交换通道</b>:PNG 规范的<b>色彩类型中没有 BGR</b> —— 真彩只有
    /// colorType 2(RGB)与 6(RGBA),二者的通道序恒为 R,G,B(,A)。若把 <c>Bgr24</c> 的字节
    /// 原样写进 colorType 2 的块,文件结构完全合法、任何解码器都能打开、宽高也全对,
    /// <b>唯独红蓝两色被对调</b>。这种错误既不会抛异常,也不会被 CRC/Adler 校验发现
    /// (校验和算的是同一串字节),肉眼在多数照片上也很难察觉,只能靠逐一比对像素值兜住。
    /// </remarks>
    private static void CopyRow(Span<byte> destination, ReadOnlySpan<byte> source, int bytesPerPixel, bool swapRedBlue)
    {
        if (!swapRedBlue)
        {
            source.CopyTo(destination);
            return;
        }

        for (int i = 0; i < source.Length; i += bytesPerPixel)
        {
            destination[i] = source[i + 2];
            destination[i + 1] = source[i + 1];
            destination[i + 2] = source[i];

            // 仅 32 位格式有第 4 字节;Alpha 在 BGRA 与 RGBA 中都位于末字节,无需搬移
            if (bytesPerPixel == 4)
            {
                destination[i + 3] = source[i + 3];
            }
        }
    }

    /// <summary>求像素格式对应的 PNG 色彩类型:Grayscale=0、Truecolor=2、Truecolor+Alpha=6。</summary>
    /// <exception cref="ArgumentOutOfRangeException">格式不是已定义的像素格式。</exception>
    private static byte ResolveColorType(PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => 0,
        PixelFormat.Rgb24 => 2,
        PixelFormat.Bgr24 => 2,
        PixelFormat.Rgba32 => 6,
        PixelFormat.Bgra32 => 6,
        // 未定义格式必须立即失败:静默兜底会让 (PixelFormat)99 按某个色彩类型写出
        // 字节数不匹配的扫描行,解码器只会报「损坏」,无法定位到格式取值本身
        _ => throw new ArgumentOutOfRangeException(
            nameof(format), format, $"未知的像素格式({(int)format}),无法确定 PNG 色彩类型。")
    };

    /// <summary>该格式在写入 PNG 前是否需要交换 R/B 通道。</summary>
    /// <exception cref="ArgumentOutOfRangeException">格式不是已定义的像素格式。</exception>
    private static bool NeedsRedBlueSwap(PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => false,
        PixelFormat.Rgb24 => false,
        PixelFormat.Rgba32 => false,
        PixelFormat.Bgr24 => true,
        PixelFormat.Bgra32 => true,
        _ => throw new ArgumentOutOfRangeException(
            nameof(format), format, $"未知的像素格式({(int)format}),无法确定通道序。")
    };

    /// <summary>
    /// 写出一个 PNG 块:<c>长度(4, 大端) + 类型(4) + 数据 + CRC-32(4, 大端)</c>。
    /// </summary>
    /// <remarks>
    /// CRC 的计算范围是<b>类型与数据</b>,不含长度字段 —— 规范如此定义。把长度也算进去
    /// 会产出一份 CRC 自洽、但所有标准解码器都判为损坏的文件。
    /// </remarks>
    private static void WriteChunk(Stream destination, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], data.Length);
        type.CopyTo(header[4..]);
        destination.Write(header);

        destination.Write(data);

        uint crc = Crc32.Compute(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        destination.Write(crcBytes);
    }

    /// <summary>
    /// Adler-32 校验(RFC 1950),zlib 流的尾校。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两个累加器 <c>s1</c>(字节和)与 <c>s2</c>(s1 的滚动和)均对素数 <c>65521</c> 取模。
    /// 起始值 <c>s1=1, s2=0</c>,最终值 <c>(s2 &lt;&lt; 16) | s1</c>。
    /// </para>
    /// <para>
    /// <b>为什么要按 5552 字节分段取模</b>:逐字节取模对 256 MiB 的图意味着上亿次整数除法;
    /// 而完全不在中途取模则会让 <c>s2</c> 回绕。5552 是 zlib 采用的<b>可证明安全</b>的步长:
    /// 段内 <c>s2</c> 的最大增量为
    /// <c>255 * 5552 * 5553 / 2 + 5553 * 65520 = 4_294_690_200</c>,
    /// 恰好小于 <see cref="uint"/> 上限 <c>4_294_967_295</c>,再多一个字节就会溢出。
    /// <b>此常量不可随意调大</b>。
    /// </para>
    /// </remarks>
    private struct Adler32()
    {
        private const uint Modulus = 65521;
        private const int ChunkSize = 5552;

        private uint _s1 = 1;
        private uint _s2 = 0;

        /// <summary>当前累加结果的最终校验值。</summary>
        public readonly uint Value => (_s2 << 16) | _s1;

        /// <summary>累加一段压缩前的原始字节。</summary>
        public void Update(ReadOnlySpan<byte> data)
        {
            uint s1 = _s1;
            uint s2 = _s2;

            while (!data.IsEmpty)
            {
                int take = Math.Min(ChunkSize, data.Length);
                ReadOnlySpan<byte> chunk = data[..take];

                foreach (byte b in chunk)
                {
                    s1 += b;
                    s2 += s1;
                }

                s1 %= Modulus;
                s2 %= Modulus;

                data = data[take..];
            }

            _s1 = s1;
            _s2 = s2;
        }
    }
}
