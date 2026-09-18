namespace DImage.Api.Imaging;

/// <summary>
/// CRC-32 校验(IEEE 802.3 多项式 <c>0xEDB88320</c> 的反射形式),PNG 各块共用。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么抽成本类型</b>:本实现原先嵌套在 <see cref="PngEncoder"/> 内(<c>private static class</c>),
/// 是编码侧的唯一实现。<see cref="PngDecoder"/> 必须<b>逐块校验 CRC</b> —— 解码侧一旦漏掉这一步,
/// 「文件损坏」就只能靠像素结果反推,而像素错乱与 CRC 失败是两类完全不同的成因。
/// 沿任务 10 抽出 <see cref="ColorText"/> 的先例,把 CRC-32 提升为编解码共用的内部类型。
/// </para>
/// <para>
/// <b>这是一次纯搬移</b>:算法、常量与查表构造与搬移前逐字一致,<see cref="PngEncoder"/> 的输出
/// <b>逐字节不变</b>。搬移时不得顺手改动多项式、初值或反射方式。
/// </para>
/// <para>
/// <b>可见性刻意留在 <c>internal</c></b>:它是 <c>Imaging/</c> 内部的实现细节,
/// 编解码两侧同处一个程序集,无需对外暴露。
/// </para>
/// <para>
/// <b>Adler-32 保持原位不动</b>:zlib 尾校在解码侧由 <see cref="System.IO.Compression.ZLibStream"/>
/// 负责,把它一并抽出等于为一份没有第二个消费者的实现扩大可见性。
/// </para>
/// </remarks>
internal static class Crc32
{
    /// <summary>反射形式的 IEEE 802.3 多项式。</summary>
    private const uint Polynomial = 0xEDB88320u;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    /// <summary>计算两段连续字节(块类型 + 块数据)的 CRC-32。</summary>
    /// <remarks>
    /// CRC 的计算范围是<b>类型与数据</b>,不含长度字段 —— 规范如此定义。
    /// </remarks>
    public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        uint crc = 0xFFFFFFFFu;
        crc = Accumulate(crc, first);
        crc = Accumulate(crc, second);

        // 输入反射、输出反射:初值与终值都要取反,否则结果与规范不符
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Accumulate(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        }

        return crc;
    }
}
