using System.Collections.Concurrent;

namespace DImage.Api.Imaging;

/// <summary>
/// 一次像素赋值请求:目标坐标与颜色。
/// </summary>
/// <param name="X">列号。</param>
/// <param name="Y">行号。</param>
/// <param name="Color">归一化颜色值。</param>
public readonly record struct PixelAssignment(int X, int Y, PixelColor Color);

/// <summary>
/// 注册表中某个图像对象的只读元信息快照(<b>不含</b>像素数据与底层缓冲区)。
/// </summary>
/// <param name="Id">对象标识。</param>
/// <param name="Width">宽度(像素)。</param>
/// <param name="Height">高度(像素)。</param>
/// <param name="Format">像素格式。</param>
/// <param name="Stride">行步长(字节)。本注册表创建的对象恒为紧排。</param>
/// <param name="ByteLength">有效像素字节数。</param>
public readonly record struct ImageDescriptor(
    string Id,
    int Width,
    int Height,
    PixelFormat Format,
    int Stride,
    long ByteLength);

/// <summary>
/// 注册表的运行计数快照,供诊断与验收使用。
/// </summary>
/// <param name="ImageCount">当前存活对象数。</param>
/// <param name="TotalBytes">当前存活对象占用的有效像素字节总数。</param>
/// <param name="MaxImageCount">对象数上限。</param>
/// <param name="MaxTotalBytes">总字节上限。</param>
/// <param name="CreatedCount">累计创建成功次数。</param>
/// <param name="ReleasedCount">累计显式释放次数。</param>
/// <param name="ReapedCount">累计被空闲回收次数。</param>
public readonly record struct ImageStoreStats(
    int ImageCount,
    long TotalBytes,
    int MaxImageCount,
    long MaxTotalBytes,
    long CreatedCount,
    long ReleasedCount,
    long ReapedCount);

