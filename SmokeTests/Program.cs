using System.Text.Json;
using Jellyfin.Plugin.BiliArchive;
using QRCoder;

var folder = Path.Combine(Path.GetTempPath(), "bili-archive-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);

var first = new ArchiveStore(folder);
await first.SaveCookieAsync("SESSDATA=test", CancellationToken.None);
await first.SaveAsync(new ArchiveRecord
{
    Bvid = "BV1xx411c7mD", Cid = 123, Title = "测试视频", Status = "completed",
    FilePath = "/media/bilibili/BV1xx411c7mD/P01-123/video.mp4"
}, CancellationToken.None);

var second = new ArchiveStore(folder);
var record = await second.GetAsync("BV1xx411c7mD", 123, CancellationToken.None);
if (record?.Status != "completed" || record.FilePath is null)
    throw new Exception("BV/CID 完成状态未持久化。");
const long largeCid = 3_000_000_123L;
using (var videoJson = JsonDocument.Parse("""{"title":"大 CID 测试","pic":"","pages":[{"cid":3000000123,"page":1,"part":"正片"}]}"""))
{
    var video = BiliApi.ParseVideoInfo("BV1xx411c7mD", videoJson.RootElement);
    if (video.Pages.Count != 1 || video.Pages[0].Cid != largeCid)
        throw new Exception("超过 Int32 范围的 CID 解析失败。");
}
await second.SaveAsync(new ArchiveRecord
{
    Bvid = "BV1xx411c7mE", Cid = largeCid, Title = "大 CID 测试", Status = "completed",
    FilePath = "/media/bilibili/BV1xx411c7mE/P01-3000000123/video.mp4"
}, CancellationToken.None);
if ((await new ArchiveStore(folder).GetAsync("BV1xx411c7mE", largeCid, CancellationToken.None))?.Cid != largeCid)
    throw new Exception("超过 Int32 范围的 CID 状态未持久化。");
await second.ClearCookieAsync(CancellationToken.None);
var third = new ArchiveStore(folder);
if ((await third.GetAsync("BV1xx411c7mD", 123, CancellationToken.None))?.Status != "completed")
    throw new Exception("退出登录不应清理已归档状态。");
if (third.ReadCookie() is not null)
    throw new Exception("退出登录应清理 Cookie。");

if (!typeof(BiliApi).Assembly.GetManifestResourceNames().Contains("Jellyfin.Plugin.BiliArchive.Configuration.configPage.html"))
    throw new Exception("Jellyfin 配置页未嵌入插件 DLL。");
using var qrGenerator = new QRCodeGenerator();
using var qrData = qrGenerator.CreateQrCode("https://passport.bilibili.com/", QRCodeGenerator.ECCLevel.M);
if (!new SvgQRCode(qrData).GetGraphic(5).Contains("<svg", StringComparison.Ordinal))
    throw new Exception("二维码 SVG 生成失败。");

var syncStore = new SyncStateStore(folder);
var syncState = new SyncState();
syncState.Folders[42] = new FolderSyncState
{
    KnownBvids = ["BV1xx411c7mD"], HasSnapshot = true,
    NextCheckAt = DateTimeOffset.UtcNow.AddHours(2), ReconcilePage = 3
};
syncState.Pending["BV1xx411c7mD"] = new PendingVideo
{
    Bvid = "BV1xx411c7mD", Failures = 2, NextAttemptAt = DateTimeOffset.UtcNow.AddHours(2)
};
await syncStore.SaveAsync(syncState, CancellationToken.None);
var restored = await new SyncStateStore(folder).LoadAsync(CancellationToken.None);
if (!restored.Folders[42].HasSnapshot || restored.Folders[42].ReconcilePage != 3 || restored.Pending["BV1xx411c7mD"].Failures != 2)
    throw new Exception("增量扫描游标或失败队列未持久化。");
if (SyncPlanner.NextCheckDelay(0, 30, 240, 1).TotalMinutes != 30 ||
    SyncPlanner.NextCheckDelay(5, 30, 240, 1).TotalMinutes != 240 ||
    SyncPlanner.RetryDelay(4, 1).TotalHours != 8 ||
    SyncPlanner.RateLimitDelay(3, 1).TotalHours != 24)
    throw new Exception("自适应或退避间隔计算错误。");
await syncStore.MarkLoginRestoredAsync(CancellationToken.None);
restored = await syncStore.LoadAsync(CancellationToken.None);
if (restored.Folders[42].NextCheckAt != DateTimeOffset.MinValue || restored.LoginRequired)
    throw new Exception("重新登录后应立即允许扫描。");

var plannerState = new SyncState();
var plannerFolder = new FolderSyncState();
var baseTime = DateTimeOffset.UtcNow;
var archived = new HashSet<string>(StringComparer.Ordinal) { "BV1xx411c7mD" };
var b = "BV1xx411c7mE";
if (SyncPlanner.ApplyMembership(plannerFolder, plannerState, ["BV1xx411c7mD", b], archived,
        new HashSet<string>(), baseTime, 30, 240, 1) != 2 ||
    plannerState.Pending.Count != 1 || plannerState.Pending[b].IsNewFavorite)
    throw new Exception("初次扫描应跳过已归档 BV，并将未归档 BV 入队。");
if (SyncPlanner.ApplyMembership(plannerFolder, plannerState, [b, "BV1xx411c7mD"], archived,
        new HashSet<string>(), baseTime, 30, 240, 1) != 0 || plannerState.Pending.Count != 1)
    throw new Exception("仅排序变化不应重复入队。");
SyncPlanner.ApplyMembership(plannerFolder, plannerState, ["BV1xx411c7mD"], archived,
    new HashSet<string>(), baseTime, 30, 240, 1);
if (plannerState.Pending.Count != 1) throw new Exception("取消收藏不应删除归档队列或文件状态。");
SyncPlanner.ApplyMembership(plannerFolder, plannerState, ["BV1xx411c7mD", b], archived,
    new HashSet<string>(), baseTime.AddMinutes(5), 30, 240, 1);
if (!plannerState.Pending[b].IsNewFavorite) throw new Exception("重新收藏应提高待下载项优先级。");

Console.WriteLine("PASS: 大 CID 解析与持久化；BV/CID、增量游标与重试队列持久化；差异检测、退避、配置页、二维码。");
