namespace DImage.Api.Imaging;

/// <summary>
/// 一次裁切的输出布局:目标画布尺寸与「目标原点在源图坐标系中的位置」。
/// </summary>
/// <remarks>
/// <para>
/// 目标像素 <c>(dx, dy)</c> 对应的源像素恒为 <c>(dx + <see cref="OriginX"/>, dy + <see cref="OriginY"/>)</c>。
/// 保持源图尺寸时原点是 <c>(0, 0)</c>;收缩到包围盒时原点即包围盒左上角的整数化结果。
/// </para>
/// <para>
/// <b>由 <see cref="ImageCropper.Plan"/> 产出、由 <see cref="ImageCropper.Crop"/> 消费</b>,
/// 两者之间不重算 —— 布局是本能力域唯一一处「尺寸与原点怎样算」的实现。
/// 拆开算两次必然分叉,而分叉的表现是「分配的画布与写入的坐标对不上」:一部分像素越界丢弃、
/// 另一部分永远保持 0,不报任何错。
/// </para>
/// </remarks>
/// <param name="Width">目标画布宽度(像素),须大于 0。</param>
/// <param name="Height">目标画布高度(像素),须大于 0。</param>
/// <param name="OriginX">目标原点对应的源图横坐标,可落在源图之外。</param>
/// <param name="OriginY">目标原点对应的源图纵坐标,可落在源图之外。</param>
public readonly record struct CropLayout(int Width, int Height, int OriginX, int OriginY);

