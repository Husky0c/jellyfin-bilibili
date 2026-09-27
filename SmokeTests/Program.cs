using System.Text.Json;
using System.Xml.Linq;
using Jellyfin.Plugin.BiliArchive;
using Microsoft.Extensions.Logging.Abstractions;
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
using (var videoJson = JsonDocument.Parse("""{"title":"大 CID 测试","desc":"视频简介","pic":"https://example.org/cover.jpg","owner":{"mid":123456,"name":"测试 UP 主","face":"https://example.org/avatar?x=1&y=2"},"pages":[{"cid":3000000123,"page":1,"part":"正片","first_frame":"https://example.org/p1.jpg"}]}"""))
{
    var video = BiliApi.ParseVideoInfo("BV1xx411c7mD", videoJson.RootElement);
    if (video.Pages.Count != 1 || video.Pages[0].Cid != largeCid || video.Uploader?.Mid != 123456 ||
        video.Description != "视频简介" || video.Pages[0].FirstFrame != "https://example.org/p1.jpg")
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
        (string?)nfo.Root?.Element("thumb") != "https://example.org/p1.jpg" ||
        (string?)actor?.Element("name") != "测试 UP 主" ||
        (string?)actor?.Element("role") != "UP主" ||
        (string?)actor?.Element("thumb") != "https://example.org/avatar?x=1&y=2" ||
        !((string?)nfo.Root?.Element("plot") ?? string.Empty).Contains("https://space.bilibili.com/123456", StringComparison.Ordinal))
        throw new Exception("同名 NFO、UP 主演员信息或旧版 NFO 迁移失败。");
    nfo.Root!.Add(new XElement("genre", "用户自定义"));
    nfo.Root.SetElementValue("plot", "手动编辑的简介");
    nfo.Save(nfoPath);
    ArchiveService.WriteNfo(video, video.Pages[0], videoPath);
    nfo = XDocument.Load(nfoPath);
    if (nfo.Root?.Elements("actor").Count() != 1 || (string?)nfo.Root?.Element("genre") != "用户自定义" ||
        (string?)nfo.Root?.Element("plot") != "手动编辑的简介")
        throw new Exception("再次同步不应重复演员或覆盖用户 NFO 字段。");
}
var layoutRoot = Path.Combine(folder, "movie-library");
var layoutStore = new ArchiveStore(Path.Combine(folder, "movie-state"));
var layoutService = new ArchiveService(new BiliApi(layoutStore), layoutStore,
    new SyncStateStore(Path.Combine(folder, "movie-sync")), NullLogger<ArchiveService>.Instance);
var bvid = "BV1xx411c7mF";
var oldPage1 = Path.Combine(layoutRoot, bvid, "P01-101", "video.mp4");
Directory.CreateDirectory(Path.GetDirectoryName(oldPage1)!);
File.WriteAllBytes(oldPage1, [1, 2, 3]);
File.WriteAllText(ArchiveService.DanmakuPath(oldPage1), "旧弹幕");
new XDocument(new XElement("movie", new XElement("title", "归档标题 - P01 开篇"),
    new XElement("genre", "自定义分类"),
    new XElement("uniqueid", new XAttribute("type", "bilibili"), bvid + ":101")))
    .Save(Path.ChangeExtension(oldPage1, ".nfo"));
await layoutStore.SaveAsync(new ArchiveRecord { Bvid = bvid, Cid = 101, Title = "归档标题",
    Status = "completed", FilePath = oldPage1 }, CancellationToken.None);
await layoutService.MigrateExistingArchivesAsync(layoutRoot, CancellationToken.None);
var single = ArchiveService.MoviePath(layoutRoot, bvid, "归档标题", 1, 101, false);
if (!File.Exists(single) || File.Exists(oldPage1) ||
    Path.GetFileName(Path.GetDirectoryName(single)) != $"归档标题 [{bvid}]" ||
    Path.GetFileNameWithoutExtension(single) != Path.GetFileName(Path.GetDirectoryName(single)) ||
    File.Exists(ArchiveService.DanmakuPath(oldPage1)) ||
    File.ReadAllText(ArchiveService.DanmakuPath(single)) != "旧弹幕" ||
    (await layoutStore.GetAsync(bvid, 101, CancellationToken.None))?.FilePath != single ||
    (string?)XDocument.Load(Path.ChangeExtension(single, ".nfo")).Root?.Element("title") != "归档标题" ||
    (string?)XDocument.Load(Path.ChangeExtension(single, ".nfo")).Root?.Element("genre") != "自定义分类")
    throw new Exception("单 P 旧归档未整理成电影库条目，或手动 NFO 字段丢失。");
