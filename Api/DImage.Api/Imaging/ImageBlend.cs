namespace DImage.Api.Imaging;

/// <summary>
/// 混合原语:按覆盖率在底色与前景色之间做线性插值。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类型是「混合」语义在项目内的唯一实现</b>。它原先是一段嵌套在
/// <see cref="ShapeRasterizer"/> 内的私有方法,任务 16 新增图像合成时被抽出 ——
/// 抽取而非复制,是因为混合正是本能力域<b>最易出错又最不易被发现</b>的一处:
/// 写成不同的公式不会有任何编译或运行错误,只会让同一份几何在不同工具下产出不同颜色。
/// </para>
/// <para>
/// <b>语义是逐通道线性插值,不是 Porter-Duff <c>source-over</c></b>:
/// <c>dst' = dst × (1 − c) + foreground × c</c>,<b>Alpha 通道一并参与插值</b>。
/// 故「全透明画布 + 半透明前景」得到的是<b>半透明的前景色</b>,而非原色
/// (例:目标 <c>rgba32</c> 全透明、前景 <c>#0000FF80</c>、覆盖率 0.5 →
/// 得 <c>(0,0,128,128)</c>,标准 <c>source-over</c> 会得 <c>(0,0,255,128)</c>)。
/// </para>
/// <para>
/// <b>为什么保留该取向而不改成标准 alpha 合成</b>:既有 6 项绘制工具在透明画布上就是这个结果,
/// 且任务 14 已把它固化为验收基线(<c>rgba32</c> 画布初值为全透明黑,AA 覆盖率断言必须量 alpha)。
/// 让「绘制」与「合成」使用两套 alpha 语义,代价远高于承受一处语义的已知偏向 ——
/// 前者会让「画上去」与「贴上去」在同一张图上产生肉眼可辨的差异,而调用方无从预期。
/// <b>请勿按字面把它「修正」为 source-over。</b>
/// </para>
/// <para>
/// <b>调用方须自行把覆盖率钳到 <c>[0, 1]</c></b>:<see cref="ShapeRasterizer"/> 在调用前对
/// 累积覆盖率做饱和,<see cref="ImageCompositor"/> 则由 <c>alpha/255</c> 与 <c>opacity ≤ 1</c>
/// 天然保证。本方法只负责插值,不重复承担饱和职责 —— 两处都判必然逐渐分叉。
/// </para>
/// <para>
/// <b>格式分支为零</b>:进出都是归一化的 <see cref="PixelColor"/>,5 种像素格式的通道序
/// 与 Gray8 的 BT.601 折叠全部由 <see cref="ImageBuffer"/> 承担。代价是 Gray8 下混合发生在
/// <b>折叠后的亮度</b>上,连续两次半覆盖混合存在 8 位量化损失 —— 这是既有设计(任务 10)的
/// 已知代价,不是本类型引入的。
/// </para>
/// </remarks>
internal static class ImageBlend
{
    /// <summary>
    /// 按覆盖率在前景色与底色之间做线性插值,逐通道(含 Alpha)。
    /// </summary>
    /// <param name="destination">底色,即目标图像上的当前像素。</param>
    /// <param name="foreground">前景色,即要落上去的颜色。</param>
    /// <param name="coverage">覆盖率:<c>0</c> 得底色、<c>1</c> 得前景色。须已落在 <c>[0, 1]</c>。</param>
    /// <returns>混合后的像素颜色。</returns>
    internal static PixelColor Mix(PixelColor destination, PixelColor foreground, float coverage)
    {
        // `+ 0.5f` 再取整是四舍五入:`(int)` 的截断会让 127.5 落到 127,
        // 使一个理论上对称的混合在两端出现系统性偏暗 —— 单次看不出来,累积上千像素即显形
        static byte Lerp(byte from, byte to, float t)
            => (byte)Math.Clamp((int)(from + (to - from) * t + 0.5f), 0, 255);

        return new PixelColor(
            Lerp(destination.R, foreground.R, coverage),
            Lerp(destination.G, foreground.G, coverage),
            Lerp(destination.B, foreground.B, coverage),
            Lerp(destination.A, foreground.A, coverage));
    }
}
