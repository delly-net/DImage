namespace DImage.Api.Imaging;

/// <summary>
/// 文本的水平对齐方式:决定「行起点 x」相对给定 <c>x</c> 的位置。
/// </summary>
/// <remarks>
/// <para>
/// 枚举值采用<b>显式编号</b>,沿用 <see cref="FillRule"/> / <see cref="PixelFormat"/> 的既有教训:
/// 这些取值会成为对外契约(工具入参 <c>anchor</c> 的语义)的一部分,显式写死可避免日后
/// 在枚举中间插入新取值时悄无声息地改变既有取值的数值。
/// </para>
/// <para>
/// <b>对齐是「逐行」的,不是「整段」的。</b>多行文本在 <see cref="Center"/> 下每行各自居中,
/// 于是各行左端并不齐平 —— 这正是「居中」应有的样子;若按整段最长行对齐,
/// 短行会整体偏在一侧。
/// </para>
/// </remarks>
public enum TextAnchor
{
    /// <summary>行首左端落在 <c>x</c>。</summary>
    Left = 0,

    /// <summary>行中心落在 <c>x</c>(即行起点为 <c>x − 行宽 / 2</c>)。</summary>
    Center = 1,

    /// <summary>行尾右端落在 <c>x</c>(即行起点为 <c>x − 行宽</c>)。</summary>
    Right = 2
}

/// <summary>
/// <see cref="TextAnchor"/> 的名称解析与规范化。
/// </summary>
/// <remarks>
/// 与 <see cref="FillRuleExtensions"/> 同样的理由:<b>刻意不用
/// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/></b> —— 它对数字串同样返回成功,
/// <c>"1"</c> 会被解析成 <see cref="TextAnchor.Center"/>,而调用方写 <c>"1"</c> 时
/// 几乎总是想表达别的东西。白名单式匹配是唯一能把「数字串」与「拼错的词」一起挡掉的形式。
/// </remarks>
public static class TextAnchorExtensions
{
    /// <summary>解析对齐名称(大小写不敏感),仅接受 <c>left</c>、<c>center</c>、<c>right</c>。</summary>
    /// <param name="name">待解析的名称。</param>
    /// <param name="anchor">解析成功时写入结果。</param>
    /// <returns>是否为可识别的对齐名称。</returns>
    public static bool TryParseAnchor(string? name, out TextAnchor anchor)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case "left":
                anchor = TextAnchor.Left;
                return true;
            case "center":
                anchor = TextAnchor.Center;
                return true;
            case "right":
                anchor = TextAnchor.Right;
                return true;
            default:
                anchor = default;
                return false;
        }
    }

    /// <summary>把对齐方式渲染为对外的规范名称(小写)。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="anchor"/> 不是已定义的对齐方式。</exception>
    public static string ToWireName(this TextAnchor anchor) => anchor switch
    {
        TextAnchor.Left => "left",
        TextAnchor.Center => "center",
        TextAnchor.Right => "right",
        // 未定义值必须立即失败:兜底分支会让未知取值一路流到居中断言里,表现为「文字偏了」而无告警
        _ => throw new ArgumentOutOfRangeException(
            nameof(anchor), anchor, $"未知的文本对齐方式({(int)anchor}),无法确定对外名称。")
    };
}