var oldPage2 = Path.Combine(layoutRoot, bvid, "P02-102", "video.mp4");
Directory.CreateDirectory(Path.GetDirectoryName(oldPage2)!);
File.WriteAllBytes(oldPage2, [4, 5, 6]);
await layoutStore.SaveAsync(new ArchiveRecord { Bvid = bvid, Cid = 102, Title = "归档标题",
    Status = "completed", FilePath = oldPage2 }, CancellationToken.None);
var multiVideo = new VideoInfo(bvid, "归档标题", "https://example.org/cover.jpg",
    [new VideoPage(101, 1, "开篇", "https://example.org/p1.jpg"),
     new VideoPage(102, 2, "后续", "https://example.org/p2.jpg")], null, "视频的整体简介");
await layoutService.MigrateVideoArchivesAsync(multiVideo, layoutRoot, CancellationToken.None);
var part1 = ArchiveService.MoviePath(layoutRoot, bvid, "归档标题", 1, 101, true, "开篇");
var part2 = ArchiveService.MoviePath(layoutRoot, bvid, "归档标题", 2, 102, true, "后续");
ArchiveService.WriteNfo(multiVideo, multiVideo.Pages[0], part1);
ArchiveService.WriteNfo(multiVideo, multiVideo.Pages[1], part2);
var collectionFolder = Path.Combine(layoutRoot, $"归档标题 [{bvid}] [boxset]");
var collection = XDocument.Load(Path.Combine(collectionFolder, "collection.xml"));
if (File.Exists(single) || !File.Exists(part1) || !File.Exists(part2) || File.Exists(oldPage2) ||
    Directory.Exists(Path.Combine(layoutRoot, bvid)) ||
    Path.GetFileName(Path.GetDirectoryName(part1)) != "P01 - 开篇 (CID 101)" ||
    File.Exists(ArchiveService.DanmakuPath(single)) ||
    File.ReadAllText(ArchiveService.DanmakuPath(part1)) != "旧弹幕" ||
    (await layoutStore.GetAsync(bvid, 101, CancellationToken.None))?.FilePath != part1 ||
    (await layoutStore.GetAsync(bvid, 102, CancellationToken.None))?.FilePath != part2 ||
    (string?)collection.Root?.Element("LocalTitle") != "归档标题" ||
    (string?)collection.Root?.Element("Overview") != "视频的整体简介" ||
    (string?)XDocument.Load(Path.ChangeExtension(part1, ".nfo")).Root?.Element("uniqueid") != bvid + ":101" ||
    (string?)XDocument.Load(Path.ChangeExtension(part1, ".nfo")).Root?.Element("title") != "P01 开篇" ||
    (string?)XDocument.Load(Path.ChangeExtension(part2, ".nfo")).Root?.Element("title") != "P02 后续" ||
    !((string?)XDocument.Load(Path.ChangeExtension(part2, ".nfo")).Root?.Element("plot") ?? string.Empty)
        .Contains("视频的整体简介", StringComparison.Ordinal))
    throw new Exception("新增分 P 后未生成电影合集，或各分 P 的独立元数据丢失。");
var previousBvid = "BV1xx411c7mG";
for (var number = 1; number <= 2; number++)
{
    var previousPath = Path.Combine(layoutRoot, previousBvid, $"{previousBvid}-cd{number:D2}.mp4");
    Directory.CreateDirectory(Path.GetDirectoryName(previousPath)!);
    File.WriteAllBytes(previousPath, [(byte)number]);
    new XDocument(new XElement("movie", new XElement("title", "旧版多 P"),
        new XElement("genre", "保留的分类"),
        new XElement("uniqueid", new XAttribute("type", "bilibili"), previousBvid)))
        .Save(Path.ChangeExtension(previousPath, ".nfo"));
    await layoutStore.SaveAsync(new ArchiveRecord { Bvid = previousBvid, Cid = 200 + number,
        Title = "旧版多 P", Status = "completed", FilePath = previousPath }, CancellationToken.None);
}
await layoutService.MigrateExistingArchivesAsync(layoutRoot, CancellationToken.None);
var migratedPart = ArchiveService.MoviePath(layoutRoot, previousBvid, "旧版多 P", 2, 202, true);
var migratedNfo = XDocument.Load(Path.ChangeExtension(migratedPart, ".nfo"));
if (!File.Exists(migratedPart) || (string?)migratedNfo.Root?.Element("title") != "P02" ||
    (string?)migratedNfo.Root?.Element("genre") != "保留的分类" ||
    (string?)migratedNfo.Root?.Element("uniqueid") != previousBvid + ":202" ||
    (await layoutStore.GetAsync(previousBvid, 202, CancellationToken.None))?.FilePath != migratedPart)
    throw new Exception("1.2.4.0 连续片段升级为独立合集条目时丢失视频或 NFO。");
