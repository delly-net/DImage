namespace DImage.Api.Imaging;

/// <summary>
/// 图像内存对象:以纯托管的 <see cref="byte"/> 数组承载原始二进制像素数据,提供创建、零拷贝切片、
/// 深拷贝裁剪、区域拷贝与填充等底层能力,作为自研图像处理算法的数据载体。
/// </summary>
/// <remarks>
/// <para>
/// <b>零第三方依赖</b>:不使用 <c>System.Drawing</c>/<c>ImageSharp</c>/<c>SkiaSharp</c>,
/// 不使用 <c>unsafe</c> 与 <c>P/Invoke</c>,一切访问经由 <see cref="Span{T}"/>。
/// 同理,本类型<b>不引用</b> <c>Results</c>、<c>IConfiguration</c> 等 Web 类型 ——
/// Imaging 层是依赖图的叶子,必须能脱离 ASP.NET Core 宿主独立复用,禁止反向依赖宿主。
/// </para>
/// <para>
/// <b>不实现 <see cref="IDisposable"/></b>:底层是纯托管数组,生命周期由 GC 完全接管,
/// 不存在任何需要显式释放的资源。实现 <see cref="IDisposable"/> 只能得到一个空的 <c>Dispose</c>,
/// 却会让调用方误以为「必须释放」并写出 <c>using</c> 包裹,还会掩盖真正的泄漏来源。
/// 这是刻意的选择,请勿「顺手补上」。
/// </para>
/// <para>
/// <b>行步长(<see cref="Stride"/>)是内建维度,不是可选优化</b>:<see cref="Slice"/> 返回的视图
/// 要与父对象共用同一底层数组,行尾填充正是子视图与父对象错位共存的前提;没有 Stride 就没有零拷贝切片。
/// </para>
/// <para>
/// <b>尺寸上限是安全要求</b>:所有尺寸乘积一律以 <c>long</c> 计算后再与 <see cref="ImageLimits"/> 比较。
/// 在 <c>int</c> 下 <c>50000 * 50000 * 4</c> 会回绕为正的小值(<c>1_410_065_408</c>),使校验形同虚设,
/// 最终按回绕后的长度分配数组、却按 <see cref="Width"/>/<see cref="Height"/> 计算行偏移,直接越界读写。
/// </para>
/// <para>
/// <b>底层数组不外泄</b>:仅经 <see cref="Span{T}"/> 暴露。调用方必须显式使用 <see cref="Wrap"/>
/// 或 Span 版 API 才能获得共享语义 —— 共享是「显式选择」,而非「意外泄漏」。
/// </para>
/// </remarks>
public sealed class ImageBuffer
{
    /// <summary>
    /// 底层字节数组。<b>视图与所有者共享同一实例</b>,故一律不得对外返回其引用。
    /// </summary>
    private readonly byte[] _buffer;

    /// <summary>图像宽度(像素)。</summary>
    public int Width { get; }

    /// <summary>图像高度(像素)。</summary>
    public int Height { get; }

    /// <summary>像素格式,决定每像素字节数与通道序。</summary>
    public PixelFormat Format { get; }

    /// <summary>行步长(字节/行),恒不小于 <see cref="MinStride"/>;每行超出 <see cref="MinStride"/> 的部分为填充字节。</summary>
    public int Stride { get; }

    /// <summary>每像素占用的字节数,由 <see cref="Format"/> 决定。</summary>
    public int BytesPerPixel { get; }

    /// <summary>紧排所需的行字节数,等于 <c>Width * BytesPerPixel</c>。</summary>
    public int MinStride { get; }

    /// <summary>像素总数,等于 <c>Width * Height</c>;以 <c>long</c> 表示,避免乘法溢出。</summary>
    public long PixelCount { get; }

    /// <summary>
    /// 有效像素占用的字节数,等于 <c>PixelCount * BytesPerPixel</c>。
    /// <b>不含行尾填充</b>,故可能小于底层数组长度。
    /// </summary>
    public long ByteLength { get; }

    /// <summary>本缓冲区可见区域在底层数组中的起始字节偏移。</summary>
    public int Offset { get; }

    /// <summary>
    /// 是否与外部共享底层数组(由 <see cref="Wrap"/> 或 <see cref="Slice"/> 产生)。
    /// 为 <c>true</c> 时,通过本对象写入会同步反映到外部数组或父视图。
    /// </summary>
    public bool IsView { get; }

