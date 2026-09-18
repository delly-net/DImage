using System.Buffers.Binary;
using System.IO.Compression;

namespace DImage.Api.Imaging.Glyphs;

/// <summary>
/// 汉字字库:GB2312 一级 3755 字的单线笔画(medians)数据。
/// </summary>
/// <remarks>
/// <para>
/// <b>数据形态是「骨架」而不是「轮廓」。</b>取自 Make Me a Hanzi 的 <c>medians</c> 字段 ——
/// 即每一笔的中心线折线。按描边渲染得到的是单线笔画的字,<b>不是印刷体</b>;
/// 这是决策 #74 的既定取舍,不是缺陷。(同一份上游数据另有 <c>strokes</c> 轮廓字段,
/// 本项目的资源中<b>只取 medians</b>,体积与渲染代价都低一个数量级。)
/// </para>
/// <para>
/// <b>归一化是恒等的,这是选 1024 作 em 刻度的直接收益。</b>上游数据的原生网格正是
/// <c>1024×1024</c>、<c>y</c> 向上为正、基线在 <c>y = 0</c>,字身实际占据约
/// <c>y ∈ [−124, 900]</c>(基线下方 0.12 em、上方 0.88 em)—— 与「汉字底部略低于拉丁基线」的
/// 排版惯例天然吻合,故<b>无需任何缩放或基线补偿</b>,只需翻转 y。
/// </para>
/// <para>
/// <b>资源格式</b>:<c>DIMG</c> 魔数 + 版本 + em 刻度 + 码点表 + 偏移表 + 变长整数数据区,
/// 整体 gzip。<b>码点表升序</b>,故查字是二分而非哈希 —— 3755 项下两者的差距可以忽略,
/// 而升序数组可以顺带断言「无重复、无乱序」,这是哈希表给不了的。
/// </para>
/// <para>
/// <b>加载是惰性且一次性的</b>:首次真的用到文字能力时才解压,之后只保留解压后的字节块
/// (实测 0.64 MB)与两个整数索引数组,<b>不把 3755 个字形对象全部物化</b> ——
/// 那会产生数倍于数据本身的托管对象开销并常驻进程。字形按需从数据区解出,用完即弃。
/// </para>
/// <para>
/// <b>汉字数据不含中文标点</b>(<c>,。!?「」</c> 等)与全角字符 —— 它们会走缺字占位路径。
/// 这不是缺陷,但必须在工具文档中写明,否则会被反复当成 bug 排查。
/// </para>
/// </remarks>
internal static class HanziGlyphs
{
    /// <summary>内嵌的汉字字库资源文件名。</summary>
    /// <remarks>
    /// 文件名刻意只有<b>一段</b>扩展名(<c>.gz</c>),不写成 <c>hanzi-medians.bin.gz</c>:
    /// MSBuild 会把「看起来像 ISO-639 语言代码」的中间段当成 culture 剥掉
    /// (<c>bin</c> 恰是 Bini 语的三字母代码),于是清单名静默变成
    /// <c>…Glyphs.hanzi-medians.gz</c> —— 与按文件名后缀匹配的
    /// <see cref="EmbeddedResources.Open"/> 对不上,运行期才炸。
    /// 详见 <c>DImage.Api.csproj</c> 中该 <c>EmbeddedResource</c> 项的注释。
    /// </remarks>
    private const string ResourceFileName = "hanzi-medians.gz";

    /// <summary>数据区魔数。</summary>
    private static readonly byte[] Magic = "DIMG"u8.ToArray();

    /// <summary>本类型支持的资源格式版本。</summary>
    private const byte SupportedVersion = 1;

    private static readonly Lazy<Loader> Cache =
        new(() => new Loader(EmbeddedResources.ReadAllBytes(ResourceFileName)), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>取一个汉字码点对应的字形。</summary>
    /// <param name="codePoint">Unicode 码点。</param>
    /// <param name="glyph">命中的字形;未命中时为 <c>null</c>。</param>
    /// <returns>是否命中。</returns>
    internal static bool TryGet(int codePoint, out StrokeGlyph glyph) => Cache.Value.TryGet(codePoint, out glyph!);

    /// <summary>字库收录的码点数,供覆盖率断言使用。</summary>
    internal static int Count => Cache.Value.Count;

    /// <summary>字库收录的全部码点(升序),供覆盖率断言使用。</summary>
    internal static ReadOnlySpan<int> CodePoints => Cache.Value.CodePoints;

    /// <summary>
    /// 资源加载器:持有解压后的字节块与索引,并按需解码单个字形。
    /// </summary>
    private sealed class Loader
    {
        private readonly int[] _codePoints;
        private readonly int[] _offsets;
        private readonly byte[] _data;
        private readonly int _dataStart;

        internal Loader(byte[] compressed)
        {
            byte[] raw = Decompress(compressed);
            const int headerLength = 4 + 1 + 2 + 4;

            if (raw.Length < headerLength || !raw.AsSpan(0, 4).SequenceEqual(Magic))
            {
                throw new InvalidOperationException(
                    $"汉字字库资源不是预期的 DIMG 格式(前 {Math.Min(raw.Length, 4)} 字节为 {Describe(raw)})。");
            }

            byte version = raw[4];
            if (version != SupportedVersion)
            {
                throw new InvalidOperationException(
                    $"汉字字库资源版本为 {version},本程序只支持 {SupportedVersion};请重新运行 tools/glyphgen/generate.py。");
            }

            int emUnits = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(5, 2));
            if (emUnits != TextLimits.EmUnitsPerEm)
            {
                throw new InvalidOperationException(
                    $"汉字字库资源的 em 刻度为 {emUnits},而 <see cref=\"TextLimits\"/> 约定为 {TextLimits.EmUnitsPerEm};"
                    + "二者不一致会让全部汉字的字号整体偏大或偏小。");
            }

            int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(7, 4)));
            if (count <= 0)
            {
                throw new InvalidOperationException($"汉字字库资源为空(码点数 {count})。");
            }