/// <summary>
/// 线程安全的内存图像注册表:图像对象按唯一 Id 登记,所有像素读写与编码导出均经本类型完成。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么裸 <see cref="ImageBuffer"/> 绝不外泄</b>:<see cref="ImageBuffer.GetPixelSpan"/>
/// 返回的是<b>可变的 <see cref="Span{T}"/></b>,而 <see cref="ImageBuffer"/> 自身不提供任何
/// 线程安全保证。若工具层各自 <c>TryGet</c> 拿到 <see cref="ImageBuffer"/> 引用后并发写入,
/// 同一 Id 上的两次 <c>set_pixels</c> 会产生交错写入 —— 现象是「像素值偶尔不对」,
/// 既不会抛异常,也无法从调用方的输入复现。把读写全部收进注册表、在每对象独立锁内完成,
/// 是唯一能把这处竞态封死在单点的手段。
/// </para>
/// <para>
/// <b>锁的层级与顺序</b>:注册表级 <see cref="_gate"/> 保护对象集合与字节计数;
/// 每个条目持有独立的 <see cref="Entry.Sync"/>,保护该图像的像素数据。
/// 全局锁<b>恒先于</b>条目锁获取(<see cref="Release"/>、<see cref="Reap"/>),
/// 且 <see cref="Reap"/> 对条目锁只做 <see cref="Monitor.TryEnter(object, out bool)"/>
/// 而不阻塞等待 —— 两条规则共同保证不会出现「回收器等待写者、写者等待全局锁」的死锁。
/// </para>
/// <para>
/// <b>同时取两个条目锁的规则(任务 16 随 <see cref="Composite"/> 引入)</b>:除
/// <see cref="Composite"/> 外,本类型所有成员<b>只持有一个条目锁</b>,故条目锁之间本无顺序可言;
/// 而合成必须同时锁住源与目标,于是首次出现了「两个条目锁」这一情形。规则有两条,缺一不可:
/// <list type="number">
///   <item><description>按 <b>Id 的序数序</b>(<see cref="string.CompareOrdinal(string, string)"/>)
///   决定先后,使 A→B 与 B→A 两次调用取得一致的加锁顺序。若改用「先目标后源」这类按角色排序,
///   两个方向相反的并发合成就会各持一把、互等对方 —— 经典的 ABBA 死锁;</description></item>
///   <item><description><b>绝不在持有条目锁时再去取 <see cref="_gate"/></b>。本方法因此全程不碰全局锁:
///   否则「持条目锁等全局锁」与 <see cref="Reap"/> 的「持全局锁等条目锁」
///   恰好构成上述死锁的另一个实例,而这一个连加锁顺序都救不了。</description></item>
/// </list>
/// <b>后续若再添「一次动两张图」的能力,必须沿用这两条规则</b>,而不是各自发明一套顺序。
/// </para>
/// <para>
/// <b>为什么超限立即失败、而不是驱逐已有对象</b>:驱逐会让<b>先创建、仍在使用</b>的 Id 突然失效,
/// 调用方拿到的是一句「找不到」,却无从知道自己做错了什么 —— 真正的成因(别人挤占了容量)
/// 出现在完全无关的请求里。静默失效比显式失败昂贵得多,故超限一律抛
/// <see cref="ImageCapacityExceededException"/>,并带上上限与实际值。
/// </para>
/// <para>
/// <b>为什么 TTL 回收与显式释放必须语义可分</b>:被回收的 Id 再次访问返回「未找到」,
/// 与显式释放后的行为一致;但二者对<b>容量核算</b>的意义完全不同 ——
/// 显式释放是调用方的确定性动作,TTL 回收是后台的非确定动作。
/// 因此 TTL <b>不能</b>被当作容量保护手段:一条「持续创建、从不释放」的请求流在每次创建时
/// 都必须撞上同步的容量校验,而不是指望后台清理追上创建速度。
/// </para>
/// <para>
/// <b>单实例假设</b>:注册表是进程内全局的,Id 不跨进程共享。本服务当前为单实例部署;
/// 若日后水平扩容,同一 Id 在另一个实例上必然「找不到」—— 届时需要的是分布式存储,
/// 而不是在本类型上加缓存。
/// </para>
/// </remarks>
public sealed class ImageBufferStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ImageRegistryLimits _limits;
    private readonly TimeProvider _timeProvider;

    /// <summary>注册表级互斥体:保护 <see cref="_entries"/> 与 <see cref="_totalBytes"/> 的核算一致性。</summary>
    private readonly object _gate = new();

    private long _totalBytes;
    private long _createdCount;
    private long _releasedCount;
    private long _reapedCount;

    /// <summary>
    /// 构造注册表。上限与时钟均由调用方注入 ——
    /// 本类型不读 <c>IConfiguration</c> 也不注入 <c>IOptions&lt;T&gt;</c>,
    /// 配置的来源是宿主的事,<c>Imaging/</c> 只吃纯值参数。
    /// </summary>
    /// <param name="limits">上限配置。</param>
    /// <param name="timeProvider">时钟;为空时使用 <see cref="TimeProvider.System"/>。</param>
    public ImageBufferStore(ImageRegistryLimits limits, TimeProvider? timeProvider = null)
    {
        _limits = limits;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>当前运行计数快照。</summary>
    public ImageStoreStats Stats
    {
        get
        {
            lock (_gate)
            {
                return new ImageStoreStats(
                    _entries.Count,
                    _totalBytes,
                    _limits.MaxImageCount,
                    _limits.MaxTotalBytes,
                    _createdCount,
                    _releasedCount,
                    _reapedCount);
            }
        }
    }

    /// <summary>
    /// 创建一张全 0 的空白图像并登记,返回其唯一 Id。
    /// </summary>
    /// <param name="width">宽度(像素)。</param>
    /// <param name="height">高度(像素)。</param>
    /// <param name="format">像素格式。</param>
    /// <returns>新对象的唯一 Id(32 位十六进制,无连字符)。</returns>
    /// <exception cref="ArgumentOutOfRangeException">尺寸非法或超出 <see cref="ImageLimits"/>。</exception>
    /// <exception cref="ImageCapacityExceededException">对象数或总字节数超出上限。</exception>
    public string Create(int width, int height, PixelFormat format)
    {
        // 先做一次不分配内存的对象数预检:注册表已满时,若等到分配完 256 MiB 的缓冲区才拒绝,
        // 反复请求最大尺寸就成了一条零成本的 GC 压力放大器
        lock (_gate)
        {
            if (_entries.Count >= _limits.MaxImageCount)
            {
                throw new ImageCapacityExceededException(
                    CapacityLimitKind.ImageCount, _limits.MaxImageCount, _entries.Count);
            }
        }

        // 几何校验与分配由 ImageBuffer 唯一负责,避免此处的校验与构造期校验出现两套标准
        var image = new ImageBuffer(width, height, format);

        return Register(image);
    }

    /// <summary>
    /// <b>零分配</b>的容量预检:在真正分配像素内存之前,先用「声明尺寸」撞一遍注册表容量。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么必须真的零分配</b>:上传路径会在解码 PNG 之前调用本方法。它若顺手
    /// <c>new ImageBuffer(...)</c> 探一下大小,「先校验、后分配」就退化成了「先分配再拒绝」——
    /// 于是「声明 16384×16384、IDAT 只有几十字节」这类请求仍然能逼出一次近 GB 的分配,
    /// 整个炸开防护链条失效,而调用方看到的错误码却完全正常。
    /// </para>
    /// <para>
    /// 与 <see cref="Create"/> 的差异只在于「只预检、不登记」:<see cref="Add"/> 会在锁内复核,
    /// 故预检通过不代表登记必然成功(期间可能有并发登记),这是刻意的两段式 ——
    /// 前段保证异常路径不产生巨额分配,后段保证核算一致性。
    /// </para>
    /// </remarks>
    /// <param name="width">即将分配的宽度(像素)。</param>
    /// <param name="height">即将分配的高度(像素)。</param>
    /// <param name="format">即将使用的像素格式。</param>
    /// <exception cref="ArgumentOutOfRangeException">尺寸非法或超出 <see cref="ImageLimits"/>。</exception>
    /// <exception cref="ImageCapacityExceededException">对象数或总字节数超出上限。</exception>
    public void EnsureCapacity(int width, int height, PixelFormat format)
    {
        // 字节数的计算口径由 ImageBuffer 唯一负责 —— 在此重算一份等于让同一个公式有两个副本
        long byteLength = ImageBuffer.ComputeByteLength(width, height, format);

        lock (_gate)
        {
            ThrowIfExceeded(byteLength);
        }
    }

    /// <summary>
    /// 登记一张<b>已填好像素</b>的外部构造图像,返回其唯一 Id。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么另开方法而不复用 <see cref="Create"/></b>:<see cref="Create"/> 是已被既有
    /// 四项工具验收过的路径,「分配与登记同在一个方法内」正是它的语义。上传路径拿到的是
    /// 一张<b>已经构造并填好像素</b>的缓冲区,需要的是「只登记、不分配」这另一半能力。
    /// 改动 <see cref="Create"/> 去容纳这个形状,等于给一条已验证的路径加分支。
    /// </para>
    /// <para>
    /// 登记前的容量复核与 <see cref="Create"/> 走同一段代码(<see cref="Register"/>),
    /// 故容量判定、Id 生成方式、计数语义三者在两条路径上只有一处实现。
    /// </para>
    /// </remarks>
    /// <param name="image">待登记的图像,其像素数据已就绪。</param>
    /// <returns>新对象的唯一 Id(32 位十六进制,无连字符)。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> 为 <c>null</c>。</exception>
    /// <exception cref="ImageCapacityExceededException">对象数或总字节数超出上限。</exception>
    public string Add(ImageBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Register(image);
    }

    /// <summary>
    /// 登记一张已构造的图像:锁内复核容量、生成 Id、累加计数。两条创建路径的唯一汇合点。
    /// </summary>
    /// <remarks>
    /// <b>不复用 <see cref="Create"/> 的「先无分配预检」那一段</b>:那张缓冲区在调用本方法之前
    /// 就已经分配好了,预检无从谈起。此处只有一次锁内复核,作用是让并发登记之间不超限。
    /// </remarks>
    private string Register(ImageBuffer image)
    {
        long byteLength = image.ByteLength;

        lock (_gate)
        {
            ThrowIfExceeded(byteLength);

            string id = Guid.NewGuid().ToString("N");
            var entry = new Entry(id, image, byteLength, _timeProvider.GetUtcNow());

            // 单一 Id 空间内 Guid 冲突概率可忽略;此处仍用循环而非 AddOrUpdate,
            // 是为了让「已存在则换 Id」成为显式行为,而不是靠覆盖掩盖
            while (!_entries.TryAdd(id, entry))
            {
                id = Guid.NewGuid().ToString("N");
                entry = new Entry(id, image, byteLength, _timeProvider.GetUtcNow());
            }

            _totalBytes += byteLength;
            _createdCount++;
            return id;
        }
    }

    /// <summary>锁内复核容量,超限即抛。调用方须已持有 <see cref="_gate"/>。</summary>
    private void ThrowIfExceeded(long byteLength)
    {
        if (_entries.Count >= _limits.MaxImageCount)
        {
            throw new ImageCapacityExceededException(
                CapacityLimitKind.ImageCount, _limits.MaxImageCount, _entries.Count);
        }

        if (_totalBytes + byteLength > _limits.MaxTotalBytes)
        {
            throw new ImageCapacityExceededException(
                CapacityLimitKind.TotalBytes, _limits.MaxTotalBytes, _totalBytes + byteLength);
        }
    }

    /// <summary>
    /// 显式释放指定图像。
    /// </summary>
    /// <remarks>
    /// 释放后该 Id 上的任何后续操作一律返回「未找到」。重复释放返回 <c>false</c> 而非抛异常 ——
    /// 释放是幂等的收尾动作,而「对象已经不在了」正是调用方期望的结果。
    /// </remarks>
    /// <param name="id">目标 Id。</param>
    /// <param name="released">释放成功时为 1,否则为 0。</param>
    /// <returns>是否确实释放了一个对象。</returns>
    public bool TryRelease(string id, out int released)
    {
        released = 0;

        if (string.IsNullOrEmpty(id) || !_entries.TryGetValue(id, out var entry))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_entries.TryRemove(id, out entry))
            {
                return false;
            }

            // 在条目锁内打上墓碑:并发的 SetPixels/EncodePng 若已越过字典查找,
            // 会在锁内看到 Removed 并转为「未找到」,而不会对着一个已被摘除的对象写入
            lock (entry.Sync)
            {
                entry.Removed = true;
            }

            _totalBytes -= entry.ByteLength;
            _releasedCount++;
        }

        released = 1;
        return true;
    }

    /// <summary>查询指定图像的元信息,不触碰像素数据。</summary>
    /// <param name="id">目标 Id。</param>
    /// <param name="descriptor">查询成功时写入元信息。</param>
    /// <returns>对象是否存在。</returns>
    public bool TryDescribe(string id, out ImageDescriptor descriptor)
    {
        descriptor = default;

        if (string.IsNullOrEmpty(id) || !_entries.TryGetValue(id, out var entry))
        {
            return false;
        }

        lock (entry.Sync)
        {
            if (entry.Removed)
            {
                return false;
            }

            var image = entry.Image;
            descriptor = new ImageDescriptor(
                entry.Id,
                image.Width,
                image.Height,
                image.Format,
                image.Stride,
                entry.ByteLength);
            return true;
        }
    }

    /// <summary>
    /// 批量写入像素。<b>先全量校验、后一次性写入</b>,任一点非法则整批拒绝、零写入。
    /// </summary>
    /// <remarks>
    /// 半成功(前 3 点已写、第 4 点报错)会让调用方无从判断图像的真实状态 ——
    /// 它无法知道该重试整批、还是接着写下一点。要么全做,要么全不做。
    /// </remarks>
    /// <param name="id">目标 Id。</param>
    /// <param name="points">待写入的点集合。</param>
    /// <returns>实际写入的像素数。</returns>
    /// <exception cref="ImageNotFoundException">Id 不存在、已被释放或被回收。</exception>
    /// <exception cref="PixelOutOfRangeException">存在越界坐标;异常携带该点在集合中的序号与实际坐标。</exception>
    public int SetPixels(string id, IReadOnlyList<PixelAssignment> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        var entry = Resolve(id);
        lock (entry.Sync)
        {
            EnsureAlive(entry);

            var image = entry.Image;
            int width = image.Width;
            int height = image.Height;

            // 第一遍:全量校验。遍历结束时若任何一个点非法,下面的写入循环根本不会开始
            for (int i = 0; i < points.Count; i++)
            {
                var point = points[i];
                if (point.X < 0 || point.X >= width || point.Y < 0 || point.Y >= height)
                {
                    throw new PixelOutOfRangeException(i, point.X, point.Y, width, height);
                }
            }

            // 第二遍:写入。坐标已全部通过校验,此处不会再抛越界异常
            for (int i = 0; i < points.Count; i++)
            {
                var point = points[i];
                image.SetPixel(point.X, point.Y, point.Color);
            }

            entry.LastAccessUtc = _timeProvider.GetUtcNow();
            return points.Count;
        }
    }

    /// <summary>
    /// 把一个形状按给定样式绘制到指定图像上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>校验在条目锁内、在任何像素写入之前完成</b>,沿用 <see cref="SetPixels"/> 的原子性范式:
    /// 样式或几何参数非法时抛出异常,图像保持<b>零改动</b>。
    /// </para>
    /// <para>
    /// <b>越界是裁剪语义,不是错误</b>:形状几何落在画布外(含部分在外)时正常绘制,
    /// 只把画布内的部分落到像素上。这与 <see cref="SetPixels"/> 的「任一点越界即整批拒绝」
    /// <b>刻意不同</b>,理由见 <see cref="ImageDraw"/>。
    /// </para>
    /// <para>
    /// 绘制全程在条目锁内进行,故同一 Id 上的并发绘制与像素写入不会交错 ——
    /// 代价是大图的整个绘制期间该 Id 被独占,与 <see cref="EncodePng"/> 同理。
    /// </para>
    /// <para>
    /// <see cref="Shape"/> 与 <see cref="DrawStyle"/> 都是<b>纯值输入</b>:本方法因此不需要
    /// 把 <see cref="ImageBuffer"/> 交出去,「裸缓冲区不出注册表」的约束继续成立。
    /// </para>
    /// </remarks>
    /// <param name="id">目标 Id。</param>
    /// <param name="shape">待绘制的形状。</param>
    /// <param name="style">绘制样式。</param>
    /// <returns>被覆盖(覆盖率大于 0)的像素数;形状整体落在画布外时为 0。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> 为 <c>null</c>。</exception>
    /// <exception cref="ImageNotFoundException">Id 不存在、已被释放或被回收。</exception>
    /// <exception cref="InvalidStrokeWidthException">线宽非有限、非正或超上限。</exception>
    /// <exception cref="InvalidFillRuleException">填充规则不是已定义值。</exception>
    /// <exception cref="InvalidGeometryException">几何参数非法。</exception>
    /// <exception cref="PathSyntaxException"><c>d</c> 字符串存在语法错误。</exception>
    /// <exception cref="DrawingLimitExceededException">超出绘制上限。</exception>
    public int Draw(string id, Shape shape, DrawStyle style)
    {
        ArgumentNullException.ThrowIfNull(shape);

        var entry = Resolve(id);
        lock (entry.Sync)
        {
            EnsureAlive(entry);

            // 绘制失败时不会走到下面这行,访问时间不刷新 —— 与 SetPixels 一致:
            // 失败的请求不应把一张已无人使用的图像续命,否则「持续用错参数打同一 Id」
            // 就成了绕过空闲回收的手段
            int covered = ImageDraw.Draw(entry.Image, shape, style);

            entry.LastAccessUtc = _timeProvider.GetUtcNow();
            return covered;
        }
    }

    /// <summary>
    /// 把一个 Id 的图像按落点、缩放与不透明度合成到另一个 Id 的图像上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>本方法是唯一同时锁住两个条目锁的成员</b>,故加锁顺序是本方法最需要读懂的部分 ——
    /// 见类型注释中「同时取两个条目锁的规则」。要点:两个 Id 指向<b>不同条目</b>时按 Id 序数序加锁;
    /// 指向<b>同一条目</b>时只锁一次并先取快照。全程不碰 <see cref="_gate"/>。
    /// </para>
    /// <para>
    /// <b>两个 Id 相同时为什么必须先 <see cref="ImageBuffer.Clone"/> 再合成</b>:
    /// 合成是边读源、边写目标的就地操作,源与目标一旦是同一块像素,
    /// 后写入的像素就会成为后续读取的输入,结果取决于扫描顺序 ——
    /// 既不会报错,也无法从调用方的输入复现,只是输出被源图自身的像素质地拖尾涂抹。
    /// 快照让「同 Id 自合并」退化为一个定义明确的操作:<b>结果等同于拿合成前的自己当源</b>,
    /// 故同一个 <see cref="CompositeStyle"/> 在同一张图上重复执行是幂等的
    /// (普通合成则相反,半透明叠加会逐次逼近源图颜色)。
    /// 快照的成本是源图的一份深拷贝,故只在这一个分支上付。
    /// </para>
    /// <para>
    /// <b>失败时两个 Id 的访问时间都不刷新</b>,与 <see cref="Draw"/>、<see cref="SetPixels"/> 一致:
    /// 失败的请求不应把已无人使用的图像续命,否则持续用错参数打同一 Id 就成了绕过空闲回收的手段。
    /// 校验在算法层完成,而它发生在取到快照、进入条目锁之后 ——
    /// 也就是说参数非法时目标图像的像素<b>逐字节不变</b>,但源图在那次调用里确实被读过一遍。
    /// </para>
    /// </remarks>
    /// <param name="targetId">目标 Id,就地修改。</param>
    /// <param name="sourceId">源 Id;可与 <paramref name="targetId"/> 相同。</param>
    /// <param name="style">合成样式(落点、缩放、不透明度)。</param>
    /// <returns>被覆盖(覆盖率大于 0)的目标像素数;源图整体落在画布外时为 0。</returns>
    /// <exception cref="ImageNotFoundException">任一 Id 不存在、已被释放或被回收。</exception>
    /// <exception cref="InvalidGeometryException">落点非有限值或超出量级上限。</exception>
    /// <exception cref="InvalidScaleException">缩放倍数非有限值,或小于等于 0。</exception>
    /// <exception cref="InvalidOpacityException">不透明度非有限值,或落在 <c>[0, 1]</c> 之外。</exception>
    public int Composite(string targetId, string sourceId, CompositeStyle style)
    {
        var target = Resolve(targetId);
        var source = Resolve(sourceId);

        if (ReferenceEquals(target, source))
        {
            lock (target.Sync)
            {
                EnsureAlive(target);

                ImageBuffer snapshot = target.Image.Clone();
                int covered = ImageCompositor.Composite(target.Image, snapshot, style);

                target.LastAccessUtc = _timeProvider.GetUtcNow();
                return covered;
            }
        }

        // 按 Id 的序数序加锁,使 A→B 与 B→A 取得一致的顺序(见类型注释的 ABBA 段落)
        var first = string.CompareOrdinal(target.Id, source.Id) <= 0 ? target : source;
        var second = ReferenceEquals(first, target) ? source : target;

        lock (first.Sync)
        {
            lock (second.Sync)
            {
                // 两把锁拿到之前对象都可能已被释放或回收,故两个都要复核
                EnsureAlive(first);
                EnsureAlive(second);

                int covered = ImageCompositor.Composite(target.Image, source.Image, style);

                var now = _timeProvider.GetUtcNow();
                target.LastAccessUtc = now;

                // 源图确实被读取过,故两个 Id 的访问时间一并刷新 ——
                // 否则若只有目标续命,一张反复被当作贴图素材的源图会先于它的使用者被回收,
                // 而调用方的下一句「还贴同一张」就突然收到「找不到」
                source.LastAccessUtc = now;

                return covered;
            }
        }
    }

    /// <summary>
    /// 把指定图像编码为 PNG 字节流。
    /// </summary>
    /// <remarks>
    /// 编码在条目锁内进行。大图导出期间该 Id 上的写入会被阻塞,这是刻意的:
    /// 允许边写边编码会让导出结果取决于线程调度,而「同一份输入两次导出结果不同」
    /// 是极难归因的现象。
    /// </remarks>
    /// <param name="id">目标 Id。</param>
    /// <returns>完整的 PNG 文件字节流。</returns>
    /// <exception cref="ImageNotFoundException">Id 不存在、已被释放或被回收。</exception>
    public byte[] EncodePng(string id)
    {
        var entry = Resolve(id);
        lock (entry.Sync)
        {
            EnsureAlive(entry);
            byte[] png = PngEncoder.Encode(entry.Image);
            entry.LastAccessUtc = _timeProvider.GetUtcNow();
            return png;
        }
    }

    /// <summary>
    /// 回收空闲超时的图像对象。
    /// </summary>
    /// <remarks>
    /// 只回收「距上次访问已超过 <see cref="ImageRegistryLimits.EffectiveIdleTtl"/>」的对象,
    /// 且对正在被其它线程操作的对象<b>直接跳过、留待下一轮</b>(<see cref="Monitor.TryEnter(object, out bool)"/>
    /// 失败即放弃)。回收器绝不为腾地方而等待某个写者 —— 那是死锁的经典起手式,
    /// 而空闲回收本就容许延迟一个周期。
    /// </remarks>
    /// <param name="now">当前时刻。</param>
    /// <returns>本轮实际回收的对象数。</returns>
    public int Reap(DateTimeOffset now)
    {
        TimeSpan idleTtl = _limits.EffectiveIdleTtl;

        // 先摘取快照:遍历 ConcurrentDictionary 的同时修改它是安全的,但快照能让候选集合
        // 在整轮回收内保持稳定,避免同一对象因枚举顺序被反复计入
        var candidates = _entries.ToArray();
        int reaped = 0;

        foreach (var pair in candidates)
        {
            var entry = pair.Value;

            if (now - entry.LastAccessUtc <= idleTtl)
            {
                continue;
            }

            lock (_gate)
            {
                // 尝试进入条目锁:失败说明该对象正被写入或导出,本轮跳过
                if (!Monitor.TryEnter(entry.Sync))
                {
                    continue;
                }

                try
                {
                    // 拿到锁后复核空闲时长 —— 等待期间该对象可能刚被访问过
                    if (entry.Removed || now - entry.LastAccessUtc <= idleTtl)
                    {
                        continue;
                    }

                    if (_entries.TryRemove(pair.Key, out _))
                    {
                        entry.Removed = true;
                        _totalBytes -= entry.ByteLength;
                        _reapedCount++;
                        reaped++;
                    }
                }
                finally
                {
                    Monitor.Exit(entry.Sync);
                }
            }
        }

        return reaped;
    }

    /// <summary>按 Id 取出条目,不存在则抛「未找到」。</summary>
    /// <remarks>
    /// Id 解析失败与「不存在」<b>统一按未找到处理</b>,不区分二者:
    /// 区分会让攻击者能据此枚举出 Id 的格式甚至有效性,而调用方对两种情况的可行动作完全一致 ——
    /// 都是「重新创建一张」。
    /// </remarks>
    private Entry Resolve(string id)
    {
        if (string.IsNullOrEmpty(id) || !_entries.TryGetValue(id, out var entry))
        {
            throw new ImageNotFoundException(id);
        }

        return entry;
    }

    /// <summary>在条目锁内复核对象是否仍然存活。</summary>
    /// <remarks>
    /// 查找与加锁之间存在窗口期:期间对象可能已被释放或回收。缺了这次复核,
    /// 调用方会对着一个已经摘除的对象写入并收到成功响应,而后续读取却「找不到」。
    /// </remarks>
    private static void EnsureAlive(Entry entry)
    {
        if (entry.Removed)
        {
            throw new ImageNotFoundException(entry.Id);
        }
    }

    /// <summary>注册表中的一个图像对象及其独立同步原语。</summary>
    private sealed class Entry(string id, ImageBuffer image, long byteLength, DateTimeOffset createdAt)
    {
        /// <summary>本对象的互斥体,保护 <see cref="Image"/> 的像素数据。</summary>
        public object Sync { get; } = new();

        public string Id { get; } = id;

        public ImageBuffer Image { get; } = image;

        /// <summary>有效像素字节数,供注册表级字节核算使用(避免每次重新计算)。</summary>
        public long ByteLength { get; } = byteLength;

        /// <summary>创建时刻,仅用于诊断。</summary>
        public DateTimeOffset CreatedAtUtc { get; } = createdAt;

        /// <summary>最近一次访问时刻,TTL 回收的判定依据。</summary>
        public DateTimeOffset LastAccessUtc { get; set; } = createdAt;

        /// <summary>是否已被释放或回收。写入须在 <see cref="Sync"/> 内进行。</summary>
        public bool Removed { get; set; }
    }
}

