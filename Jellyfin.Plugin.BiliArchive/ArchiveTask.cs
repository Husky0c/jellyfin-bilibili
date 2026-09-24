using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BiliArchive;

public sealed class ArchiveTask : IScheduledTask
{
    private readonly ArchiveService _archive;
    private readonly ILogger<ArchiveTask> _logger;

    public ArchiveTask(ArchiveService archive, ILogger<ArchiveTask> logger)
    {
        _archive = archive;
        _logger = logger;
    }

    public string Name => "同步 Bilibili 收藏归档";
    public string Key => "BiliArchiveSyncV2";
    public string Description => "每 15 分钟检查是否到期；收藏夹检查间隔自适应，已归档文件永久保留。";
    public string Category => "Bilibili 收藏归档";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = "IntervalTrigger", IntervalTicks = TimeSpan.FromMinutes(15).Ticks }
    ];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try { await _archive.RunAsync(progress, cancellationToken).ConfigureAwait(false); }
        catch (BiliLoginException ex) { _logger.LogWarning(ex, "B 站登录已失效，需要重新扫码"); throw; }
        catch (BiliRateLimitException ex) { _logger.LogWarning(ex, "B 站风控，本轮停止"); throw; }
    }
}
