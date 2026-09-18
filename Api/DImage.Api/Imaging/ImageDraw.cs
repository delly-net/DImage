namespace DImage.Api.Imaging;

/// <summary>
/// 绘制能力的算法层入口:把形状与样式落到一个 <see cref="ImageBuffer"/> 上。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类型是 <c>Imaging/</c> 内唯一对外的绘制入口</b>,也是这一层错误契约的定义处 ——
/// 下面那组异常即「绘制失败的全部形态」,工具层按类型逐一映射为对外的错误码。
/// </para>
/// <para>
/// <b>只提供统一的 <c>Draw(image, shape, style)</c>,不提供</b><c>DrawRect</c> /
/// <c>DrawLine</c> 之类的逐图形重载:重载的唯一作用是省掉调用方拼装形状的一行代码,
/// 代价却是五份「形状一旦新增就要同步补齐」的公开 API ——
/// 而形状与样式本就已是纯值对象,拼装它们没有任何门槛。
/// </para>
/// <para>
/// <b>调用方不应直接使用本类型</b>:MCP 工具一律经 <see cref="ImageBufferStore.Draw"/> 调用,
/// 以便在条目锁内完成校验与绘制。本类型是 public 的,是为了让算法层能脱离注册表被独立复用与验证。
/// </para>
/// <para>
/// <b>两种「越界」语义必须分清</b>(这是本层最容易被误按直觉改动的地方):
/// <list type="bullet">
///   <item><description><b>报错</b> —— 颜色串非法、线宽非法、半径或宽高非正、顶点数不足、
///   <c>fill_rule</c> 非法、<c>d</c> 字符串语法错误、超上限、坐标非有限值或超量级、Id 不存在;</description></item>
///   <item><description><b>裁剪</b> —— 坐标是合法有限值但落在画布外(含部分在外):正常绘制,
///   画布内部分落像素、画布外部分丢弃,返回成功。</description></item>
/// </list>
/// 注意这与 <see cref="ImageBufferStore.SetPixels"/> 的「任一点越界即整批拒绝」<b>语义不同</b>。
/// 该差异是刻意选择的:几何图形天然会部分越出画布(画一条横穿画布的线),拒绝它没有意义;
/// 而逐像素写入的越界则几乎总是调用方算错了坐标。请勿按字面把两者统一。
/// </para>
/// </remarks>
public static class ImageDraw
{
    /// <summary>
    /// 把一个形状按给定样式绘制到图像上。
    /// </summary>
    /// <remarks>
    /// <b>参数非法时图像零改动</b>:样式与形状的校验都在写入任何像素之前完成,任一项失败即抛出。
    /// </remarks>
    /// <param name="image">目标缓冲区。</param>
    /// <param name="shape">待绘制的形状。</param>
    /// <param name="style">绘制样式。</param>
    /// <returns>被覆盖(覆盖率大于 0)的像素数;形状整体落在画布外时为 0。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> 或 <paramref name="shape"/> 为 <c>null</c>。</exception>
    /// <exception cref="InvalidStrokeWidthException">线宽非有限、非正或超上限。</exception>
    /// <exception cref="InvalidFillRuleException">填充规则不是已定义值。</exception>
    /// <exception cref="InvalidGeometryException">几何参数非法(顶点数不足、半径或宽高非正、坐标非有限值或超量级等)。</exception>
    /// <exception cref="PathSyntaxException"><c>d</c> 字符串存在语法错误。</exception>
    /// <exception cref="DrawingLimitExceededException">超出 <see cref="DrawingLimits"/> 的任一项上限。</exception>
    public static int Draw(ImageBuffer image, Shape shape, DrawStyle style)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(shape);

        // 先全量校验、后写入:两步校验都不触碰像素,故参数非法时图像逐字节不变
        style.Validate();
        shape.Validate();

        return ShapeRasterizer.Rasterize(image, shape.ToFigures(), style);
    }
}

// —————————————————————— 绘制层的错误契约 ——————————————————————
//
// 下面这组异常是「绘制失败」的全部形态,由 ImageDraw / Shapes / SvgPathParser / PathFlattener /
// ShapeRasterizer / DrawStyle 共同抛出,并被工具层按类型映射为对外的错误码。
//
// 刻意用「一个失败形态一个类型」而非「一个类型带一个枚举」:异常类型本身就是机器的判别依据,
// 工具层因此不需要维护一张「枚举值 → 错误码」的对照表 —— 那张表一旦漏掉一项,
// 表现是某个错误被归到了错误的码上,而调用方据此做出的处置也就跟着错了。

/// <summary>几何参数非法:顶点数不足、半径或宽高非正、坐标非有限值或超出量级上限、起止角只给其一等。</summary>
/// <remarks>对应对外错误码 <c>invalid_geometry</c>。</remarks>
public sealed class InvalidGeometryException(string message) : Exception(message);

/// <summary>线宽非法:非有限值、小于等于 0,或超过 <see cref="DrawingLimits.MaxStrokeWidth"/>。</summary>
/// <remarks>对应对外错误码 <c>invalid_stroke_width</c>。</remarks>
public sealed class InvalidStrokeWidthException(string message) : Exception(message);

/// <summary>填充规则非法:不是 <see cref="FillRule"/> 的已定义值。</summary>
/// <remarks>对应对外错误码 <c>invalid_fill_rule</c>。</remarks>
public sealed class InvalidFillRuleException(string message) : Exception(message);

/// <summary>SVG path 的 <c>d</c> 字符串存在语法错误;消息含字符偏移与出错片段。</summary>
/// <remarks>对应对外错误码 <c>invalid_path</c>。</remarks>
public sealed class PathSyntaxException(string message) : Exception(message);

/// <summary>超出 <see cref="DrawingLimits"/> 的任一项上限(<c>d</c> 长度、命令数、扁平化段数)。</summary>
/// <remarks>
/// 对应对外错误码 <c>limit_exceeded</c>。与 <see cref="PathSyntaxException"/> 分开的理由:
/// 二者的可行动作不同 —— 语法错误要改写法,超限要减小规模。
/// </remarks>
public sealed class DrawingLimitExceededException(string message) : Exception(message);