    // —————————————————————————————— 创建 ——————————————————————————————

    /// <summary>按紧排(<c>Stride == MinStride</c>)分配一个全 0 的空白缓冲区。</summary>
    /// <param name="width">宽度(像素),须大于 0。</param>
    /// <param name="height">高度(像素),须大于 0。</param>
    /// <param name="format">像素格式。</param>
    /// <exception cref="ArgumentOutOfRangeException">尺寸非法、超出 <see cref="ImageLimits"/> 上限,或格式未定义。</exception>
    public ImageBuffer(int width, int height, PixelFormat format)
        : this(width, height, format, ResolveMinStride(width, height, format))
    {
    }

    /// <summary>
    /// 按显式行步长分配缓冲区。用于行对齐(如按 4 字节对齐的位图行),或为后续切片预留复用空间。
    /// </summary>
    /// <param name="width">宽度(像素),须大于 0。</param>
    /// <param name="height">高度(像素),须大于 0。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="stride">行步长(字节/行),须不小于 <c>Width * BytesPerPixel</c>。</param>
    /// <exception cref="ArgumentOutOfRangeException">尺寸或步长非法、超出上限,或格式未定义。</exception>
    public ImageBuffer(int width, int height, PixelFormat format, int stride)
        : this(null, width, height, format, stride, 0, isView: false)
    {
    }

    /// <summary>
    /// 从紧排的原始像素数据<b>复制</b>构造(数据归本对象所有,与 <paramref name="source"/> 不再关联)。
    /// </summary>
    /// <param name="width">宽度(像素),须大于 0。</param>
    /// <param name="height">高度(像素),须大于 0。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="source">
    /// 紧排的源像素数据。要求长度不小于 <c>width * height * bytesPerPixel</c>;
    /// <b>只按紧排校验,不接受带行填充的输入</b>(带填充数据请改用 <see cref="Wrap"/>),
    /// 多余部分被忽略,不会参与复制。
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">尺寸非法、超出上限,或格式未定义。</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> 长度不足。</exception>
    public ImageBuffer(int width, int height, PixelFormat format, ReadOnlySpan<byte> source)
        : this(
            CopyTight(width, height, format, source),
            width,
            height,
            format,
            ResolveMinStride(width, height, format),
            0,
            isView: false)
    {
    }

    /// <summary>
    /// 零拷贝包装一个已有的 <see cref="byte"/> 数组:不复制数据,双方共享同一底层缓冲区。
    /// 修改数组立即反映到本对象,反之亦然。
    /// </summary>
    /// <param name="buffer">底层数组,不会被复制,也不会被本对象持有所有权。</param>
    /// <param name="width">宽度(像素),须大于 0。</param>
    /// <param name="height">高度(像素),须大于 0。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="stride">行步长(字节/行),须不小于 <c>Width * BytesPerPixel</c>。</param>
    /// <param name="offset">首行起始位置在 <paramref name="buffer"/> 中的字节偏移,默认 0。</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> 为 <c>null</c>。</exception>
    /// <exception cref="ArgumentOutOfRangeException">尺寸、步长或偏移非法,或超出上限。</exception>
    /// <exception cref="ArgumentException"><paramref name="buffer"/> 长度不足以容纳按该偏移与步长排布的图像。</exception>
    public static ImageBuffer Wrap(
        byte[] buffer,
        int width,
        int height,
        PixelFormat format,
        int stride,
        int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return new ImageBuffer(buffer, width, height, format, stride, offset, isView: true);
    }

    /// <summary>
    /// 全部构造路径的唯一入口:完成校验、分配或接管缓冲区,并算出全部只读元数据。
    /// </summary>
    /// <param name="buffer">
    /// 已有的底层数组。为 <c>null</c> 时自行分配(自有缓冲区);
    /// 非 <c>null</c> 时为「复制构造 / 包装 / 切片」路径,直接接管传入数组。
    /// </param>
    /// <param name="isView">仅当接管的是外部数组时有效,标记 <see cref="IsView"/>。</param>
    private ImageBuffer(
        byte[]? buffer,
        int width,
        int height,
        PixelFormat format,
        int stride,
        int offset,
        bool isView)
    {
        // 未知格式在 GetBytesPerPixel 内立即抛出,先于任何数值比较
        int bytesPerPixel = format.GetBytesPerPixel();
        int minStride = ValidateGeometry(width, height, bytesPerPixel);
        ValidateStride(stride, minStride);
        long requiredLength = ValidateTotalLength(stride, height);

        if (buffer is null)
        {
            // 每一行都占用完整的 Stride 字节(末行同样如此,行尾填充照旧计入),
            // 因此所需长度为 stride * height —— 这样 GetRowSpan(y) 才能对任意 y 恒定返回 Stride 长度,
            // 逐行算法无需为「末行特别短」写特例。填充字节恒不属于有效像素(见 ByteLength)。
            _buffer = new byte[requiredLength];
            Offset = 0;
            IsView = false;
        }
        else
        {
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(offset), offset, $"偏移量必须大于等于 0,实际 {offset}。");
            }