/// <summary>
/// 图像裁切的算法层入口:按一个<b>区域</b>从源图取像素,写入一张新的目标图。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类型是算法与协调的边界</b>,与 <see cref="ImageCompositor"/> / <see cref="ImageDraw"/> 同层同形:
/// 校验入参 → 纯计算 → 返回被写入像素数;<b>不加锁、不查表、不认识 Id</b> ——
/// 「哪个 Id 对应哪个缓冲区」「容量够不够」由 <see cref="ImageBufferStore.Crop"/> 负责。
/// </para>
/// <para>
/// <b>与 <see cref="ImageBuffer.Crop"/> 不是一回事,请勿合并</b>:后者是缓冲区层的
/// 「区域深拷贝」,要求区域<b>完全落在图内</b>(越界即抛 <see cref="ArgumentOutOfRangeException"/>);
/// 本类型是能力层的「裁切」,区域<b>可以部分或完全落在图外</b>,越界部分是裁剪语义、正常返回成功。
/// 二者名字相近而语义相反,合并会让「画布外的像素怎么办」这一分歧无处安放。
/// </para>
/// <para>
/// <b>被裁掉的部分置 0,不是留白也不是未定义</b>:目标缓冲区由调用方以
/// <see cref="ImageBuffer"/> 构造(全 0),本类型只写入覆盖率大于 0 的位置,故区域之外恒为 0 ——
/// 对 <c>gray8</c>/<c>rgb24</c>/<c>bgr24</c> 表现为黑色,对 <c>rgba32</c>/<c>bgra32</c> 表现为全透明。
/// 输出格式恒等于源图格式,<b>不引入任何隐式格式转换</b>。
/// </para>
/// <para>
/// <b>两条性能路径,同一份语义</b> —— 这是本类型最需要读懂的一处:
/// <list type="bullet">
///   <item><description><b>矩形快路径</b>:矩形区域的坐标在工具层被强制为整数,像素中心恰好落在区间内或外,
///   覆盖率恒为 0 或 1,故「按覆盖率混合」与「按行字节搬运」产出<b>逐字节相同</b>的结果。
///   后者只需行级 <see cref="Span{T}"/> 拷贝,在 <c>8192×8192</c> 上是毫秒量级;</description></item>
///   <item><description><b>覆盖率路径</b>:路径区域(以及坐标非整数的矩形)必须逐像素求覆盖率,
///   代价是同量级画布上约 6700 万次像素读写(与 <c>image_composite</c> 实测的约 21 秒同阶)。</description></item>
/// </list>
/// 分流的依据是<b>几何是否整数对齐</b>,而不是「调用方用的是哪种区域参数」——
/// 故一个坐标非整数的 <see cref="RectShape"/> 会自动回落到覆盖率路径,不会静默产出一张
/// 「参数看着合理、边缘却悄悄粗糙」的图。两条路径的等价性由验收中的交叉比对钉死。
/// </para>
/// <para>
/// <b>区域可以完全落在源图之外</b>:此时目标图照常产出(尺寸由布局决定),
/// 只是像素全为 0 且返回 <c>0</c> 个被写入像素 —— 与 <see cref="ShapeRasterizer"/> 的
/// 「形状整体落在画布外返回 0」是同一处置,与 <c>image_set_pixels</c> 的「越界即整批拒绝」
/// <b>刻意不同</b>。
/// </para>
/// </remarks>
public static class ImageCropper
{
    /// <summary>
    /// 校验区域与样式,并求出输出画布的尺寸与原点。<b>零分配、零写入</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 单独成为一步,是为了让注册表能<b>先预检容量、后分配内存</b>:
    /// 若把「算尺寸」与「分配」绑在一处,一次声明了超大包围盒的裁切请求就会先逼出巨额分配、
    /// 再被告知注册表装不下 —— 闸门形同虚设。同理,区域参数的全部失败形态都在此抛出,
    /// 故<b>参数非法时不会产生任何像素写入</b>。
    /// </para>
    /// <para>
    /// 尺寸超出 <see cref="ImageLimits"/> 时<b>不在此处自设一套校验</b>:本方法照常返回布局,
    /// 由 <see cref="ImageBuffer"/> 的构造校验以 <see cref="ArgumentOutOfRangeException"/> 报出 ——
    /// 尺寸上限的权威只有一处,在此重判必然会与之逐渐分叉。
    /// </para>
    /// </remarks>
    /// <param name="region">裁切区域(矩形或路径),须已由调用方保证非 <c>null</c>。</param>
    /// <param name="sourceWidth">源图宽度(像素),须来自一个存活的缓冲区。</param>
    /// <param name="sourceHeight">源图高度(像素),须来自一个存活的缓冲区。</param>
    /// <param name="style">裁切样式。</param>
    /// <returns>输出布局。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="region"/> 为 <c>null</c>。</exception>
    /// <exception cref="InvalidGeometryException">区域几何非法,或收缩到包围盒后尺寸不为正。</exception>
    /// <exception cref="PathSyntaxException"><c>d</c> 字符串存在语法错误。</exception>
    /// <exception cref="DrawingLimitExceededException">超出 <see cref="DrawingLimits"/> 的任一项上限。</exception>
    public static CropLayout Plan(Shape region, int sourceWidth, int sourceHeight, CropStyle style)
    {
        ArgumentNullException.ThrowIfNull(region);

        // 校验先于一切:路径的语法/上限错误、矩形的宽高非正都在这一步报出,
        // 此时尚未分配任何内存、也未写入任何像素
        region.Validate();

        if (!style.ToBounds)
        {
            // 保持源图尺寸:原点与源图重合,故裁切结果可与其它按源图坐标绘制的工具直接叠加
            return new CropLayout(sourceWidth, sourceHeight, 0, 0);
        }

        // 矩形与路径共用同一套包围盒规则(矩形的包围盒就是它自己),故此处不需要按区域形态分支
        BoundsD bounds = PathGeometry.GetBounds(region.ToFigures());

        int originX = (int)Math.Floor(bounds.MinX);
        int originY = (int)Math.Floor(bounds.MinY);
        long width = (long)Math.Ceiling(bounds.MaxX) - originX;
        long height = (long)Math.Ceiling(bounds.MaxY) - originY;

        // 退化区域(扁平化为零面积,如一条水平线)的包围盒宽或高为 0:
        // 这不是「空裁切」而是「区域根本不成其为区域」,与形状校验拒绝零宽高是同一判定
        if (width <= 0 || height <= 0)
        {
            throw new InvalidGeometryException(
                $"裁切区域的包围盒宽高必须大于 0(区域需覆盖至少一个像素),"
                + $"实际包围盒为 [{bounds.MinX}, {bounds.MinY}] × [{bounds.MaxX}, {bounds.MaxY}],"
                + $"取整后 {width} × {height}。");
        }

        // 包围盒坐标已受 MaxCoordinateMagnitude(1e7)约束,故两值均在 int 范围内;
        // 真正的尺寸上限交由 ImageBuffer 判定(见方法注释)
        return new CropLayout((int)width, (int)height, originX, originY);
    }

