namespace DImage.Api.Mcp;

using DImage.Api.Imaging;

/// <summary>
/// 像素格式名 ↔ <see cref="PixelFormat"/> 的<b>单一事实源</b>:
/// <c>gray8</c> / <c>rgb24</c> / <c>bgr24</c> / <c>rgba32</c> / <c>bgra32</c>(大小写不敏感)。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有这个类型</b>:<see cref="ImageTools"/> 要用它解析 <c>image_create</c> 的入参、
/// 也要用它回填 <c>image_create</c> / <c>image_upload</c> 的结果;而 <c>image_crop</c> 需要
/// 把裁切结果的格式<b>报出来</b>。若在 <see cref="ShapeTools"/> 里复制一份 switch,两份必然会分叉 ——
/// 而分叉的那一天没有任何告警,只表现为「同一个格式在一个工具里叫 rgba32、在另一个里叫别的」,
/// 而格式名正是调用方拿去喂给其它工具的输入。
/// </para>
/// <para>
/// <b>两个方法必须同处一地</b>:<see cref="TryParseFormat"/> 与 <see cref="FormatNameOf"/> 互为逆运算,
/// 拆开放进两个类里,「解析接受的名字集合」与「回填产出的名字集合」就会各自漂移 ——
/// 表现是「工具报了 <c>xxx</c>,但另一个工具不接受 <c>xxx</c>」这一自相矛盾的契约。
/// </para>
/// <para>
/// <b>这是对 <see cref="ImageTools"/> 的有意最小重构</b>:只搬移了原本的两个私有静态方法,
/// 线上契约、行为、错误文案<b>逐字不变</b>。既有的五项工具因此不应有任何可观测变化,
/// 对它们的回归断言就是这条约束的防线。处置与任务 10 抽出 <see cref="ColorText"/> 完全同形。
/// </para>
/// </remarks>
internal static class PixelFormatText
{
    /// <summary>求像素格式的规范化对外名称(小写)。</summary>
    /// <exception cref="ArgumentOutOfRangeException">格式不是已定义的像素格式。</exception>
    public static string FormatNameOf(PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => "gray8",
        PixelFormat.Rgb24 => "rgb24",
        PixelFormat.Bgr24 => "bgr24",
        PixelFormat.Rgba32 => "rgba32",
        PixelFormat.Bgra32 => "bgra32",
        // 未定义格式必须立即失败:静默兜底会让调用方拿到一个它无法在后续调用中使用的格式名
        _ => throw new ArgumentOutOfRangeException(
            nameof(format), format, $"未知的像素格式({(int)format}),无法确定对外名称。")
    };

    /// <summary>
    /// 解析像素格式名(大小写不敏感)。
    /// </summary>
    /// <remarks>
    /// <b>刻意不用 <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/></b>:它对数字串同样返回成功,
    /// <c>"3"</c> 会被解析成 <c>Rgba32</c>,<c>"99"</c> 会解析出一个根本不存在的枚举值。
    /// 前者让「格式名」这一契约悄悄容纳了「格式序号」,后者则会让非法值一路流到像素寻址处。
    /// 白名单式匹配是唯一能把这两条都堵死的形式。
    /// </remarks>
    public static bool TryParseFormat(string name, out PixelFormat format)
    {
        switch (name.Trim().ToLowerInvariant())
        {
            case "gray8":
                format = PixelFormat.Gray8;
                return true;
            case "rgb24":
                format = PixelFormat.Rgb24;
                return true;
            case "bgr24":
                format = PixelFormat.Bgr24;
                return true;
            case "rgba32":
                format = PixelFormat.Rgba32;
                return true;
            case "bgra32":
                format = PixelFormat.Bgra32;
                return true;
            default:
                format = default;
                return false;
        }
    }
}