            long requiredEnd = (long)offset + requiredLength;
            if (requiredEnd > buffer.Length)
            {
                throw new ArgumentException(
                    $"缓冲区长度不足:offset={offset} 时需要至少 {requiredEnd} 字节"
                    + $"(stride={stride}, height={height}, minStride={minStride}),实际 {buffer.Length}。",
                    nameof(buffer));
            }

            _buffer = buffer;
            Offset = offset;
            IsView = isView;
        }

        Width = width;
        Height = height;
        Format = format;
        Stride = stride;
        BytesPerPixel = bytesPerPixel;
        MinStride = minStride;
        PixelCount = (long)width * height;
        ByteLength = PixelCount * bytesPerPixel;
    }

    // —————————————————————————— 跨度访问 ——————————————————————————

    /// <summary>
    /// 获取第 <paramref name="y"/> 行的跨度,<b>长度恒等于 <see cref="Stride"/></b>(末行同样含行尾填充)。
    /// </summary>
    /// <remarks>
    /// 各行跨度互不重叠,首尾相接铺满底层数组。行内超出 <see cref="MinStride"/> 的字节是填充,
    /// 不属于有效像素,其内容不参与任何像素语义。
    /// </remarks>
    /// <param name="y">行号,取值范围 <c>[0, Height)</c>。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="y"/> 越界。</exception>
    public Span<byte> GetRowSpan(int y)
    {
        if (y < 0 || y >= Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(y), y, $"行号 y 必须落在 [0, {Height}) 内,实际 {y}。");
        }

        // 由构造期不变量保证:Offset + (Height-1)*Stride + Stride <= 底层数组长度(不超过 int 上限)
        return _buffer.AsSpan(Offset + y * Stride, Stride);
    }

    /// <summary>获取指定像素占用的跨度,长度恒等于 <see cref="BytesPerPixel"/>。</summary>
    /// <param name="x">列号,取值范围 <c>[0, Width)</c>。</param>
    /// <param name="y">行号,取值范围 <c>[0, Height)</c>。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="x"/> 或 <paramref name="y"/> 越界。</exception>
    public Span<byte> GetPixelSpan(int x, int y)
    {
        ValidateCoordinates(x, y);
        return _buffer.AsSpan(Offset + y * Stride + x * BytesPerPixel, BytesPerPixel);
    }

    /// <summary>
    /// 获取覆盖全部有效像素的连续跨度,长度等于 <see cref="ByteLength"/>。
    /// <b>仅紧排(含等宽切片)时可用</b>。
    /// </summary>
    /// <remarks>
    /// 带行填充或宽度小于父对象的切片,其像素区在底层数组中是被行尾填充<b>分段</b>的,
    /// 无法用单个连续 <see cref="Span{T}"/> 表示。此时本方法显式抛出 <see cref="InvalidOperationException"/>,
    /// <b>绝不</b>悄悄返回一个截断到 <c>Width * Height * BytesPerPixel</c> 的区间 ——
    /// 那会把后续几行的填充字节当作像素读出来,是典型的静默数据错误。
    /// 请改用 <see cref="GetRowSpan"/> 逐行访问。
    /// </remarks>
    /// <exception cref="InvalidOperationException">当前缓冲区不是紧排的。</exception>
    public Span<byte> GetPixelsSpan()
    {
        if (Stride != MinStride)
        {
            throw new InvalidOperationException(
                $"当前缓冲区的像素数据在底层数组中不连续(Stride={Stride}, MinStride={MinStride}),"
                + "无法用单个 Span 表示。请改用 GetRowSpan(y) 逐行访问。");
        }

        return _buffer.AsSpan(Offset, (int)ByteLength);
    }

    // ————————————————————————— 像素访问 —————————————————————————

    /// <summary>
    /// 读取指定像素,并按 <see cref="Format"/> 的通道序归一化为 RGBA。
    /// </summary>
    /// <remarks>
    /// 对不含 Alpha 通道的格式(Gray8/Rgb24/Bgr24),返回值的 <see cref="PixelColor.A"/> 恒为 255。
    /// </remarks>
    /// <param name="x">列号,取值范围 <c>[0, Width)</c>。</param>
    /// <param name="y">行号,取值范围 <c>[0, Height)</c>。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="x"/> 或 <paramref name="y"/> 越界。</exception>
    public PixelColor GetPixel(int x, int y) => ReadPixel(GetPixelSpan(x, y));

    /// <summary>
    /// 写入指定像素,按 <see cref="Format"/> 的通道序落到对应字节。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 对不含 Alpha 通道的格式(Gray8/Rgb24/Bgr24),<see cref="PixelColor.A"/> 被丢弃。
    /// </para>
    /// <para>
    /// 对 <see cref="PixelFormat.Gray8"/>,颜色按 <b>BT.601 整数权重亮度</b>折叠为单通道:
    /// <c>gray = (77*R + 150*G + 29*B) &gt;&gt; 8</c>。所有权重之和恰为 256,
    /// 故纯白仍映射为 255;读回时 R/G/B 三通道均为该灰度值。
    /// 这是<b>不可逆</b>的有损转换,彩色写进灰度格式后颜色信息即丢失,属于格式本身的语义而非缺陷。
    /// </para>
    /// </remarks>
    /// <param name="x">列号,取值范围 <c>[0, Width)</c>。</param>
    /// <param name="y">行号,取值范围 <c>[0, Height)</c>。</param>
    /// <param name="color">归一化颜色值。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="x"/> 或 <paramref name="y"/> 越界。</exception>
    public void SetPixel(int x, int y, PixelColor color) => WritePixel(GetPixelSpan(x, y), color);

    // ————————————————————————— 基础操作 —————————————————————————

    /// <summary>
    /// 截取子区域并返回<b>零拷贝视图</b>:与当前对象共享同一底层数组,不复制任何字节。
    /// </summary>
    /// <remarks>
    /// <b>对视图的写入会同步改变原对象</b>(反之亦然)。视图的 <see cref="Stride"/> 继承自父对象,
    /// 故宽度小于父对象的视图必然是「带填充」的,<see cref="GetPixelsSpan"/> 对其不可用。
    /// 需要独立副本请使用 <see cref="Crop"/> 或 <see cref="Clone"/> ——
    /// 二者与 <see cref="Slice"/> 的共享/复制语义差异是刻意设计,<b>请勿合并</b>:
    /// 一旦混淆,就会出现「以为改的是副本、实际改的是原图」的静默数据错误,且极难排查。
    /// </remarks>
    /// <param name="x">起始列号,须大于等于 0。</param>
    /// <param name="y">起始行号,须大于等于 0。</param>
    /// <param name="width">视图宽度,须大于 0 且 <c>x + width &lt;= Width</c>。</param>
    /// <param name="height">视图高度,须大于 0 且 <c>y + height &lt;= Height</c>。</param>
    /// <exception cref="ArgumentOutOfRangeException">区域参数非法或越界。</exception>
    public ImageBuffer Slice(int x, int y, int width, int height)
    {
        ValidateRegion(x, y, width, height);

        long offset = (long)Offset + (long)y * Stride + (long)x * BytesPerPixel;
        return new ImageBuffer(_buffer, width, height, Format, Stride, (int)offset, isView: true);
    }

    /// <summary>
    /// 裁剪子区域并返回<b>深拷贝</b>:结果是独立对象,自有缓冲区且紧排
    /// (<see cref="Stride"/> == <see cref="MinStride"/>)。
    /// </summary>
    /// <remarks>
    /// <b>修改结果不会影响原对象</b>。结果的紧排性是刻意的:若沿用父对象的 <see cref="Stride"/>
    /// 分配,虽不影响正确性,却会携带无意义的填充,使后续按紧排假设调用 <see cref="GetPixelsSpan"/> 的算法
    /// 直接抛异常。语义对比见 <see cref="Slice"/>。
    /// </remarks>
    /// <param name="x">起始列号,须大于等于 0。</param>
    /// <param name="y">起始行号,须大于等于 0。</param>
    /// <param name="width">裁剪宽度,须大于 0 且 <c>x + width &lt;= Width</c>。</param>
    /// <param name="height">裁剪高度,须大于 0 且 <c>y + height &lt;= Height</c>。</param>
    /// <exception cref="ArgumentOutOfRangeException">区域参数非法或越界。</exception>
    public ImageBuffer Crop(int x, int y, int width, int height)
    {
        ValidateRegion(x, y, width, height);

        var result = new ImageBuffer(width, height, Format);
        int rowBytes = result.MinStride;
        int columnOffset = x * BytesPerPixel;

        for (int row = 0; row < height; row++)
        {
            // 只复制有效像素区,不复制父对象的行尾填充
            GetRowSpan(y + row)[columnOffset..][..rowBytes].CopyTo(result.GetRowSpan(row));
        }

        return result;
    }

    /// <summary>
    /// 返回当前可见区域的<b>深拷贝</b>,结果为独立对象且紧排。
    /// </summary>
    /// <remarks>对紧排的原对象,结果与其逐字节相等;对视图或带填充对象,结果等价于紧排后的像素内容。</remarks>
    public ImageBuffer Clone() => Crop(0, 0, Width, Height);

    /// <summary>
    /// 将本缓冲区的有效像素逐行复制到目标缓冲区,双方各自遵循自身的 <see cref="Stride"/>
    /// (仅复制像素区,不复制行尾填充)。
    /// </summary>
    /// <remarks>
    /// 本缓冲区的像素区必须能完整放入目标:要求 <see cref="Width"/>/<see cref="Height"/>/<see cref="Format"/>
    /// 三者一致。
    /// </remarks>
    /// <param name="destination">目标缓冲区。</param>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> 为 <c>null</c>。</exception>
    /// <exception cref="ArgumentException">尺寸或格式与源不一致。</exception>
    public void CopyTo(ImageBuffer destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (destination.Width != Width
            || destination.Height != Height
            || destination.Format != Format)
        {
            throw new ArgumentException(
                $"目标缓冲区与源不一致:源 {Width}×{Height} {Format},"
                + $"目标 {destination.Width}×{destination.Height} {destination.Format}。",
                nameof(destination));
        }

        // 自拷贝是恒等变换,直接返回 —— 也避免同一段内存既作源又作目标
        if (ReferenceEquals(this, destination))
        {
            return;
        }

        for (int y = 0; y < Height; y++)
        {
            GetRowSpan(y)[..MinStride].CopyTo(destination.GetRowSpan(y));
        }
    }

    /// <summary>
    /// 以指定颜色填充全部有效像素。
    /// </summary>
    /// <remarks>
    /// 只写入像素区,不触碰行尾填充,也不会波及共享底层数组的其它视图。
    /// 对 <see cref="PixelFormat.Gray8"/>,颜色先按 BT.601 权重折叠为灰度,再写入。
    /// </remarks>
    /// <param name="color">填充颜色。</param>
    public void Fill(PixelColor color)
    {
        Span<byte> firstRow = GetRowSpan(0);

        // 先写入首像素,再以「字节长度倍增」把它在整行有效区内铺开(1→2→4→…),
        // 最后整行复制到其余各行。逐像素调用 SetPixel 在 4096×4096 量级意味着 1600 万次
        // 格式分支判断,这里把格式分支收敛为一次。
        WritePixel(firstRow[..BytesPerPixel], color);

        int written = BytesPerPixel;
        while (written < MinStride)
        {
            int chunk = Math.Min(written, MinStride - written);
            firstRow[..chunk].CopyTo(firstRow.Slice(written, chunk));
            written += chunk;
        }

        for (int y = 1; y < Height; y++)
        {
            firstRow[..MinStride].CopyTo(GetRowSpan(y));
        }
    }

    /// <summary>
    /// 将全部有效像素置 0(等价于填充 <c>RGBA = (0,0,0,0)</c>;对无 Alpha 的格式表现为全黑)。
    /// </summary>
    /// <remarks>
    /// 只清空本缓冲区的像素区,不触碰行尾填充,也不会波及共享底层数组的其它视图。
    /// </remarks>
    public void Clear() => Fill(default);

    // ————————————————————————— 校验与工具 —————————————————————————

    /// <summary>
    /// 在<b>不分配任何内存</b>的前提下,求出按该几何与格式构造缓冲区所需要的字节数,
    /// 并顺带完成全部几何与上限校验。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 供「先预检、后分配」的调用方使用(如上传路径在解码前撞注册表容量)。
    /// 返回值与随后的构造结果恒等 —— 校验规则与步长公式只有本类型一处实现,
    /// 调用方<b>不得</b>自行重算一份,那等于让同一条规则有两个可能分叉的副本。
    /// </para>
    /// <para>
    /// <b>可见性留在 <c>internal</c></b>:它是本类型自有缓冲区的分配口径,
    /// 不是对外契约的一部分,故不进入公开 API。
    /// </para>
    /// </remarks>
    /// <param name="width">宽度(像素)。</param>
    /// <param name="height">高度(像素)。</param>
    /// <param name="format">像素格式。</param>
    /// <returns>所需字节数,等于 <c>MinStride * height</c>(含行尾填充)。</returns>
    /// <exception cref="ArgumentOutOfRangeException">尺寸非法、超出 <see cref="ImageLimits"/> 上限,或格式未定义。</exception>
    internal static long ComputeByteLength(int width, int height, PixelFormat format)
    {
        // 未知格式在 GetBytesPerPixel 内立即抛出,先于任何数值比较
        int bytesPerPixel = format.GetBytesPerPixel();
        int minStride = ValidateGeometry(width, height, bytesPerPixel);
        ValidateTotalLength(minStride, height);
        return (long)minStride * height;
    }

    /// <summary>校验宽高合法性,返回紧排行字节数(<see cref="MinStride"/>)。</summary>
    /// <remarks>
    /// 尺寸乘积一律以 <c>long</c> 计算后再比较:在 <c>int</c> 下乘法会回绕成一个小正值,
    /// 使上限校验彻底失效,这正是「整数溢出」这条头号风险的具体形态。
    /// </remarks>
    private static int ValidateGeometry(int width, int height, int bytesPerPixel)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, $"宽度必须大于 0,实际 {width}。");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, $"高度必须大于 0,实际 {height}。");
        }

        if (width > ImageLimits.MaxDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), width, $"宽度不得超过 {ImageLimits.MaxDimension},实际 {width}。");
        }

        if (height > ImageLimits.MaxDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(height), height, $"高度不得超过 {ImageLimits.MaxDimension},实际 {height}。");
        }

        long pixelCount = (long)width * height;
        if (pixelCount > ImageLimits.MaxPixelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                $"像素总数 {pixelCount} 超过上限 {ImageLimits.MaxPixelCount}"
                + $"(width={width}, height={height})。");
        }

        return (int)((long)width * bytesPerPixel);
    }

    /// <summary>校验行步长不小于紧排所需字节数。</summary>
    private static void ValidateStride(int stride, int minStride)
    {
        if (stride < minStride)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stride), stride, $"要求 stride >= {minStride},实际 {stride}。");
        }
    }

    /// <summary>校验「每行占用完整 Stride」所要求的底层数组长度,返回该长度。</summary>
    /// <remarks>
    /// 本实现中 <see cref="GetRowSpan"/> 对<b>任意行(含末行)</b>都返回 <see cref="Stride"/> 长度,
    /// 因此所需长度为 <c>stride * height</c>。若沿用「末行只保留有效像素」的
    /// <c>stride * (height - 1) + minStride</c>,末行的行跨度将越出缓冲区 ——
    /// 逐行算法一旦读到末行就会越界。二者在紧排时完全等价,仅在带填充时相差一行填充。
    /// </remarks>
    private static long ValidateTotalLength(int stride, int height)
    {
        long requiredLength = (long)stride * height;
        if (requiredLength > ImageLimits.MaxByteLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stride),
                stride,
                $"所需缓冲区长度 {requiredLength} 超过上限 {ImageLimits.MaxByteLength}"
                + $"(stride={stride}, height={height})。");
        }

        return requiredLength;
    }

    /// <summary>求出紧排行字节数,顺带完成宽高校验(供构造函数链在分配前调用)。</summary>
    private static int ResolveMinStride(int width, int height, PixelFormat format)
        => ValidateGeometry(width, height, format.GetBytesPerPixel());

    /// <summary>按紧排校验 <paramref name="source"/> 并复制为自有的底层数组。</summary>
    private static byte[] CopyTight(int width, int height, PixelFormat format, ReadOnlySpan<byte> source)
    {
        int bytesPerPixel = format.GetBytesPerPixel();
        int minStride = ValidateGeometry(width, height, bytesPerPixel);
        long requiredLength = (long)minStride * height;

        if (requiredLength > ImageLimits.MaxByteLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                $"所需缓冲区长度 {requiredLength} 超过上限 {ImageLimits.MaxByteLength}"
                + $"(width={width}, height={height}, bytesPerPixel={bytesPerPixel})。");
        }

        if (source.Length < requiredLength)
        {
            throw new ArgumentException(
                $"source 长度不足:按紧排需要 {requiredLength} 字节"
                + $"(width={width}, height={height}, bytesPerPixel={bytesPerPixel}),实际 {source.Length}。",
                nameof(source));
        }

        var buffer = new byte[requiredLength];
        source[..(int)requiredLength].CopyTo(buffer);
        return buffer;
    }

    /// <summary>校验像素坐标落在图像范围内。</summary>
    private void ValidateCoordinates(int x, int y)
    {
        if (x < 0 || x >= Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, $"列号 x 必须落在 [0, {Width}) 内,实际 {x}。");
        }

        if (y < 0 || y >= Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y), y, $"行号 y 必须落在 [0, {Height}) 内,实际 {y}。");
        }
    }

    /// <summary>校验子区域参数(供 <see cref="Slice"/> 与 <see cref="Crop"/> 共用)。</summary>
    private void ValidateRegion(int x, int y, int width, int height)
    {
        if (x < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, $"起始列号必须大于等于 0,实际 {x}。");
        }

        if (y < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(y), y, $"起始行号必须大于等于 0,实际 {y}。");
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, $"区域宽度必须大于 0,实际 {width}。");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, $"区域高度必须大于 0,实际 {height}。");
        }

        // 以 long 相加,避免 x + width 在极端入参下回绕
        if ((long)x + width > Width)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), width, $"x + width 不得超过 {Width}(x={x}, width={width})。");
        }

        if ((long)y + height > Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(height), height, $"y + height 不得超过 {Height}(y={y}, height={height})。");
        }
    }

    /// <summary>按当前格式的通道序把底层字节读成归一化颜色。</summary>
    /// <remarks>
    /// 不含 Alpha 通道的格式在读取时补 255 —— 归一化后的 RGBA 语义恒为「不透明」。
    /// </remarks>
    private PixelColor ReadPixel(ReadOnlySpan<byte> pixel) => Format switch
    {
        PixelFormat.Gray8 => new PixelColor(pixel[0], pixel[0], pixel[0], 255),
        PixelFormat.Rgb24 => new PixelColor(pixel[0], pixel[1], pixel[2], 255),
        PixelFormat.Bgr24 => new PixelColor(pixel[2], pixel[1], pixel[0], 255),
        PixelFormat.Rgba32 => new PixelColor(pixel[0], pixel[1], pixel[2], pixel[3]),
        PixelFormat.Bgra32 => new PixelColor(pixel[2], pixel[1], pixel[0], pixel[3]),
        // Format 在构造期已校验,此分支不可达;保留是为了让「未知格式」永远以异常结束,
        // 而不是被某个兜底分支静默按 4 字节处理
        _ => throw new ArgumentOutOfRangeException(
            nameof(Format), Format, $"未知的像素格式({(int)Format}),无法读取像素。")
    };

    /// <summary>按当前格式的通道序把归一化颜色写入底层字节。</summary>
    private void WritePixel(Span<byte> pixel, PixelColor color)
    {
        switch (Format)
        {
            case PixelFormat.Gray8:
                // BT.601 整数权重亮度:77+150+29 = 256,纯白仍为 255。
                // 该权重是既定契约,不是随手取的近似值 —— 换权重会让同一份代码产出不同的灰度结果。
                pixel[0] = (byte)((77 * color.R + 150 * color.G + 29 * color.B) >> 8);
                break;

            case PixelFormat.Rgb24:
            case PixelFormat.Rgba32:
                pixel[0] = color.R;
                pixel[1] = color.G;
                pixel[2] = color.B;
                break;

            case PixelFormat.Bgr24:
            case PixelFormat.Bgra32:
                pixel[0] = color.B;
                pixel[1] = color.G;
                pixel[2] = color.R;
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(Format), Format, $"未知的像素格式({(int)Format}),无法写入像素。");
        }

        if (BytesPerPixel == 4)
        {
            // 仅 Rgba32/Bgra32 会走到这里:二者的 Alpha 都落在第 4 字节
            pixel[3] = color.A;
        }
    }
}
