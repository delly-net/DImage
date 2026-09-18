using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace DImage.Api.Imaging;

/// <summary>
/// PNG 文件头的解析结果:足以在<b>不分配任何像素内存</b>的前提下完成全部准入判定。
/// </summary>
/// <param name="Width">声明的宽度(像素),已校验落在 <see cref="ImageLimits"/> 内。</param>
/// <param name="Height">声明的高度(像素),已校验落在 <see cref="ImageLimits"/> 内。</param>
/// <param name="BitDepth">IHDR 的位深字段。本类型只承载 <c>8</c>,其余取值在解析期即被拒绝。</param>
/// <param name="ColorType">IHDR 的色彩类型字段(0/2/3/4/6)。</param>
/// <param name="Format">解码后落入的内存像素格式。</param>
/// <param name="Channels">
/// <b>原始</b>色彩类型的通道数,反 filter 的每像素字节数即取此值 ——
/// 它由 <paramref name="ColorType"/> 唯一决定,<b>与 <paramref name="Format"/> 无关</b>。
/// 例如调色板(色彩类型 3)的 <see cref="Channels"/> 是 <c>1</c>(每像素一个索引字节),
/// 而 <see cref="Format"/> 是 <see cref="PixelFormat.Rgb24" />(每像素三个字节)。
/// 取错会让 Sub/Average/Paeth 三种过滤从某列起整体错位。
/// </param>
public readonly record struct PngHeader(
    int Width,
    int Height,
    byte BitDepth,
    byte ColorType,
    PixelFormat Format,
    int Channels);

/// <summary>
/// 自研零依赖 PNG 解码器:把符合 PNG 规范的字节流还原为 <see cref="ImageBuffer"/>。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么用 <see cref="ZLibStream"/> 而不是 <see cref="DeflateStream"/></b>:这是本类型
/// 头号的静默错误点,与 <see cref="PngEncoder"/> 编码侧手写 zlib 头的教训严格对称。
/// PNG 的 <c>IDAT</c> 承载的是 <b>zlib 流</b>(RFC 1950):2 字节头(<c>0x78 0x9C</c>)
/// + 裸 deflate + 4 字节 Adler-32 尾。<see cref="DeflateStream"/> 只吃裸 deflate,
/// 会把那两个头字节当作压缩数据处理 —— 表现为<b>解压立即失败或产出乱码</b>,
/// 而错误形态与「图本身损坏」高度相似,极难归因。
/// <see cref="ZLibStream"/> 自带 zlib 头解析与 Adler-32 校验,是唯一正确的选择;
/// 编码侧手写的那两段,解码侧正应该由它负责。
/// </para>
/// <para>
/// <b>两阶段 API 的由来</b>:对外只暴露 <see cref="ReadHeader"/> 与 <see cref="Decode"/> 两个方法。
/// 前者零分配,拿得到「声明的宽高」;后者才分配目标缓冲区。这个拆分不是风格偏好 ——
/// deflate 压缩比可达约 1000:1,一段 8 MiB 的输入理论上能解出 8 GB,
/// 只有在<b>分配之前</b>就能拿到声明宽高,「先撞上限再分配」的防护链条才成立。
/// 若把两步合成一个方法,防护就退化成「先分配再拒绝」。
/// </para>
/// <para>
/// <b>内存纪律</b>:解码期只驻留<b>两行缓冲</b>(当前行 + 反 filter 用的上一行),
/// 绝不整图缓冲;IDAT 读满 <see cref="PngHeader.Height"/> 行即停止读取,不把压缩流里的剩余数据读进内存。
/// 唯一按「输入体积」而非「图像尺寸」分配的中间缓冲是压缩数据本身(IDAT 拼接),
/// 其上界由 <see cref="ImageLimits.MaxInputByteLength"/> 兜住。
/// </para>
/// <para>
/// <b>失败一律抛异常</b>:<see cref="UnsupportedImageException"/> 表示「是图像,但本服务不支持这类 PNG」,
/// <see cref="InvalidImageException"/> 表示「结构损坏」,几何超限沿用
/// <see cref="ArgumentOutOfRangeException"/>。翻译为对外错误码是工具层的职责,
/// 本类型属 <c>Imaging/</c> 依赖图叶子,不认识任何错误码。
/// </para>
/// <para>
/// <b>零第三方依赖</b>:不使用 <c>System.Drawing</c> / ImageSharp / SkiaSharp,
/// 不使用 <c>unsafe</c> 与 <c>P/Invoke</c>,一切访问经由 <see cref="Span{T}"/>。
/// </para>
/// <para>
/// <b>支持范围</b>:位深 8、非交错、压缩方法 0、滤波方法 0;色彩类型 0 / 2 / 3 / 4 / 6。
/// 位深 1 / 2 / 4 / 16、Adam7 交错、未知色彩类型(1 / 5 / 7)一律以
/// <see cref="UnsupportedImageException"/> 明确失败,不静默截断、不钳制。
/// </para>
/// </remarks>
public static class PngDecoder
{
    /// <summary>PNG 文件签名(8 字节)。</summary>
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>IHDR 块的数据区长度,规范固定为 13。</summary>
    private const int IhdrDataLength = 13;