var backfillState = new SyncState();
backfillState.Folders[42] = new FolderSyncState { KnownBvids = [previousBvid] };
await layoutService.QueueMetadataBackfillAsync(backfillState, layoutRoot, [42], CancellationToken.None);
if (!backfillState.Pending.ContainsKey(previousBvid))
    throw new Exception("旧版多 P NFO 未加入分批元数据补全队列。");
var notDownloaded = new VideoInfo("BV1xx411c7mH", "下载尚未成功", string.Empty,
    [new VideoPage(301, 1, "一"), new VideoPage(302, 2, "二")], null);
await layoutService.MigrateVideoArchivesAsync(notDownloaded, layoutRoot, CancellationToken.None);
if (Directory.Exists(Path.Combine(layoutRoot, $"下载尚未成功 [{notDownloaded.Bvid}] [boxset]")))
    throw new Exception("下载失败前不应出现空电影合集。");
var oldBoxBvid = "BV1xx411c7mJ";
var oldBoxFolder = Path.Combine(layoutRoot, oldBoxBvid + " [boxset]");
var oldBoxMovie = Path.Combine(oldBoxFolder, "P01-401", "P01-401.mp4");
Directory.CreateDirectory(Path.GetDirectoryName(oldBoxMovie)!);
File.WriteAllBytes(oldBoxMovie, [7, 8, 9]);
new XDocument(new XElement("movie", new XElement("title", "P01 老分 P"),
    new XElement("plot", "用户自定义简介"))).Save(Path.ChangeExtension(oldBoxMovie, ".nfo"));
new XDocument(new XElement("Item", new XElement("LocalTitle", "用户自定义合集标题"),
    new XElement("Overview", "用户自定义合集简介"))).Save(Path.Combine(oldBoxFolder, "collection.xml"));
await layoutStore.SaveAsync(new ArchiveRecord { Bvid = oldBoxBvid, Cid = 401,
    Title = "标题:含/非法字符", Status = "completed", FilePath = oldBoxMovie }, CancellationToken.None);
await layoutService.MigrateExistingArchivesAsync(layoutRoot, CancellationToken.None);
var newBoxMovie = ArchiveService.MoviePath(layoutRoot, oldBoxBvid, "标题:含/非法字符", 1, 401, true, "老分 P");
var newBoxFolder = Path.GetDirectoryName(Path.GetDirectoryName(newBoxMovie)!)!;
if (!File.Exists(newBoxMovie) || Directory.Exists(oldBoxFolder) ||
    Path.GetFileName(newBoxFolder) != $"标题 含 非法字符 [{oldBoxBvid}] [boxset]" ||
    (string?)XDocument.Load(Path.Combine(newBoxFolder, "collection.xml")).Root?.Element("Overview") != "用户自定义合集简介" ||
    (string?)XDocument.Load(Path.ChangeExtension(newBoxMovie, ".nfo")).Root?.Element("plot") != "用户自定义简介")
    throw new Exception("旧版 BV 合集迁移未保留自定义元数据或正确清理旧目录。");
var customMovieNfo = XDocument.Load(Path.ChangeExtension(newBoxMovie, ".nfo"));
customMovieNfo.Root!.SetElementValue("title", "用户自定义电影名");
customMovieNfo.Save(Path.ChangeExtension(newBoxMovie, ".nfo"));
await layoutService.MigrateExistingArchivesAsync(layoutRoot, CancellationToken.None);
if (!File.Exists(newBoxMovie) || Directory.Exists(oldBoxFolder) ||
    (string?)XDocument.Load(Path.ChangeExtension(newBoxMovie, ".nfo")).Root?.Element("title") != "用户自定义电影名")
    throw new Exception("标题目录迁移再次运行后不应改名或生成旧目录。");
var renamedVideo = new VideoInfo(oldBoxBvid, "改名后的视频", string.Empty,
    [new VideoPage(401, 1, "新分 P")], null, "新简介");
