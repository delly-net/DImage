namespace DImage.Api.Imaging;

/// <summary>
/// 决定「一个点算不算在多边形内部」的填充规则,语义与 SVG 的 <c>fill-rule</c> 一致。
/// </summary>
/// <remarks>
/// <para>
/// 枚举值采用<b>显式编号</b>,沿用 <see cref="PixelFormat"/> 的教训:这些取值会成为对外契约
/// (工具入参 <c>fill_rule</c> 的语义)的一部分,显式写死可避免日后在枚举中间插入新规则时,
/// 悄无声息地改变既有规则的数值。
/// </para>
/// <para>
/// 两条规则的差别只在<b>自相交或带洞</b>的图形上显现:简单凸多边形无论用哪条都得到同一结果。
/// </para>
/// </remarks>
public enum FillRule
{
    /// <summary>
    /// 环绕数非零即为内部。环绕数按每条边的方向累计(向下为 +1、向上为 -1),
    /// 是 SVG 与 <c>System.Drawing</c> 等绝大多数实现的默认值。
    /// </summary>
    NonZero = 0,

    /// <summary>
    /// 交点计数为奇数即为内部。等价于「穿过边界奇数次」的区域被填充,
    /// 自相交图形的重叠部分会因此成为空洞。
    /// </summary>
    EvenOdd = 1
}

/// <summary>
/// <see cref="FillRule"/> 的名称解析与规范化。
/// </summary>
/// <remarks>
/// 与 <c>ImageTools.TryParseFormat</c> 同样的理由:<b>刻意不用
/// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/></b> —— 它对数字串同样返回成功,
/// <c>"1"</c> 会被解析成 <see cref="FillRule.EvenOdd"/>,<c>"99"</c> 会解析出一个根本不存在的枚举值,
/// 而未知枚举值一旦流进光栅化器,表现是「整幅图零覆盖率」这类没有任何告警的静默错误。
/// 白名单式匹配是唯一能把这两条都堵死的形式。
/// </remarks>
public static class FillRuleExtensions
{
    /// <summary>解析填充规则名(大小写不敏感),仅接受 <c>nonzero</c> 与 <c>evenodd</c>。</summary>
    /// <param name="name">待解析的名称。</param>
    /// <param name="fillRule">解析成功时写入结果。</param>
    /// <returns>是否为可识别的填充规则名。</returns>
    public static bool TryParseFillRule(string? name, out FillRule fillRule)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case "nonzero":
                fillRule = FillRule.NonZero;
                return true;
            case "evenodd":
                fillRule = FillRule.EvenOdd;
                return true;
            default:
                fillRule = default;
                return false;
        }
    }

    /// <summary>把填充规则渲染为对外的规范名称(小写)。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fillRule"/> 不是已定义的填充规则。</exception>
    public static string ToWireName(this FillRule fillRule) => fillRule switch
    {
        FillRule.NonZero => "nonzero",
        FillRule.EvenOdd => "evenodd",
        // 未定义值必须立即失败,理由同 PixelFormatExtensions:兜底分支会让未知取值一路流到光栅化器
        _ => throw new ArgumentOutOfRangeException(
            nameof(fillRule), fillRule, $"未知的填充规则({(int)fillRule}),无法确定对外名称。")
    };
}