/// <summary>容量上限的种类。</summary>
public enum CapacityLimitKind
{
    /// <summary>注册表内的对象数。</summary>
    ImageCount,

    /// <summary>注册表内的总字节数。</summary>
    TotalBytes
}

/// <summary>注册表容量超限。</summary>
/// <remarks>
/// 携带上限与实际值,使调用方能直接看出「差多少」,而不是从一句「容量不足」去猜。
/// </remarks>
public sealed class ImageCapacityExceededException(CapacityLimitKind kind, long limit, long actual)
    : Exception(BuildMessage(kind, limit, actual))
{
    /// <summary>超限的种类。</summary>
    public CapacityLimitKind Kind { get; } = kind;

    /// <summary>上限值。</summary>
    public long Limit { get; } = limit;

    /// <summary>本次请求触达的实际值。</summary>
    public long Actual { get; } = actual;

    private static string BuildMessage(CapacityLimitKind kind, long limit, long actual) => kind switch
    {
        CapacityLimitKind.ImageCount =>
            $"内存图像对象数已达上限 {limit},无法再创建(当前 {actual})。请先释放不再使用的图像。",
        CapacityLimitKind.TotalBytes =>
            $"内存图像总字节数将达 {actual},超过上限 {limit}。请先释放不再使用的图像。",
        _ => $"内存图像容量超限(上限 {limit},实际 {actual})。"
    };
}

