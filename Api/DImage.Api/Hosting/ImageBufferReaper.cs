using DImage.Api.Imaging;

namespace DImage.Api.Hosting;

/// <summary>
/// 内存图像空闲回收后台服务:周期性调用 <see cref="ImageBufferStore.Reap"/>,
/// 回收长期未被访问的图像对象。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么落在 <c>Hosting/</c> 而非 <c>Imaging/</c></b>:<see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
/// 是<b>宿主抽象</b>,而 <c>Imaging/</c> 是依赖图的叶子 —— 它必须能脱离 ASP.NET Core 宿主
/// 被单独引用与测试。把宿主类型放进叶子目录,等于让「图像算法库」反向依赖宿主,
/// 这条边界一旦破开就很难再收回。故新增 <c>Hosting/</c> 目录与 <c>Auth/</c>、<c>Endpoints/</c>、
/// <c>Imaging/</c>、<c>Mcp/</c>、<c>Options/</c> 平级,专门承载这类宿主侧适配。
/// </para>
/// <para>
/// <b>时钟来自 <see cref="TimeProvider"/></b>:注入而非直接读 <see cref="DateTimeOffset.UtcNow"/>,
/// 使空闲回收在验证场景下可以用短 TTL + 真实等待复现,无需把服务实现改造成可测形态。
/// </para>
/// </remarks>
internal sealed class ImageBufferReaper(
    ImageBufferStore store,
    TimeProvider timeProvider,
    ILogger<ImageBufferReaper> logger) : BackgroundService
{
    /// <summary>扫描周期。</summary>
    /// <remarks>
    /// 取 30 秒:相对 30 分钟的空闲阈值,最坏情况下对象的实际存活时长只多出 1/60,
    /// 精度足够;而 30 秒的间隔对单次 <c>ToArray</c> 快照 + 若干次时间比较而言开销可忽略。
    /// 周期比阈值低一个数量级即可,无需更密。
    /// </remarks>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ScanInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                int reaped = store.Reap(timeProvider.GetUtcNow());

                // 只在确有回收时记录:零回收是常态,逐周期打点会把日志淹成噪声,
                // 反而让「回收异常频繁」这个真正值得注意的信号难以察觉
                if (reaped > 0)
                {
                    var stats = store.Stats;
                    logger.LogInformation(
                        "已回收 {ReapedCount} 个空闲图像对象,当前存活 {ImageCount} 个、占用 {TotalBytes} 字节。",
                        reaped,
                        stats.ImageCount,
                        stats.TotalBytes);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主正常停机时的取消是预期路径,不是错误
        }
    }
}
