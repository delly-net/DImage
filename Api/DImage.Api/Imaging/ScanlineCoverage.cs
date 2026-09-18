namespace DImage.Api.Imaging;

/// <summary>
/// 抗锯齿用的覆盖率累积缓冲,按固定行高分块(band)使用。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类型存在的唯一理由是抗锯齿的正确性</b>:同一个像素可能被多段几何覆盖
/// (自相交的填充、折线回折的描边、填充与描边重叠)。若「算出覆盖率就立即混合落笔」,
/// 该像素会被混合多次,得到 <c>1-(1-c)^2 &gt; c</c> 的加深结果 —— 表现为自相交处出现暗线。
/// 故必须先把整幅图形的覆盖率<b>累积</b>起来,再统一落笔。
/// </para>
/// <para>
/// <b>累积不是简单相加</b>:同一个像素被两段各 50% 覆盖的几何压过,相加得 200%,
/// 落笔时若不截断就会写出越界的通道值。故 <see cref="Add"/> 在每次累加后<b>饱和到 [0, 1]</b>。
/// 这是「覆盖率的物理含义是面积比例、不可能超过 1」这一事实的直接落地。
/// </para>
/// <para>
/// <b>分块是安全要求,不是优化</b>:整图分配的 <c>float[]</c> 在 <c>8192×8192</c> 画布上是
/// 268 MiB,超过单图上限,而 <c>/mcp</c> 暴露公网 —— 那是一条可被单个请求触发的 DoS 面。
/// 按 <see cref="DrawingLimits.CoverageBandHeight"/> 分块后,峰值恒为 <c>16 MiB</c>,
/// 与图形尺寸、画布尺寸均无关。分块不影响正确性:一个像素只属于一个 band,
/// 而每个 band 内的累积都在落笔之前完成,故「每像素只混合一次」的语义完整保留。
/// </para>
/// <para>
/// <b>缓冲在 band 之间复用</b>:分块若每次重新分配,「峰值有界」就只是把峰值挪到了分配器上 ——
/// 64 个 band 会制造 64 次 16 MiB 的大对象分配,GC 压力反而更大。
/// </para>
/// </remarks>
internal sealed class ScanlineCoverage
{
    private readonly float[] _buffer;

    private int _activeRows;

    /// <summary>按画布宽度与分块行高分配缓冲。</summary>
    /// <param name="width">画布宽度(像素)。</param>
    /// <param name="bandHeight">分块行高,不超过画布高度。</param>
    /// <remarks>
    /// 分块行高不另存为字段:唯一的消费点是这里的数组长度,再存一份只会多出一个
    /// 「字段与数组长度可能失配」的状态,而 <see cref="BeginBand"/> 的行数越界会由
    /// 索引越界立即暴露,不是静默错误。
    /// </remarks>
    internal ScanlineCoverage(int width, int bandHeight)
    {
        Width = width;
        _buffer = new float[width * bandHeight];
    }

    /// <summary>画布宽度(像素)。</summary>
    internal int Width { get; }

    /// <summary>开始一个新 band,把缓冲的有效区域清零。</summary>
    /// <remarks>
    /// 只清 <c>宽度 × 行数</c> 这一段,不清整块缓冲:最后一个 band 往往远短于
    /// <see cref="DrawingLimits.CoverageBandHeight"/>,整块清理会白白做几十倍的无用功。
    /// </remarks>
    /// <param name="rows">本 band 的有效行数,须不超过分配时的分块行高。</param>
    internal void BeginBand(int rows)
    {
        _activeRows = rows;
        Array.Clear(_buffer, 0, Width * rows);
    }

    /// <summary>
    /// 累加一个像素的覆盖率,并<b>饱和到 [0, 1]</b>。
    /// </summary>
    /// <remarks>
    /// 落在画布外的坐标被<b>静默丢弃</b>。这不是吞掉错误:绘制工具的越界语义就是「裁剪到画布内」,
    /// 形状与扫描线天然会越出画布,由本方法统一收口比让每个调用方各自判断更不容易漏。
    /// 参数本身非法(坐标非有限、尺寸非正)在更早的校验阶段就已报错,不会走到这里。
    /// </remarks>
    /// <param name="x">列号。</param>
    /// <param name="row">band 内的行号。</param>
    /// <param name="coverage">本次累加的覆盖率,取值 <c>[0, 1]</c>。</param>
    internal void Add(int x, int row, float coverage)
    {
        if (coverage <= 0f || (uint)x >= (uint)Width || (uint)row >= (uint)_activeRows)
        {
            return;
        }

        int index = row * Width + x;
        float sum = _buffer[index] + coverage;

        // 饱和而非简单相加:覆盖率的物理含义是面积比例,两段各 50% 的重叠不构成 200%
        _buffer[index] = sum > 1f ? 1f : sum;
    }

    /// <summary>读取一个像素当前累积的覆盖率,取值恒在 <c>[0, 1]</c>。</summary>
    /// <param name="x">列号。</param>
    /// <param name="row">band 内的行号。</param>
    internal float Get(int x, int row) => _buffer[row * Width + x];

    /// <summary>本 band 的有效行数。</summary>
    internal int ActiveRows => _activeRows;
}