/// <summary>指定的图像 Id 不存在、已被显式释放,或已被空闲回收。</summary>
/// <remarks>三种成因刻意合并为一种对外表现 —— 调用方的可行动作完全相同。</remarks>
public sealed class ImageNotFoundException(string? id)
    : Exception($"未找到内存图像对象(Id: {id ?? "<null>"})。该对象可能从未创建、已被释放,或已因空闲超时被回收。")
{
    /// <summary>请求的 Id。</summary>
    public string? Id { get; } = id;
}

/// <summary>像素坐标越界。</summary>
/// <remarks>
/// 携带<b>该点在集合中的序号</b>:批量写入时,只说「x 越界」无法定位到是哪一次赋值出的错。
/// </remarks>
public sealed class PixelOutOfRangeException(int index, int x, int y, int width, int height)
    : Exception(
        $"第 {index} 个点的坐标 ({x}, {y}) 越界:"
        + $"x 须落在 [0, {width}),y 须落在 [0, {height})。本批次已整体拒绝,未写入任何像素。")
{
    /// <summary>该点在请求集合中的序号(从 0 开始)。</summary>
    public int Index { get; } = index;

    /// <summary>请求的列号。</summary>
    public int X { get; } = x;

    /// <summary>请求的行号。</summary>
    public int Y { get; } = y;

    /// <summary>图像宽度。</summary>
    public int Width { get; } = width;

    /// <summary>图像高度。</summary>
    public int Height { get; } = height;
}