    /// <summary>
    /// 按 <paramref name="layout"/> 从 <paramref name="source"/> 取像素写入 <paramref name="destination"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>不校验、不分配</b>:区域与样式的校验已在 <see cref="Plan"/> 完成,布局也由它给出;
    /// 本方法只做「把像素搬过去」这一件事,故调用方须保证 <paramref name="destination"/>
    /// 与 <paramref name="layout"/> 一致、且其格式与源图相同。
    /// </para>
    /// <para>
    /// <b>目标图先被整体置 0</b>:即使传入的是一张已经有内容的缓冲区,区域之外也一定归零 ——
    /// 「被裁掉的部分是 0」是本能力的对外承诺,不该依赖「调用方恰好给了一张全 0 的新图」。
    /// 顺带也给覆盖率路径提供了必要前提:该路径<b>跳过</b>覆盖率为 0 的像素,从不主动写 0。
    /// </para>
    /// </remarks>
    /// <param name="source">源图,只读;在本次调用期间不得被改写。</param>
    /// <param name="destination">目标图,就地写入。</param>
    /// <param name="region">裁切区域。</param>
    /// <param name="style">裁切样式。</param>
    /// <param name="layout">输出布局,须与 <paramref name="destination"/> 一致。</param>
    /// <returns>被写入过(覆盖率大于 0 且源坐标落在源图内)的像素数。</returns>
    /// <exception cref="ArgumentNullException">任一引用参数为 <c>null</c>。</exception>
    public static int Crop(
        ImageBuffer source,
        ImageBuffer destination,
        Shape region,
        CropStyle style,
        CropLayout layout)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(region);

        destination.Clear();

        // 整数对齐的矩形走字节搬运快路径;非整数矩形与路径走覆盖率路径。
        // 分流依据是几何本身,不是「调用方用了哪个参数」—— 见类型注释的两条性能路径
        if (region is RectShape rect && IsIntegerAligned(rect))
        {
            return CropRect(source, destination, rect, layout);
        }

