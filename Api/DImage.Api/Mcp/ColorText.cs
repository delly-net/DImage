using System.Globalization;
using DImage.Api.Imaging;

namespace DImage.Api.Mcp;

/// <summary>
/// 颜色串解析的<b>单一事实源</b>:<c>#RRGGBB</c> 与 <c>#RRGGBBAA</c>(大小写不敏感)。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有这个类型</b>:绘制工具与既有的 <c>image_set_pixels</c> 都要解析同一种颜色串。
/// 若在 <see cref="ShapeTools"/> 里复制一份实现,两份必然会分叉 —— 而分叉的那一天没有任何告警,
/// 只表现为「同一个颜色串在两个工具里一个接受、一个拒绝」。
/// </para>
/// <para>
/// <b>这是对 <see cref="ImageTools"/> 的有意最小重构</b>:只搬移了原本的
/// <c>TryParseColor</c> / <c>IsHexDigit</c> / <c>ParseHexByte</c> 三个私有静态方法,
/// 线上契约、行为、错误文案<b>逐字不变</b>。既有的四项工具因此不应有任何可观测变化,
/// 对它们的回归断言就是这条约束的防线。
/// </para>
/// </remarks>
internal static class ColorText
{
    /// <summary>
    /// 解析 <c>#RRGGBB</c> 或 <c>#RRGGBBAA</c> 形式的颜色串(大小写不敏感)。
    /// </summary>
    /// <remarks>
    /// 逐字符先验十六进制再取值,而不是直接把子串交给
    /// <see cref="byte.TryParse(ReadOnlySpan{char}, NumberStyles, IFormatProvider, out byte)"/>:
    /// <see cref="NumberStyles.HexNumber"/> <b>允许前后空白</b>,形如 <c>"#1 2345"</c> 的输入会被
    /// 静默接受成另一个颜色。颜色解析是纯字符串处理,应当对非法输入<b>零容忍</b>。
    /// </remarks>
    /// <param name="text">待解析的颜色串。</param>
    /// <param name="color">解析成功时写入归一化颜色。</param>
    /// <returns>是否为合法的颜色串。</returns>
    internal static bool TryParseColor(string? text, out PixelColor color)
    {
        color = default;

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();

        // 必须带 '#'。允许省略会让 "123456" 这类任意六位串被当成颜色,
        // 而调用方很可能本意是别的编码(如十进制 RGB 元组)
        if (span.Length == 0 || span[0] != '#')
        {
            return false;
        }

        span = span[1..];
        if (span.Length is not (6 or 8))
        {
            return false;
        }

        foreach (char c in span)
        {
            if (!IsHexDigit(c))
            {
                return false;
            }
        }

        byte r = ParseHexByte(span[..2]);
        byte g = ParseHexByte(span[2..4]);
        byte b = ParseHexByte(span[4..6]);

        // 六位形式不带 Alpha 通道,按不透明处理 —— 与 ImageBuffer 对无 Alpha 格式的语义一致
        byte a = span.Length == 8 ? ParseHexByte(span[6..8]) : (byte)255;

        color = new PixelColor(r, g, b, a);
        return true;
    }

    /// <summary>判断字符是否为十六进制数字(仅 ASCII,不依赖区域设置)。</summary>
    private static bool IsHexDigit(char c)
        => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    /// <summary>把已确认为十六进制的两个字符解析为一个字节。</summary>
    private static byte ParseHexByte(ReadOnlySpan<char> hex)
        => byte.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