            long expected = headerLength + (long)count * 4 + ((long)count + 1) * 4;
            if (raw.Length < expected)
            {
                throw new InvalidOperationException(
                    $"汉字字库资源被截断:声明 {count} 个码点,至少需要 {expected} 字节,实际 {raw.Length} 字节。");
            }

            _codePoints = new int[count];
            for (int i = 0; i < count; i++)
            {
                _codePoints[i] = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(headerLength + i * 4, 4)));

                // 升序且无重复:造字脚本已保证,此处复核是因为「乱序」会让二分静默地查不到字,
                // 表现是个别汉字变成豆腐块,极难归因
                if (i > 0 && _codePoints[i] <= _codePoints[i - 1])
                {
                    throw new InvalidOperationException(
                        $"汉字字库资源的码点表未严格升序(下标 {i}:{_codePoints[i - 1]} → {_codePoints[i]})。");
                }
            }

            int offsetStart = headerLength + count * 4;
            _offsets = new int[count + 1];
            for (int i = 0; i <= count; i++)
            {
                _offsets[i] = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offsetStart + i * 4, 4)));
                if (i > 0 && _offsets[i] < _offsets[i - 1])
                {
                    throw new InvalidOperationException($"汉字字库资源的偏移表非单调(下标 {i})。");
                }
            }

            _dataStart = offsetStart + (count + 1) * 4;
            if (_dataStart + _offsets[count] > raw.Length)
            {
                throw new InvalidOperationException(
                    $"汉字字库资源的数据区越界:需要 {_dataStart + _offsets[count]} 字节,实际 {raw.Length} 字节。");
            }

            _data = raw;
        }

        internal int Count => _codePoints.Length;

        internal ReadOnlySpan<int> CodePoints => _codePoints;

        internal bool TryGet(int codePoint, out StrokeGlyph glyph)
        {
            int index = Array.BinarySearch(_codePoints, codePoint);
            if (index < 0)
            {
                glyph = null!;
                return false;
            }

            glyph = Decode(index);
            return true;
        }

        /// <summary>按索引从数据区解出一个字形。</summary>
        private StrokeGlyph Decode(int index)
        {
            var reader = new SpanReader(_data, _dataStart + _offsets[index], _dataStart + _offsets[index + 1]);

            int strokeCount = reader.ReadVarInt();
            if (strokeCount > TextLimits.MaxGlyphStrokes)
            {
                throw new DrawingLimitExceededException(
                    $"汉字字库中码点 {_codePoints[index]} 的笔画数为 {strokeCount},"
                    + $"超过单字上限 {TextLimits.MaxGlyphStrokes};字库资源可能已损坏。");
            }

            var strokes = new List<PointD[]>(strokeCount);
            for (int s = 0; s < strokeCount; s++)
            {
                int pointCount = reader.ReadVarInt();
                if (pointCount <= 0)
                {
                    continue;
                }

                var points = new PointD[pointCount];
                int x = 0;
                int y = 0;
                for (int p = 0; p < pointCount; p++)
                {
                    x += reader.ReadZigZag();
                    y += reader.ReadZigZag();

                    // y 翻转在此发生:上游 y 向上为正,而 PointD 约定 y 向下为正。
                    // 两套数据的翻转各自留在自己的加载器里,字形对象不带来源侧的方向假设
                    points[p] = new PointD(x, -y);
                }

                strokes.Add(points.Length == 1 ? [points[0], points[0]] : points);
            }

            // 步进恒为 1 em:汉字是全角的,每个字占且仅占一个 em 方格
            return new StrokeGlyph([.. strokes], TextLimits.EmUnitsPerEm);
        }

        /// <summary>解压 gzip 资源。</summary>
        private static byte[] Decompress(byte[] compressed)
        {
            using var source = new MemoryStream(compressed, writable: false);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var target = new MemoryStream(compressed.Length * 2);
            gzip.CopyTo(target);
            return target.ToArray();
        }

        /// <summary>把内容渲染成便于定位的形态。</summary>
        private static string Describe(byte[] raw)
        {
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < Math.Min(raw.Length, 8); i++)
            {
                text.Append(raw[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
            }

            return text.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// 数据区的游标:按变长整数读取交错存放的增量坐标。
    /// </summary>
    /// <remarks>
    /// 手写而非用 <c>BinaryReader</c>:后者没有变长整数原语,而变长整数正是这套资源
    /// 能压到 0.6 MB 的原因 —— 折线坐标的相邻增量绝大多数落在一个字节内。
    /// </remarks>
    private ref struct SpanReader
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _position;

        internal SpanReader(byte[] data, int start, int end)
        {
            _data = data;
            _position = start;
            _end = end;
        }

        /// <summary>读一个无符号变长整数(LEB128,每字节低 7 位、最高位为续读标志)。</summary>
        internal int ReadVarInt()
        {
            int result = 0;
            int shift = 0;

            while (true)
            {
                if (_position >= _end)
                {
                    throw new InvalidOperationException("汉字字库数据区在读取变长整数时越界。");
                }

                byte b = _data[_position++];
                result |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
                if (shift > 28)
                {
                    throw new InvalidOperationException("汉字字库数据区出现超长变长整数(超过 32 位)。");
                }
            }
        }

        /// <summary>读一个 zigzag 编码的有符号变长整数。</summary>
        internal int ReadZigZag()
        {
            int value = ReadVarInt();
            return (value >> 1) ^ -(value & 1);
        }
    }
}