    /// <summary>调色板最多容纳的颜色项数(规范上限,索引为 1 字节)。</summary>
    private const int MaxPaletteEntries = 256;

    /// <summary>单个块自身的固定开销:长度 4 + 类型 4 + CRC 4。</summary>
    private const int ChunkOverhead = 12;

    // ————————————————————————— 阶段一:解析文件头 —————————————————————————

    /// <summary>
    /// 解析 PNG 文件头(签名 + IHDR),<b>不分配任何像素内存</b>。
    /// </summary>
    /// <remarks>
    /// 调用方须在拿到结果后、调用 <see cref="Decode"/> 之前完成容量预检
    /// (<see cref="ImageBufferStore.EnsureCapacity"/>)—— 这正是本方法存在的意义。
    /// </remarks>
    /// <param name="png">PNG 文件字节流。</param>
    /// <returns>文件头信息,含规范化后的目标像素格式。</returns>
    /// <exception cref="UnsupportedImageException">不是 PNG,或使用了本服务不支持的 PNG 特性。</exception>
    /// <exception cref="InvalidImageException">是 PNG 但结构不合法(首块不是 IHDR、长度错误、CRC-32 失败、宽高非正)。</exception>
    /// <exception cref="ArgumentOutOfRangeException">声明的宽高超出 <see cref="ImageLimits"/>。</exception>
    public static PngHeader ReadHeader(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
        {
            throw new UnsupportedImageException(
                "输入不是 PNG 图像:缺少 PNG 文件签名(89 50 4E 47 0D 0A 1A 0A)。"
                + "本服务仅支持 PNG,不支持 JPEG / GIF / BMP / WebP 等其它格式。");
        }

        if (png.Length < Signature.Length + ChunkOverhead + IhdrDataLength)
        {
            throw new InvalidImageException(
                $"PNG 数据不完整:至少需要 {Signature.Length + ChunkOverhead + IhdrDataLength} 字节"
                + $"才能容纳签名与 IHDR 块,实际 {png.Length} 字节。");
        }

        ReadOnlySpan<byte> type = png.Slice(Signature.Length + 4, 4);
        if (!type.SequenceEqual("IHDR"u8))
        {
            // 规范要求 IHDR 必须是首块:允许别的块占位会让「文件头」这一概念失去唯一性
            throw new InvalidImageException(
                $"PNG 首块必须是 IHDR,实际是 {Encoding.ASCII.GetString(type)}。");
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(png.Slice(Signature.Length, 4));
        if (length != IhdrDataLength)
        {
            throw new InvalidImageException(
                $"IHDR 数据区长度应为 {IhdrDataLength},实际 {length}。");
        }

        ReadOnlySpan<byte> data = png.Slice(Signature.Length + 8, IhdrDataLength);
        ValidateChunkCrc(type, data, png.Slice(Signature.Length + 8 + IhdrDataLength, 4));

        int width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
        int height = BinaryPrimitives.ReadInt32BigEndian(data[4..8]);

        if (width <= 0 || height <= 0)
        {
            throw new InvalidImageException(
                $"PNG 声明的宽高必须为正整数,实际 width={width}、height={height}。");
        }

        byte bitDepth = data[8];
        byte colorType = data[9];

        // IHDR 的字段序是「宽 4 + 高 4 + 位深 1 + 色彩类型 1 + 压缩方法 1 + 滤波方法 1 + 交错方法 1」。
        // 第 5 个字节(下标 10)是压缩方法、第 6 个(下标 11)是滤波方法,两者都必须是 0。
        // 把这两个下标记反不会报错,只会让本该拒绝的文件被放行到解压阶段。
        byte compressionMethod = data[10];
        byte filterMethod = data[11];
        byte interlaceMethod = data[12];

        if (compressionMethod != 0 || filterMethod != 0)
        {
            throw new UnsupportedImageException(
                $"不支持的 PNG 压缩/滤波方法:规范仅定义压缩方法 0、滤波方法 0,"
                + $"实际 compression={compressionMethod}、filter={filterMethod}。");
        }

        if (interlaceMethod != 0)
        {
            throw new UnsupportedImageException(
                $"不支持的 PNG 交错方式:本服务仅支持非交错(interlace=0,逐行顺序),"
                + $"实际 interlace={interlaceMethod}(1 为 Adam7)。");
        }

        if (bitDepth != 8)
        {
            throw new UnsupportedImageException(
                $"不支持的 PNG 位深:本服务仅支持 8 位/通道,实际 bitDepth={bitDepth}。");
        }

        PixelFormat format = ResolveFormat(colorType);

        // 尺寸校验一律以 long 运算:width/height 各可接近 2^31,相乘必然在 int 下回绕成一个小正值,
        // 使后面的比较形同虚设。这是「整数溢出」这条头号风险在解码侧的具体形态
        long pixelCount = (long)width * height;

        if (width > ImageLimits.MaxDimension || height > ImageLimits.MaxDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(png),
                $"PNG 声明的尺寸 {width}×{height} 超出单边上限 {ImageLimits.MaxDimension}。");
        }

        if (pixelCount > ImageLimits.MaxPixelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(png),
                $"PNG 声明的像素总数 {pixelCount}({width}×{height})超出上限 {ImageLimits.MaxPixelCount}。");
        }