await layoutService.MigrateVideoArchivesAsync(renamedVideo, layoutRoot, CancellationToken.None);
var renamedMovie = ArchiveService.MoviePath(layoutRoot, oldBoxBvid, renamedVideo.Title, 1, 401, true, "新分 P");
if (!File.Exists(renamedMovie) || Directory.Exists(newBoxFolder) ||
    (await layoutStore.GetAsync(oldBoxBvid, 401, CancellationToken.None))?.FilePath != renamedMovie ||
    (string?)XDocument.Load(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(renamedMovie)!)!, "collection.xml"))
        .Root?.Element("Overview") != "用户自定义合集简介")
    throw new Exception("视频改名时未迁移合集元数据，或状态记录仍指向旧路径。");
var duplicateTitle = ArchiveService.MoviePath(layoutRoot, "BV1xx411c7mK", "归档标题", 1, 501, false);
if (Path.GetDirectoryName(duplicateTitle) == Path.GetDirectoryName(single))
    throw new Exception("相同标题的不同 BV 不应共用目录。");
var longTitle = ArchiveService.MoviePath(layoutRoot, "BV1xx411c7mL", string.Concat(Enumerable.Repeat("🚀标题", 80)),
    1, 601, false);
if (System.Text.Encoding.UTF8.GetByteCount(Path.GetFileName(longTitle)) > 255 ||
    Path.GetFileNameWithoutExtension(longTitle).Contains('�'))
    throw new Exception("长标题截断后不能超过文件系统文件名限制或拆断 Unicode 字符。");
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

const string danmakuXml = """<i><d p="1.25,1,25,16711680,0,0,0,0">红色{弹幕}\指令</d><d p="2,5,25,16777215,0,0,0,0">顶部</d><d p="3,6,25,255,0,0,0,0">反向</d><d p="4,7,25,0,0,0,0,0">高级弹幕</d><d p="bad,1,25,0">坏时间</d></i>""";
using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(danmakuXml)))
{
    var comments = await DanmakuConverter.ParseAsync(stream, CancellationToken.None);
    var ass = DanmakuConverter.ToAss(comments);
    if (comments.Count != 3 || !ass.Contains("Dialogue: 0,0:00:01.25", StringComparison.Ordinal) ||
        !ass.Contains("\\c&H0000FF&", StringComparison.Ordinal) ||
        !ass.Contains("红色（弹幕）\\\\指令", StringComparison.Ordinal) ||
        !ass.Contains("\\an8\\pos(960,60)", StringComparison.Ordinal) ||
        !ass.Contains("\\an7\\move(-", StringComparison.Ordinal) ||
        ass.Contains("高级弹幕", StringComparison.Ordinal))
        throw new Exception("弹幕 XML 解析、ASS 时间/颜色/位置或文本转义失败。");
}
try
{
    using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE i [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><i><d p='1,1,25,0'>&x;</d></i>"));
    await DanmakuConverter.ParseAsync(stream, CancellationToken.None);
    throw new Exception("不应接受带 DTD 的弹幕 XML。");
}
catch (System.Xml.XmlException) { }
var fetchedRecord = await layoutStore.GetAsync(bvid, 101, CancellationToken.None) ?? throw new Exception("归档记录丢失。");
fetchedRecord.DanmakuFetched = true;
await layoutStore.SaveAsync(fetchedRecord, CancellationToken.None);
if (!(await new ArchiveStore(Path.Combine(folder, "movie-state")).GetAsync(bvid, 101, CancellationToken.None))!.DanmakuFetched)
    throw new Exception("弹幕同步状态未持久化。");
var secondPartRecord = await layoutStore.GetAsync(bvid, 102, CancellationToken.None) ?? throw new Exception("第二分 P 记录丢失。");
secondPartRecord.DanmakuFetched = true;
await layoutStore.SaveAsync(secondPartRecord, CancellationToken.None);
var danmakuBackfill = new SyncState();
danmakuBackfill.Folders[42] = new FolderSyncState { KnownBvids = [bvid] };
await layoutService.QueueMetadataBackfillAsync(danmakuBackfill, layoutRoot, [42], CancellationToken.None);
if (danmakuBackfill.Pending.ContainsKey(bvid))
    throw new Exception("已补齐弹幕和元数据的视频不应重复入队。");
fetchedRecord.DanmakuFetched = false;
await layoutStore.SaveAsync(fetchedRecord, CancellationToken.None);
await layoutService.QueueMetadataBackfillAsync(danmakuBackfill, layoutRoot, [42], CancellationToken.None);
if (!danmakuBackfill.Pending.ContainsKey(bvid))
    throw new Exception("旧归档缺少弹幕时应加入分批补齐队列。");

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

Console.WriteLine("PASS: 弹幕 XML/ASS 与旧归档字幕迁移；大 CID、UP 主 NFO、无音轨 DASH、手动重试；不可访问错误码；状态与队列持久化；差异检测、退避、配置页、二维码。");