        return CropByCoverage(source, destination, region, style, layout);
    }

    /// <summary>
    /// 判定矩形是否整数对齐 —— 即覆盖率是否恒为 0 或 1。
    /// </summary>
    /// <remarks>
    /// <c>Math.Floor</c> 对 <c>±Infinity</c>/<c>NaN</c> 是良定义的,而矩形已过
    /// <see cref="RectShape.Validate"/> 的有限性与量级校验,故此处不会出现
    /// 「非有限值比较恒为 false 而被误判为对齐」的情形。
    /// </remarks>
    private static bool IsIntegerAligned(RectShape rect)
        => rect.X == Math.Floor(rect.X)
        && rect.Y == Math.Floor(rect.Y)
        && rect.Width == Math.Floor(rect.Width)
        && rect.Height == Math.Floor(rect.Height);

    /// <summary>
    /// 矩形区域的快路径:逐行搬运源图与目标图的交集,不做任何逐像素换算。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 目标列 <c>dx</c> 与源列 <c>sx</c> 满足 <c>sx = dx + OriginX</c>,故同一行内
    /// 「源区间」与「目标区间」等长、只差一个平移 —— 两端各切一刀即可整段拷贝。
    /// </para>
    /// <para>
    /// <b>区域范围取自 <paramref name="rect"/> 而非布局</b>:布局只说「目标画布多大、原点在哪」,
    /// 「保留哪些像素」始终由区域决定。保持源图尺寸时布局原点为 <c>(0,0)</c> 且尺寸与源图相同,
    /// 若误用布局当作区域,整张源图都会被原样搬过去 —— 一次「裁切等于复制」的静默失败。
    /// </para>
    /// <para>
    /// 交集为空(矩形整体落在源图外)时返回 0:这是合法的裁切结果,不是失败。
    /// </para>
    /// </remarks>
    private static int CropRect(ImageBuffer source, ImageBuffer destination, RectShape rect, CropLayout layout)
    {
        int bytesPerPixel = source.BytesPerPixel;

        // 区域在源图坐标系中的整数范围。可直接转 int:整数对齐已判定,且矩形四个分量连同
        // 右下角都过了量级校验(1e7),故 (int) 不会截断、右下角也不会溢出
        int regionX = (int)rect.X;
        int regionY = (int)rect.Y;
        int regionRight = regionX + (int)rect.Width;
        int regionBottom = regionY + (int)rect.Height;

        // 与源图求交 —— 交集之外的部分被裁掉,在目标图上即保持 0
        int firstSourceColumn = Math.Max(regionX, 0);
        int lastSourceColumn = Math.Min(regionRight, source.Width);
        int firstSourceRow = Math.Max(regionY, 0);
        int lastSourceRow = Math.Min(regionBottom, source.Height);

        if (firstSourceColumn >= lastSourceColumn || firstSourceRow >= lastSourceRow)
        {
            return 0;
        }

        // 目标坐标 = 源坐标 − 布局原点。二者与目标画布的相容性由 Plan 保证(见类型注释的调用约定),
        // 故此处不再重复裁剪 —— 真越界会由 GetRowSpan / Slice 抛异常,而不是静默写错位置
        int sourceOffset = firstSourceColumn * bytesPerPixel;
        int destinationOffset = (firstSourceColumn - layout.OriginX) * bytesPerPixel;
        int rowBytes = (lastSourceColumn - firstSourceColumn) * bytesPerPixel;

        int rows = lastSourceRow - firstSourceRow;
        for (int row = 0; row < rows; row++)
        {
            int sourceRow = firstSourceRow + row;
            int destinationRow = sourceRow - layout.OriginY;

            // 只搬有效像素区:源图的 Offset 与行尾填充由 GetRowSpan 自行处理,
            // 目标为紧排(本能力恒定新建目标),故两次切片等长
            source.GetRowSpan(sourceRow)
                .Slice(sourceOffset, rowBytes)
                .CopyTo(destination.GetRowSpan(destinationRow).Slice(destinationOffset, rowBytes));
        }

        // 快路径下每个被写入的像素覆盖率都是 1,故「写入像素数」即搬运的像素数
        return rowBytes / bytesPerPixel * rows;
    }

    /// <summary>
    /// 覆盖率路径:把区域折线平移到目标坐标系,逐 band 累积覆盖率后混合落笔。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>覆盖率由 <see cref="ShapeRasterizer.AccumulateFill"/> 提供</b>,本方法不自行实现扫描线求交 ——
    /// 那会让「填充规则、AA 部分覆盖率、同横坐标顶点合并、半开区间」这套语义出现第二份实现,
    /// 而两份实现的分叉不会报警,只会表现为「同一个路径在绘制与裁切下形状不同」。
    /// 平移是唯一需要的适配:光栅化器只认几何,而裁切的几何要落到<b>目标</b>的像素网格上。
    /// </para>
    /// <para>
    /// <b>混合用的是「按覆盖率衰减到透明黑」,不是「按覆盖率混到某种颜色上」</b>:
    /// <c>ImageBlend.Mix(dst, fg, c) = dst×(1−c) + fg×c</c>,故取 <c>dst = default(PixelColor)</c>(全 0)
    /// 即得 <c>源像素 × c</c> —— 正是「该像素有多大比例被保留」。
    /// <b>参数顺序写反会得到 <c>源像素 × (1−c)</c>**:区域内外完全颠倒,而图仍能正常导出、不报任何错。
    /// 这是本能力最容易被「顺手修正」成相反语义的一处,<b>请勿调整这两个实参的顺序</b>。
    /// </para>
    /// </remarks>
    private static int CropByCoverage(
        ImageBuffer source,
        ImageBuffer destination,
        Shape region,
        CropStyle style,
        CropLayout layout)
    {
        IReadOnlyList<PathFigure> figures = Translate(region.ToFigures(), layout.OriginX, layout.OriginY);
        if (figures.Count == 0)
        {
            return 0;
        }

        BoundsD bounds = PathGeometry.GetBounds(figures);

        // 先与目标画布求交再遍历:区域可以远在画布之外,让循环去跑那些必然被丢弃的像素
        // 是一条无谓的耗时路径(与 ShapeRasterizer 的包围盒约定一致)
        int firstRow = Math.Max(0, (int)Math.Floor(bounds.MinY));
        int lastRow = Math.Min(destination.Height, (int)Math.Ceiling(bounds.MaxY));
        int firstColumn = Math.Max(0, (int)Math.Floor(bounds.MinX));
        int lastColumn = Math.Min(destination.Width, (int)Math.Ceiling(bounds.MaxX));

        if (firstRow >= lastRow || firstColumn >= lastColumn)
        {
            return 0;
        }

        int bandHeight = Math.Min(DrawingLimits.CoverageBandHeight, destination.Height);
        var coverage = new ScanlineCoverage(destination.Width, bandHeight);
        int kept = 0;

        for (int bandStart = firstRow; bandStart < lastRow; bandStart += bandHeight)
        {
            int rows = Math.Min(bandHeight, lastRow - bandStart);

            // 缓冲在 band 之间复用(见扫描线覆盖率类型的分块说明);
            // 每个像素只属于一个 band,故「每像素只混合一次」的语义不受分块影响
            coverage.BeginBand(rows);

            ShapeRasterizer.AccumulateFill(coverage, figures, style.FillRule, style.Antialias, bandStart, rows);

            for (int row = 0; row < rows; row++)
            {
                int y = bandStart + row;
                int sourceY = y + layout.OriginY;

                if (sourceY < 0 || sourceY >= source.Height)
                {
                    continue;
                }

                for (int x = firstColumn; x < lastColumn; x++)
                {
                    float value = coverage.Get(x, row);

                    if (value <= 0f)
                    {
                        continue;
                    }

                    // 区域可以伸出源图:那一部分保持 0,不计入 kept。
                    // 判定用「否定式」而非 `sx < 0 || sx >= Width`,是为了让 NaN 也被收进跳过分支
                    // —— 虽然 OriginX 来自 int 运算不可能是 NaN,但这一写法与 ImageCompositor 保持一致,
                    // 后来者改动此处时不会因为「换成正说式」而悄悄放宽边界
                    int sourceX = x + layout.OriginX;

                    if (!(sourceX >= 0 && sourceX < source.Width))
                    {
                        continue;
                    }

                    kept++;

                    // 覆盖率按 ImageBlend 的契约落在 [0, 1],饱和到 1 与光栅化器落笔处同规
                    destination.SetPixel(
                        x,
                        y,
                        ImageBlend.Mix(default, source.GetPixel(sourceX, sourceY), value > 1f ? 1f : value));
                }
            }
        }

        return kept;
    }

    /// <summary>把折线子路径整体平移,使其落在目标图的坐标系中。</summary>
    /// <remarks>
    /// <see cref="PathFigure"/> 的顶点数组是只读暴露的,平移因此产生新的子路径对象。
    /// 这段分配发生在<b>校验之后、写入之前</b>,与覆盖率缓冲同属「已通过全部校验」的工作内存。
    /// </remarks>
    private static IReadOnlyList<PathFigure> Translate(IReadOnlyList<PathFigure> figures, int originX, int originY)
    {
        if (figures.Count == 0)
        {
            return figures;
        }

        var offset = new PointD(originX, originY);
        var translated = new PathFigure[figures.Count];

        for (int i = 0; i < figures.Count; i++)
        {
            PathFigure figure = figures[i];
            ReadOnlySpan<PointD> points = figure.Points;
            var shifted = new PointD[points.Length];

            for (int j = 0; j < points.Length; j++)
            {
                shifted[j] = points[j] - offset;
            }

            translated[i] = new PathFigure(shifted, figure.IsClosed);
        }

        return translated;
    }
}
