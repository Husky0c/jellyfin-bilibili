using System.Text.Json;
using System.Xml.Linq;
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
using (var videoJson = JsonDocument.Parse("""{"title":"大 CID 测试","pic":"","owner":{"mid":123456,"name":"测试 UP 主","face":"https://example.org/avatar?x=1&y=2"},"pages":[{"cid":3000000123,"page":1,"part":"正片"}]}"""))
{
    var video = BiliApi.ParseVideoInfo("BV1xx411c7mD", videoJson.RootElement);
    if (video.Pages.Count != 1 || video.Pages[0].Cid != largeCid || video.Uploader?.Mid != 123456)
        throw new Exception("超过 Int32 范围的 CID 或 UP 主解析失败。");

    var nfoFolder = Path.Combine(folder, "nfo");
    Directory.CreateDirectory(nfoFolder);
    var videoPath = Path.Combine(nfoFolder, "video.mp4");
    var oldNfo = Path.Combine(nfoFolder, "movie.nfo");
    new XDocument(new XElement("movie",
        new XElement("title", video.Title),
        new XElement("plot", $"Bilibili {video.Bvid} / CID {largeCid}"),
        new XElement("uniqueid", new XAttribute("type", "bilibili"), new XAttribute("default", "true"), $"{video.Bvid}:{largeCid}"))).Save(oldNfo);
    ArchiveService.WriteNfo(video, video.Pages[0], videoPath);
    var nfoPath = Path.ChangeExtension(videoPath, ".nfo");
    var nfo = XDocument.Load(nfoPath);
    var actor = nfo.Root?.Element("actor");
    if (File.Exists(oldNfo) || (string?)nfo.Root?.Element("title") != video.Title ||
        (string?)actor?.Element("name") != "测试 UP 主" ||
        (string?)actor?.Element("role") != "UP主" ||
        (string?)actor?.Element("thumb") != "https://example.org/avatar?x=1&y=2" ||
        !((string?)nfo.Root?.Element("plot") ?? string.Empty).Contains("https://space.bilibili.com/123456", StringComparison.Ordinal))
        throw new Exception("同名 NFO、UP 主演员信息或旧版 NFO 迁移失败。");
    nfo.Root!.Add(new XElement("genre", "用户自定义"));
    nfo.Save(nfoPath);
    ArchiveService.WriteNfo(video, video.Pages[0], videoPath);
    nfo = XDocument.Load(nfoPath);
    if (nfo.Root?.Elements("actor").Count() != 1 || (string?)nfo.Root?.Element("genre") != "用户自定义")
        throw new Exception("再次同步不应重复演员或覆盖用户 NFO 字段。");
}
const long recentCid = 27_293_516_471L;
using (var videoJson = JsonDocument.Parse("""{"title":"近期视频","pic":"","pages":[{"cid":27293516471,"page":1,"part":"正片"}]}"""))
{
    if (BiliApi.ParseVideoInfo("BV1qSqFYFErX", videoJson.RootElement).Pages[0].Cid != recentCid)
        throw new Exception("近期视频的超大 CID 解析失败。");
}
using (var playJson = JsonDocument.Parse("""{"dash":{"video":[{"id":64,"baseUrl":"https://example.org/video.m4s","bandwidth":1000}],"audio":null}}"""))
{
    var streams = BiliApi.ParsePlayStreams(playJson.RootElement, 80);
    if (streams.Video.Quality != 64 || streams.Audio is not null)
        throw new Exception("无音轨 DASH 视频不应因 audio=null 失败。");
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

var retryState = new SyncState();
retryState.Folders[42] = new FolderSyncState { KnownBvids = ["BV1xx411c7mD", "BV1xx411c7mE"] };
retryState.Pending["BV1xx411c7mD"] = new PendingVideo { Bvid = "BV1xx411c7mD", NextAttemptAt = baseTime.AddHours(1) };
retryState.Pending["BV1xx411c7mE"] = new PendingVideo { Bvid = "BV1xx411c7mE", NextAttemptAt = baseTime.AddHours(2) };
long[] selectedFolders = [42];
if (ArchiveService.SelectPending(retryState, selectedFolders, baseTime, false).Length != 0 ||
    ArchiveService.SelectPending(retryState, selectedFolders, baseTime, true)[0].Bvid != "BV1xx411c7mD")
    throw new Exception("立即同步应允许重试等待中的视频。");
retryState.Pending["BV1xx411c7mD"].NextAttemptAt = baseTime.AddHours(3);
if (ArchiveService.SelectPending(retryState, selectedFolders, baseTime, true)[0].Bvid != "BV1xx411c7mE")
    throw new Exception("重试后应优先处理其他等待项。");
if (!ArchiveService.IsUnavailableVideoError(new BiliApiException(62012)) ||
    !ArchiveService.IsUnavailableVideoError(new BiliApiException(62002)) ||
    !ArchiveService.IsUnavailableVideoError(new BiliApiException(-404)) ||
    ArchiveService.IsUnavailableVideoError(new BiliApiException(62004)) ||
    !new BiliApiException(62012).Message.Contains("仅 UP 主可见", StringComparison.Ordinal))
    throw new Exception("不可访问视频的错误码分类或提示不正确。");

Console.WriteLine("PASS: 大 CID、UP 主同名 NFO 与旧版迁移、无音轨 DASH、手动重试；不可访问错误码；BV/CID、增量游标与重试队列持久化；差异检测、退避、配置页、二维码。");
