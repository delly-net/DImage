namespace DImage.Api.Imaging;

/// <summary>
/// 图像合成的算法层入口:把一张图像按落点、缩放与不透明度叠加到另一张图像上。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类型是算法与协调的边界</b>,与 <see cref="ImageDraw"/> 同层同形:
/// 校验入参 → 纯计算 → 返回被覆盖像素数;<b>不加锁、不查表、不认识 Id</b> ——
/// 「哪个 Id 对应哪个缓冲区」「两个 Id 是不是同一个条目」由 <see cref="ImageBufferStore.Composite"/> 负责。
/// 这与 <see cref="ShapeRasterizer"/> 只认 <see cref="ImageBuffer"/> 是同一分工。
/// </para>
/// <para>
/// <b>为什么合成不能像文字那样做成一个 <see cref="Shape"/></b>:<see cref="Shape"/> 是纯值类型,
/// 其全部表达能力最终都要落到「几何 → 覆盖率」这条链上(<c>ToFigures</c> → 光栅化),
/// 而源图是<b>像素</b>,不是几何 —— 它无法被表达为任何一组图形。
/// 强行把它塞进形状体系,就要让 <see cref="Shape"/> 持有 <see cref="ImageBuffer"/> 引用,
/// 从而把「形状」从可自由复制、可跨线程传递的值对象,变成一个有生命周期的资源持有者。
/// 故此处另开一条算法入口,但<b>混合公式仍然只有一份</b>(见 <see cref="ImageBlend"/>)。
/// </para>
/// <para>
/// <b><paramref name="source"/> 必须是一份「本次调用期间不会被改写」的缓冲区。</b>
/// 若源与目标是<b>同一个</b> <see cref="ImageBuffer"/> 实例(同 Id 自合并),
/// 调用方<b>必须</b>先 <see cref="ImageBuffer.Clone"/> 出快照再传进来:本方法边读边写同一块像素,
/// 读到的是「已经被本次合成改过一半」的数据,结果取决于扫描顺序(自左向右、自上而下),
/// 表现为源图被<b>自身的像素质地拖尾涂抹</b> —— 一个不会报错、只会越看越怪的输出。
/// 该快照职责刻意留在注册表层:<see cref="ImageBufferStore.Composite"/> 是唯一知道
/// 「两个 Id 是否指向同一条目」的地方,算法层无从判断。
/// </para>
/// <para>
/// <b>输出范围恒被裁剪到目标画布内</b>,与既有的绘制工具一致:源图部分落在画布外时正常合成,
/// 画布外部分丢弃并返回成功,不报错(与 <c>image_set_pixels</c> 的越界即整批拒绝<b>语义不同</b>)。
/// 故无论缩放倍数多大,写入像素数的上界都是目标画布的像素总数。
/// </para>
/// <para>
/// <b>合成是非幂等的</b>:半透明叠加重复执行会逐次逼近源图颜色,
/// 故同一合成做两次的结果与做一次<b>不逐字节相同</b>。与绘制工具同理,不声明为可安全重试。
/// </para>
/// </remarks>
public static class ImageCompositor
{
    /// <summary>
    /// 按 <paramref name="style"/> 把 <paramref name="source"/> 合成到 <paramref name="destination"/> 上(就地修改目标)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>缩放算法是最近邻</b>:目标像素中心反向映射到源图坐标后取整(向下取整即该像素的左上角),
    /// 不做任何邻域加权。选它而非双线性,是因为本能力的其余部分(颜色、覆盖率、格式折叠)
    /// 全部是<b>确定性、可逐像素复现</b>的:放大后的图像必须能被逐像素断言,
    /// 而双线性会在边缘产生一连串无法用一条式子写清的中间色,让验收退化为「看起来差不多」。
    /// 代价是放大后的边缘呈锯齿状 —— 对「把图标贴到画布上」这类用法,锯齿是可预期的,
    /// 而不可断言才是真正的问题。
    /// </para>
    /// <para>
    /// <b>像素中心约定与绘制一致</b>:目标像素 <c>(x, y)</c> 的中心在 <c>(x + 0.5, y + 0.5)</c>,
    /// 故落点 <c>x = 0</c> 恰好让源图第 0 列铺满目标第 0 列,而不是偏移半个像素。
    /// 落点带亚像素时,该约定决定「半个像素落在哪边」—— 与 <see cref="ShapeRasterizer"/>
    /// 用同一套约定,是为了让「画上去的图形」与「贴上去的图像」边界对齐。
    /// </para>
    /// <para>
    /// <b>不透明度的落点是覆盖率,不是 Alpha 通道的覆写</b>:像素最终覆盖率为
    /// <c>opacity × 源像素 Alpha / 255</c>,再交给 <see cref="ImageBlend.Mix"/> 逐通道插值
    /// (Alpha 通道一并插值,详见其类型注释)。故 <c>opacity = 1</c> 且源像素不透明时,
    /// 结果<b>逐字节等于源像素</b>;而 <c>opacity = 0</c> 或源像素全透明时该像素完全不被触碰,
    /// 也<b>不计入</b>被覆盖像素数 —— 与绘制侧「覆盖率不大于 0 的像素不计」保持一致。
    /// </para>
    /// <para>
    /// <b>NaN 安全</b>:反向映射出的坐标先经 <c>!(v &gt;= 0 &amp;&amp; v &lt; 上限)</c> 判定再取整。
    /// 写成「否定式」而非 <c>v &lt; 0 || v &gt;= 上限</c> 是刻意的:NaN 参与任何比较恒为 <c>false</c>,
    /// 故只有这个写法能把 NaN 收进「跳过」分支;<c>(int)</c> 转换收到 NaN 是未定义行为。
    /// </para>
    /// </remarks>
    /// <param name="destination">目标图像,就地修改。</param>
    /// <param name="source">源图像,只读;<b>不得</b>与 <paramref name="destination"/> 为同一实例(见类型注释)。</param>
    /// <param name="style">合成样式(落点、缩放、不透明度)。</param>
    /// <returns>被覆盖(覆盖率大于 0)的目标像素数;源图完全落在画布外时为 <c>0</c>,且此时仍返回成功。</returns>
    /// <exception cref="InvalidGeometryException">落点非有限值或超出量级上限。</exception>
    /// <exception cref="InvalidScaleException">缩放倍数非有限值,或小于等于 0。</exception>
    /// <exception cref="InvalidOpacityException">不透明度非有限值,或落在 <c>[0, 1]</c> 之外。</exception>
    public static int Composite(ImageBuffer destination, ImageBuffer source, CompositeStyle style)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);

        // 先全量校验、后写入:校验不触碰像素,故参数非法时目标图像逐字节不变
        style.Validate();

        // 目标矩形先在 double 空间求交、再转 int:先转 int 再钳制会被 ±Infinity 与天文数字
        // 撞成未定义行为;而 Math.Min/Max 在 double 上对 ±Infinity 都是良定义的。
        // 这个范围是「真正会被写入的列/行」的超集,精确拒绝交给下面的逐像素判定 ——
        // 与 ShapeRasterizer「先取包围盒、再逐像素算覆盖率」是同一手法
        int firstColumn = (int)Math.Max(0.0, Math.Floor(style.X));
        int lastColumn = (int)Math.Min(destination.Width, Math.Ceiling(style.X + source.Width * style.ScaleX));
        int firstRow = (int)Math.Max(0.0, Math.Floor(style.Y));
        int lastRow = (int)Math.Min(destination.Height, Math.Ceiling(style.Y + source.Height * style.ScaleY));

        if (firstColumn >= lastColumn || firstRow >= lastRow)
        {
            // 源图整体落在画布外(或缩放到亚像素宽/高):合法的裁剪结果,不是失败
            return 0;
        }

        int covered = 0;

        for (int y = firstRow; y < lastRow; y++)
        {
            double sourceY = (y + 0.5 - style.Y) / style.ScaleY;

            if (!(sourceY >= 0 && sourceY < source.Height))
            {
                continue;
            }

            int sourceRow = (int)sourceY;

            for (int x = firstColumn; x < lastColumn; x++)
            {
                double sourceX = (x + 0.5 - style.X) / style.ScaleX;

                if (!(sourceX >= 0 && sourceX < source.Width))
                {
                    continue;
                }

                int sourceColumn = (int)sourceX;

                PixelColor sourcePixel = source.GetPixel(sourceColumn, sourceRow);

                // 逐通道插值需要的是「这一像素被前景盖住多少」,而源像素自身的 Alpha
                // 就是它「有多不透明」;再乘以调用方给的整体不透明度,即最终覆盖率。
                // opacity ≤ 1 且 alpha/255 ≤ 1,故此处必然落在 [0, 1],无需再饱和
                float coverage = (float)(style.Opacity * sourcePixel.A / 255.0);

                if (coverage <= 0f)
                {
                    continue;
                }

                covered++;

                destination.SetPixel(
                    x, y, ImageBlend.Mix(destination.GetPixel(x, y), sourcePixel, coverage));
            }
        }

        return covered;
    }
}

// —————————————————————— 合成层的错误契约 ——————————————————————
//
// 与绘制层同规:一个失败形态一个类型,异常类型本身就是工具层映射错误码的依据。
// 二者不复用 InvalidGeometryException / InvalidStrokeWidthException 的理由是
// 「可行动作不同」——合成失败要改的是落点/缩放/不透明度,绘制失败要改的是几何/线宽,
// 报错对象的差异应当体现在对外错误码上,调用方才能据此决定改哪一段调用代码。

/// <summary>缩放倍数非法:非有限值,或小于等于 0。</summary>
/// <remarks>对应对外错误码 <c>invalid_scale</c>。</remarks>
public sealed class InvalidScaleException(string message) : Exception(message);

/// <summary>不透明度非法:非有限值,或落在 <c>[0, 1]</c> 之外。</summary>
/// <remarks>对应对外错误码 <c>invalid_opacity</c>。</remarks>
public sealed class InvalidOpacityException(string message) : Exception(message);