        long byteLength = pixelCount * format.GetBytesPerPixel();
        if (byteLength > ImageLimits.MaxByteLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(png),
                $"PNG 解码后需要 {byteLength} 字节({format}),超出上限 {ImageLimits.MaxByteLength}。");
        }

        return new PngHeader(width, height, bitDepth, colorType, format, ChannelsOf(colorType));
    }

    // ————————————————————————— 阶段二:解码像素 —————————————————————————

    /// <summary>
    /// 把 PNG 字节流解码为一张<b>新建</b>的 <see cref="ImageBuffer"/>。
    /// </summary>
    /// <remarks>
    /// 调用方须先经 <see cref="ReadHeader"/> 取得 <paramref name="header"/> 并完成容量预检。
    /// 本方法会重新解析一次文件头并与 <paramref name="header"/> 复核一致,以防两者来自不同的文件。
    /// </remarks>
    /// <param name="png">PNG 文件字节流,须与 <paramref name="header"/> 同源。</param>
    /// <param name="header">由 <see cref="ReadHeader"/> 得到的文件头。</param>
    /// <returns>解码结果,格式为 <see cref="PngHeader.Format"/>,紧排、独立自有缓冲区。</returns>
    /// <exception cref="UnsupportedImageException">出现 <c>tRNS</c> 透明色键,或存在未知的关键块。</exception>
    /// <exception cref="InvalidImageException">块结构、CRC-32、zlib 流或扫描行数据不合法。</exception>
    public static ImageBuffer Decode(ReadOnlySpan<byte> png, in PngHeader header)
    {
        PngHeader parsed = ReadHeader(png);
        if (parsed != header)
        {
            throw new InvalidImageException(
                $"传入的文件头与 PNG 内容不一致:文件头 {header.Width}×{header.Height} colorType={header.ColorType},"
                + $"实际 {parsed.Width}×{parsed.Height} colorType={parsed.ColorType}。");
        }

        var idat = new MemoryStream();
        byte[]? palette = null;
        bool sawIdat = false;
        bool sawIend = false;

        // 第一遍:遍历块,校验结构与 CRC、收集 IDAT 与 PLTE、检出 tRNS。
        // 这一步完成后才允许分配目标缓冲区 —— tRNS 一类的「特性不支持」必须与几何超限一样,
        // 在分配之前就失败,否则拒绝本身就成了一次内存放大
        for (int offset = Signature.Length; ;)
        {
            if (offset + ChunkOverhead > png.Length)
            {
                throw new InvalidImageException(
                    $"PNG 块结构不完整:偏移 {offset} 处不足一个完整的块(至少需要 {ChunkOverhead} 字节),"
                    + $"文件总长 {png.Length} 字节。缺少 IEND 块?");
            }

            int length = BinaryPrimitives.ReadInt32BigEndian(png.Slice(offset, 4));
            if (length < 0)
            {
                throw new InvalidImageException($"PNG 块长度非法:偏移 {offset} 处声明长度 {length}(不得为负)。");
            }

            long end = (long)offset + ChunkOverhead + length;
            if (end > png.Length)
            {
                throw new InvalidImageException(
                    $"PNG 块数据越出文件范围:偏移 {offset} 处声明长度 {length},"
                    + $"需要到第 {end} 字节,文件仅 {png.Length} 字节。");
            }

            ReadOnlySpan<byte> type = png.Slice(offset + 4, 4);
            ReadOnlySpan<byte> data = png.Slice(offset + 8, length);
            ValidateChunkCrc(type, data, png.Slice(offset + 8 + length, 4));

            if (type.SequenceEqual("IEND"u8))
            {
                sawIend = true;
                break;
            }

            if (type.SequenceEqual("IHDR"u8))
            {
                // 已在 ReadHeader 中校验;此处仅在它出现在错误位置时拒绝
                if (offset != Signature.Length)
                {
                    throw new InvalidImageException("PNG 中出现了重复的 IHDR 块。");
                }
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                sawIdat = true;
                idat.Write(data);
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (sawIdat)
                {
                    throw new InvalidImageException("PLTE 块出现在 IDAT 之后,调色板必须在图像数据之前给出。");
                }

                if (palette is not null)
                {
                    throw new InvalidImageException("PNG 中出现了重复的 PLTE 块。");
                }

                if (header.ColorType is 0 or 4 or 6)
                {
                    throw new InvalidImageException(
                        $"色彩类型 {header.ColorType} 不允许出现 PLTE 块(调色板仅用于色彩类型 3)。");
                }

                if (length == 0 || length % 3 != 0 || length / 3 > MaxPaletteEntries)
                {
                    throw new InvalidImageException(
                        $"PLTE 数据长度非法:应为 1~{MaxPaletteEntries} 个 RGB 三元组"
                        + $"(共 3~{MaxPaletteEntries * 3} 字节),实际 {length} 字节。");
                }

                palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                // 透明色键与调色板 Alpha 都无法用现有 5 种内存格式表达。
                // 静默忽略会让带透底色的 PNG 解出实心像素 —— 图像看起来「正常但不对」,
                // 是极难归因的一类错误,故宁可明确失败
                throw new UnsupportedImageException(
                    $"不支持的 PNG 特性:tRNS(透明色键 / 调色板 Alpha)无法用本服务的像素格式表达,"
                    + $"色彩类型 {header.ColorType} 亦不例外。请先去除透明度再上传。");
            }
            else if ((type[0] & 0x20) == 0)
            {
                // 首字母大写即「关键块」。辅助块(小写,如 gAMA/pHYs/tEXt)可以安全跳过,
                // 关键块则意味着渲染结果依赖本解码器不认识的信息,跳过必然产出错误的图
                throw new UnsupportedImageException(
                    $"不支持的 PNG 关键块 {Encoding.ASCII.GetString(type)}:"
                    + "本服务仅认识 IHDR / PLTE / IDAT / IEND 四种关键块。");
            }

            offset = (int)end;
        }

        if (!sawIend)
        {
            throw new InvalidImageException("PNG 缺少 IEND 块,文件不完整。");
        }

        if (!sawIdat)
        {
            throw new InvalidImageException("PNG 缺少 IDAT 块,没有图像数据。");
        }

        if (header.ColorType == 3 && palette is null)
        {
            throw new InvalidImageException("调色板 PNG(色彩类型 3)缺少 PLTE 块,无法展开颜色索引。");
        }

        var image = new ImageBuffer(header.Width, header.Height, header.Format);

        // 压缩数据按输入体积分配(其上界由 ImageLimits.MaxInputByteLength 兜住),
        // 不属于「整图缓冲」;真正的像素缓冲始终只有 image 本身加两行扫描行
        int compressedLength = (int)idat.Length;
        byte[] compressed = idat.GetBuffer();
        using var source = new MemoryStream(compressed, 0, compressedLength, writable: false);

        try
        {
            using var zlib = new ZLibStream(source, CompressionMode.Decompress);
            DecodeScanlines(zlib, image, header, palette);
        }
        catch (InvalidDataException ex)
        {
            // ZLibStream 对 zlib 头错误、deflate 数据损坏、Adler-32 不匹配统一抛此异常。
            // 三类成因对调用方的可行动作一致 —— 图坏了,重新导出一份
            throw new InvalidImageException($"PNG 的 zlib 数据流无法解压:{ex.Message}");
        }

        return image;
    }

    // ————————————————————————— 扫描行解码 —————————————————————————

    /// <summary>
    /// 逐行解压、反 filter 并写入像素,全程只驻留两行缓冲。
    /// </summary>
    private static void DecodeScanlines(Stream zlib, ImageBuffer image, in PngHeader header, byte[]? palette)
    {
        // 行宽必须来自「源」:宽度 × 源色彩类型的通道数。取目标 ImageBuffer 的步长是错的 ——
        // 调色板(1 通道 → 目标 rgb24)与灰度+Alpha(2 通道 → 目标 rgba32)两种源一旦按目标
        // 步长读,整行就会错位:第 1 行还像是「过滤类型非法」,第 2 行起彻底走样。
        int rowBytes = header.Width * header.Channels;

        // 两行缓冲:current 承担「本行的过滤后数据 → 反演结果」,
        // previous 恒为「上一行的反演结果」—— 这正是 Up/Average/Paeth 三种预测所要求的语义。
        // 用原始字节当上一行(而非反演结果)是最常见的静默错误:第 1 行完全正常,第 2 行起逐渐走样
        byte[] current = new byte[rowBytes];
        byte[] previous = new byte[rowBytes];

        for (int y = 0; y < header.Height; y++)
        {
            int filter = zlib.ReadByte();
            if (filter < 0)
            {
                throw new InvalidImageException(
                    $"PNG 扫描行数据不足:读到第 {y} 行(共 {header.Height} 行)时解压流已结束。");
            }

            if (filter > 4)
            {
                throw new InvalidImageException(
                    $"PNG 第 {y} 行的过滤类型 {filter} 未定义(有效值为 0~4)。");
            }

            ReadExactly(zlib, current, y, header.Height);
            Unfilter(filter, current, previous, header.Channels);
            WriteRow(image, y, current, header.ColorType, palette);

            // 交换而非复制:反演结果成为下一行的「上一行」,旧上一行缓冲就地复用
            (previous, current) = (current, previous);
        }
    }

    /// <summary>读满一行扫描行数据;提前结束即视为文件损坏。</summary>
    private static void ReadExactly(Stream zlib, Span<byte> destination, int y, int height)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int count = zlib.Read(destination[read..]);
            if (count <= 0)
            {
                throw new InvalidImageException(
                    $"PNG 扫描行数据不足:第 {y} 行(共 {height} 行)需要 {destination.Length} 字节,"
                    + $"实际只解出 {read} 字节。");
            }

            read += count;
        }
    }

    /// <summary>
    /// 就地反演 PNG 行过滤。<b>当前行是原地反演的</b>:<c>[0, i)</c> 区间在读 <c>i</c> 时已是反演结果,
    /// 故 Sub/Average/Paeth 取左邻字节可直接读本行缓冲。
    /// </summary>
    /// <param name="filter">过滤类型 0~4。</param>
    /// <param name="row">本行数据,进入时为过滤后的差分值,返回时为原始像素字节。</param>
    /// <param name="previous">上一行<b>已反演</b>的像素字节;首行为全 0。</param>
    /// <param name="bpp">
    /// 每像素字节数,按<b>原始</b>色彩类型取(灰度 1、RGB 3、调色板 1、灰度+Alpha 2、RGBA 4)。
    /// 按目标内存格式取会让 Sub/Average/Paeth 从某列起整体错位。
    /// </param>
    private static void Unfilter(int filter, Span<byte> row, ReadOnlySpan<byte> previous, int bpp)
    {
        switch (filter)
        {
            case 0:
                // None:数据即像素,无需还原
                break;

            case 1:
                for (int i = bpp; i < row.Length; i++)
                {
                    row[i] = (byte)(row[i] + row[i - bpp]);
                }

                break;

            case 2:
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] = (byte)(row[i] + previous[i]);
                }

                break;

            case 3:
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bpp ? row[i - bpp] : 0;
                    row[i] = (byte)(row[i] + ((left + previous[i]) >> 1));
                }

                break;

            case 4:
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bpp ? row[i - bpp] : 0;
                    int up = previous[i];
                    int upLeft = i >= bpp ? previous[i - bpp] : 0;
                    row[i] = (byte)(row[i] + Paeth(left, up, upLeft));
                }

                break;

            default:
                // 调用方已在读 filter 字节时拒绝 5 及以上,此处不可达;保留是为了让未知取值
                // 永远以异常结束,而不是被某个兜底分支当作 None 静默放行
                throw new InvalidImageException($"PNG 行过滤类型 {filter} 未定义(有效值为 0~4)。");
        }
    }

    /// <summary>
    /// Paeth 预测器(PNG 规范 9.4):在左、上、左上三个邻居中取最接近
    /// <c>p = left + up - upLeft</c> 的一个。5 种过滤里唯一需要三分支比较的。
    /// </summary>
    private static int Paeth(int left, int up, int upLeft)
    {
        int p = left + up - upLeft;
        int distanceLeft = Math.Abs(p - left);
        int distanceUp = Math.Abs(p - up);
        int distanceUpLeft = Math.Abs(p - upLeft);

        if (distanceLeft <= distanceUp && distanceLeft <= distanceUpLeft)
        {
            return left;
        }

        return distanceUp <= distanceUpLeft ? up : upLeft;
    }

    /// <summary>
    /// 把一行原始像素落到目标 <see cref="ImageBuffer"/>。
    /// </summary>
    /// <remarks>
    /// 一律经 <see cref="ImageBuffer.SetPixel"/> 写入,由它按目标格式完成通道归一化 ——
    /// 每种色彩类型各手写一段字节拷贝看似更快,但那是 <c>bytesPerPixel</c> 与目标
    /// <see cref="ImageBuffer.Stride"/> 两套口径相遇的地方,一旦不一致就是越界写,
    /// 而且只在特定宽高下才暴露。
    /// </remarks>
    private static void WriteRow(ImageBuffer image, int y, ReadOnlySpan<byte> row, byte colorType, byte[]? palette)
    {
        switch (colorType)
        {
            case 0:
                for (int x = 0; x < image.Width; x++)
                {
                    byte gray = row[x];
                    image.SetPixel(x, y, new PixelColor(gray, gray, gray, 255));
                }

                break;

            case 2:
                for (int x = 0; x < image.Width; x++)
                {
                    int offset = x * 3;
                    image.SetPixel(x, y, new PixelColor(row[offset], row[offset + 1], row[offset + 2], 255));
                }

                break;

            case 3:
                for (int x = 0; x < image.Width; x++)
                {
                    int index = row[x];

                    // 调色板查表:漏掉这一步会解出索引值而非颜色,图像「有形状、颜色全错」
                    int offset = index * 3;
                    if (offset + 2 >= palette!.Length)
                    {
                        throw new InvalidImageException(
                            $"PNG 第 {y} 行第 {x} 列的调色板索引 {index} 越出 PLTE 表"
                            + $"(共 {palette.Length / 3} 项)。");
                    }

                    image.SetPixel(x, y, new PixelColor(palette[offset], palette[offset + 1], palette[offset + 2], 255));
                }

                break;

            case 4:
                for (int x = 0; x < image.Width; x++)
                {
                    // 灰度 + Alpha 在现有 5 种内存格式中<b>没有对应项</b>,必须展开为 Rgba32:
                    // 每像素 2 字节被当作 1 字节(灰度)或 4 字节(RGBA)读都会让整行错位
                    int offset = x * 2;
                    byte gray = row[offset];
                    image.SetPixel(x, y, new PixelColor(gray, gray, gray, row[offset + 1]));
                }

                break;

            case 6:
                for (int x = 0; x < image.Width; x++)
                {
                    int offset = x * 4;
                    image.SetPixel(x, y, new PixelColor(row[offset], row[offset + 1], row[offset + 2], row[offset + 3]));
                }

                break;

            default:
                throw new UnsupportedImageException($"不支持的 PNG 色彩类型 {colorType}。");
        }
    }

    // ————————————————————————— 共用校验 —————————————————————————

    /// <summary>校验一个块的 CRC-32(覆盖范围是「类型 + 数据」,不含长度字段)。</summary>
    private static void ValidateChunkCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data, ReadOnlySpan<byte> stored)
    {
        uint expected = BinaryPrimitives.ReadUInt32BigEndian(stored);
        uint actual = Crc32.Compute(type, data);

        if (expected != actual)
        {
            throw new InvalidImageException(
                $"块 {Encoding.ASCII.GetString(type)} 的 CRC-32 校验失败:文件记载 {expected:X8},实算 {actual:X8}。"
                + "图像数据已损坏。");
        }
    }

    /// <summary>求色彩类型对应的通道数(反 filter 的每像素字节数依据)。</summary>
    /// <exception cref="UnsupportedImageException">色彩类型不是 0/2/3/4/6。</exception>
    private static int ChannelsOf(byte colorType) => colorType switch
    {
        0 => 1,
        2 => 3,
        3 => 1,
        4 => 2,
        6 => 4,
        _ => throw new UnsupportedImageException(
            $"不支持的 PNG 色彩类型 {colorType}:本服务支持 0(灰度)/ 2(RGB)/ 3(调色板)"
            + "/ 4(灰度+Alpha)/ 6(RGBA)。")
    };

    /// <summary>求色彩类型解码后落入的内存像素格式。</summary>
    /// <exception cref="UnsupportedImageException">色彩类型不是 0/2/3/4/6。</exception>
    private static PixelFormat ResolveFormat(byte colorType) => colorType switch
    {
        0 => PixelFormat.Gray8,
        2 => PixelFormat.Rgb24,

        // 调色板展开为真彩:本服务没有「索引色」这一内存格式,查表展开是唯一的落点
        3 => PixelFormat.Rgb24,

        // 灰度 + Alpha 无对应的内存格式,展开为 Rgba32
        4 => PixelFormat.Rgba32,
        6 => PixelFormat.Rgba32,
        _ => throw new UnsupportedImageException(
            $"不支持的 PNG 色彩类型 {colorType}:本服务支持 0(灰度)/ 2(RGB)/ 3(调色板)"
            + "/ 4(灰度+Alpha)/ 6(RGBA)。")
    };
}

/// <summary>
/// 输入是图像,但属于本服务不支持的格式或特性(非 PNG 魔数、位深 / 交错 / 色彩类型 /
/// 压缩或滤波方法 / <c>tRNS</c> / 未知关键块)。
/// </summary>
/// <remarks>
/// 与 <see cref="InvalidImageException"/> 的分野是<b>调用方的可行动作</b>:
/// 本异常意味着「换一种输入」(重新导出为受支持的 PNG),后者意味着「这份文件坏了」(重新导出一份)。
/// 两者在工具层分别翻译为 <c>unsupported_image</c> 与 <c>invalid_image</c>。
/// </remarks>
public sealed class UnsupportedImageException(string message) : Exception(message);

/// <summary>
/// 输入是 PNG,但结构不合法:块结构错误、CRC-32 失败、zlib 解压失败、扫描行数据不足。
/// </summary>
public sealed class InvalidImageException(string message) : Exception(message);
