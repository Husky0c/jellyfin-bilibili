using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Plugin.BiliArchive.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BiliArchive;

public sealed class ArchiveService
{
    private static readonly Regex ValidBvid = new("^BV[0-9A-Za-z]{10}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly BiliApi _api;
    private readonly ArchiveStore _store;
    private readonly SyncStateStore _syncStore;
    private readonly ILogger<ArchiveService> _logger;
    private int _manualStarting;

    public ArchiveService(BiliApi api, ArchiveStore store, SyncStateStore syncStore, ILogger<ArchiveService> logger)
    {
        _api = api;
        _store = store;
        _syncStore = syncStore;
        _logger = logger;
    }

    public bool IsRunning => _runGate.CurrentCount == 0 || Volatile.Read(ref _manualStarting) != 0;

    public async Task<bool> StartManualAsync(CancellationToken ct)
    {
        if (IsRunning || Interlocked.CompareExchange(ref _manualStarting, 1, 0) != 0) return false;
        try
        {
            var config = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("插件尚未初始化。");
            ValidateConfiguration(config);
            if (string.IsNullOrEmpty(_store.ReadCookie())) throw new BiliLoginException();
            var state = await _syncStore.LoadAsync(ct).ConfigureAwait(false);
            if (state.LoginRequired) throw new BiliLoginException();
            if (state.PausedUntil > DateTimeOffset.UtcNow)
                throw new InvalidOperationException($"B 站风控冷却中，预计 {state.PausedUntil:yyyy-MM-dd HH:mm} 后恢复。");
            _ = Task.Run(async () =>
            {
                try { await RunAsync(null, CancellationToken.None, true).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "手动归档任务失败"); }
                finally { Interlocked.Exchange(ref _manualStarting, 0); }
            });
            return true;
        }
        catch
        {
            Interlocked.Exchange(ref _manualStarting, 0);
            throw;
        }
    }

    public async Task RunAsync(IProgress<double>? progress, CancellationToken ct, bool force = false)
    {
        if (!await _runGate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("归档任务已经在运行。");

        try
        {
            var config = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("插件尚未初始化。");
            if (config.FolderIds.Count == 0 || string.IsNullOrWhiteSpace(config.ArchivePath))
            {
                progress?.Report(100);
                return;
            }
            ValidateConfiguration(config);
            var state = await _syncStore.LoadAsync(ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            if (state.LoginRequired || state.PausedUntil > now)
            {
                progress?.Report(100);
                return;
            }

            foreach (var id in config.FolderIds.Distinct())
                state.Folders.TryAdd(id, new FolderSyncState());

            var pollDue = config.FolderIds.Any(id => force || state.Folders[id].NextCheckAt <= now);
            var reconcileDue = config.FolderIds.Any(id => force || state.Folders[id].NextReconcileAt <= now);
            var workDue = state.Pending.Values.Any(x => x.NextAttemptAt <= now && IsSelected(x.Bvid, state, config.FolderIds));
            if (!pollDue && !reconcileDue && !workDue)
            {
                progress?.Report(100);
                return;
            }

            try
            {
                if (pollDue || reconcileDue)
                {
                    var allFolders = await _api.GetFoldersAsync(ct).ConfigureAwait(false);
                    var selected = allFolders.Where(x => config.FolderIds.Contains(x.Id)).ToArray();
                    var foundIds = selected.Select(x => x.Id).ToHashSet();
                    foreach (var missingId in config.FolderIds.Where(x => !foundIds.Contains(x)))
                    {
                        var missing = state.Folders[missingId];
                        if (force || missing.NextCheckAt <= now || missing.NextReconcileAt <= now)
                            _logger.LogWarning("已选收藏夹 {FolderId} 不在当前账号的收藏夹列表中，稍后复查", missingId);
                        missing.NextCheckAt = now.AddHours(4);
                        missing.NextReconcileAt = now.AddHours(4);
                    }
                    if (selected.Length != config.FolderIds.Distinct().Count())
                        await _syncStore.SaveAsync(state, ct).ConfigureAwait(false);
                    var records = await _store.ListAsync(ct).ConfigureAwait(false);
                    var completed = records.Where(x => x.Status == "completed" && x.FilePath is not null && File.Exists(x.FilePath))
                        .Select(x => x.Bvid).ToHashSet(StringComparer.Ordinal);
                    var failed = records.Where(x => x.Status is "failed" or "downloading").Select(x => x.Bvid)
                        .ToHashSet(StringComparer.Ordinal);

                    foreach (var folder in selected)
                    {
                        var folderState = state.Folders[folder.Id];
                        if (force || folderState.NextCheckAt <= now)
                        {
                            try { await RefreshMembershipAsync(folder, folderState, state, completed, failed, config, now, ct).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                            catch (BiliLoginException) { throw; }
                            catch (BiliRateLimitException) { throw; }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "检查收藏夹 {FolderId} 失败，下次重试", folder.Id);
                                folderState.NextCheckAt = now.AddHours(1);
                            }
                            await _syncStore.SaveAsync(state, ct).ConfigureAwait(false);
                        }

                        if (force || folderState.NextReconcileAt <= now)
                        {
                            try { await ReconcilePageAsync(folder, folderState, state, completed, now, ct).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                            catch (BiliLoginException) { throw; }
                            catch (BiliRateLimitException) { throw; }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "复核收藏夹 {FolderId} 第 {Page} 页失败", folder.Id, folderState.ReconcilePage);
                                folderState.NextReconcileAt = now.AddHours(3);
                            }
                            await _syncStore.SaveAsync(state, ct).ConfigureAwait(false);
                        }
                    }
                }

                await ProcessPendingAsync(state, config, progress, force, ct).ConfigureAwait(false);
                state.RateLimitCount = 0;
                state.PausedUntil = null;
                await _syncStore.SaveAsync(state, ct).ConfigureAwait(false);
                progress?.Report(100);
            }
            catch (BiliLoginException)
            {
                state.LoginRequired = true;
                await _syncStore.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (BiliRateLimitException)
            {
                state.RateLimitCount++;
                state.PausedUntil = DateTimeOffset.UtcNow.Add(SyncPlanner.RateLimitDelay(state.RateLimitCount, Jitter()));
                _logger.LogWarning("B 站风控，本插件暂停 API 请求至 {PausedUntil}", state.PausedUntil);
                await _syncStore.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally { _runGate.Release(); }
    }

    private async Task RefreshMembershipAsync(FavoriteFolder folder, FolderSyncState folderState, SyncState state,
        HashSet<string> completed, HashSet<string> failed, PluginConfiguration config, DateTimeOffset now, CancellationToken ct)
    {
        HashSet<string> current;
        try
        {
            var ids = await _api.GetFolderIdsAsync(folder.Id, ct).ConfigureAwait(false);
            if (ids.ResourceCount < folder.Count)
                throw new InvalidDataException($"清单仅返回 {ids.ResourceCount}/{folder.Count} 项，需要分页回退。");
            current = ids.Bvids.Where(x => ValidBvid.IsMatch(x)).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is BiliApiException or HttpRequestException or InvalidDataException)
        {
            _logger.LogWarning(ex, "BV 清单不可用，收藏夹 {FolderId} 回退到分页列表", folder.Id);
            current = [];
            await foreach (var item in _api.GetFolderVideosAsync(folder.Id, ct).ConfigureAwait(false))
            {
                if (!ValidBvid.IsMatch(item.Bvid)) continue;
                current.Add(item.Bvid);
                if (!item.Available) await MarkUnavailableAsync(item, ct).ConfigureAwait(false);
            }
        }

        var additions = SyncPlanner.ApplyMembership(folderState, state, current, completed, failed, now,
            config.MinScanMinutes, config.MaxScanMinutes, Jitter());
        _logger.LogInformation("收藏夹 {FolderId} 检查完成：{Count} 项，新增 {NewCount} 项，下次 {NextCheck}",
            folder.Id, current.Count, additions, folderState.NextCheckAt);
    }

    private async Task ReconcilePageAsync(FavoriteFolder folder, FolderSyncState folderState, SyncState state,
        HashSet<string> completed, DateTimeOffset now, CancellationToken ct)
    {
        var page = await _api.GetFolderVideoPageAsync(folder.Id, folderState.ReconcilePage, ct).ConfigureAwait(false);
        foreach (var item in page.Items)
        {
            if (!ValidBvid.IsMatch(item.Bvid)) continue;
            folderState.KnownBvids.Add(item.Bvid);
            if (!item.Available)
            {
                await MarkUnavailableAsync(item, ct).ConfigureAwait(false);
                continue;
            }

            var knownPageCount = folderState.PageCounts.GetValueOrDefault(item.Bvid);
            if (!state.Pending.ContainsKey(item.Bvid) &&
                (!completed.Contains(item.Bvid) || (item.PageCount > 0 && knownPageCount != item.PageCount)))
                state.Pending[item.Bvid] = new PendingVideo { Bvid = item.Bvid, DiscoveredAt = now };
            if (item.PageCount > 0) folderState.PageCounts[item.Bvid] = item.PageCount;
        }

        folderState.ReconcilePage = page.HasMore ? folderState.ReconcilePage + 1 : 1;
        folderState.NextReconcileAt = now.Add(page.HasMore ? TimeSpan.FromHours(3) : TimeSpan.FromDays(1));
    }

    private async Task ProcessPendingAsync(SyncState state, PluginConfiguration config, IProgress<double>? progress, bool force, CancellationToken ct)
    {
        var due = SelectPending(state, config.FolderIds, DateTimeOffset.UtcNow, force);
        for (var index = 0; index < due.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var pending = due[index];
            try
            {
                var video = await _api.GetVideoAsync(pending.Bvid, ct).ConfigureAwait(false);
                if (video.Pages.Count == 0) throw new InvalidDataException("B 站未返回任何视频分 P。");
                var placeholder = await _store.GetAsync(pending.Bvid, 0, ct).ConfigureAwait(false);
                if (placeholder is not null)
                {
                    placeholder.Status = "resolved";
                    placeholder.Error = null;
                    await _store.SaveAsync(placeholder, ct).ConfigureAwait(false);
                }

                var succeeded = true;
                foreach (var page in video.Pages)
                {
                    try { await ArchivePageAsync(video, page, config, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (BiliLoginException) { throw; }
                    catch (BiliRateLimitException) { throw; }
                    catch (Exception ex)
                    {
                        succeeded = false;
                        _logger.LogWarning(ex, "归档 {Bvid} / {Cid} 失败", pending.Bvid, page.Cid);
                    }
                }

                if (succeeded)
                {
                    state.Pending.Remove(pending.Bvid);
                    foreach (var folder in state.Folders.Values.Where(x => x.KnownBvids.Contains(pending.Bvid)))
                        folder.PageCounts[pending.Bvid] = video.Pages.Count;
                }
                else ScheduleRetry(pending);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (BiliLoginException) { throw; }
            catch (BiliRateLimitException) { throw; }
            catch (Exception ex)
            {
                var unavailable = ex is BiliApiException { Code: -404 or 62002 };
                _logger.LogWarning(ex, "获取 {Bvid} 信息失败", pending.Bvid);
                await _store.SaveAsync(new ArchiveRecord
                {
                    Bvid = pending.Bvid, Cid = 0, Title = pending.Bvid,
                    Status = unavailable ? "unavailable" : "failed", Error = ex.Message
                }, ct).ConfigureAwait(false);
                ScheduleRetry(pending, unavailable);
            }

            await _syncStore.SaveAsync(state, ct).ConfigureAwait(false);
            progress?.Report(95.0 * (index + 1) / Math.Max(1, due.Length));
            if (index + 1 < due.Length) await Task.Delay(1500, ct).ConfigureAwait(false);
        }
    }

    internal static PendingVideo[] SelectPending(SyncState state, IReadOnlyCollection<long> folderIds, DateTimeOffset now, bool force) =>
        state.Pending.Values.Where(x => force || x.NextAttemptAt <= now)
            .Where(x => IsSelected(x.Bvid, state, folderIds))
            .OrderByDescending(x => x.IsNewFavorite).ThenBy(x => x.NextAttemptAt).ThenBy(x => x.DiscoveredAt).Take(10).ToArray();

    private static void ScheduleRetry(PendingVideo pending, bool unavailable = false)
    {
        pending.Failures++;
        var delay = unavailable ? TimeSpan.FromHours(24) : SyncPlanner.RetryDelay(pending.Failures, Jitter());
        pending.NextAttemptAt = DateTimeOffset.UtcNow.Add(delay);
        pending.IsNewFavorite = false;
    }

    private static double Jitter() => 0.9 + Random.Shared.NextDouble() * 0.2;

    private static bool IsSelected(string bvid, SyncState state, IEnumerable<long> folderIds) =>
        folderIds.Any(id => state.Folders.TryGetValue(id, out var folder) && folder.KnownBvids.Contains(bvid));

    private async Task ArchivePageAsync(VideoInfo video, VideoPage page, PluginConfiguration config, CancellationToken ct)
    {
        var prior = await _store.GetAsync(video.Bvid, page.Cid, ct).ConfigureAwait(false);
        var folder = Path.Combine(config.ArchivePath, video.Bvid, $"P{page.Page:D2}-{page.Cid}");
        var final = Path.Combine(folder, "video.mp4");
        if (prior is { Status: "completed", FilePath: not null } && File.Exists(prior.FilePath))
        {
            WriteNfo(video, page, prior.FilePath);
            return;
        }
        if (File.Exists(final) && new FileInfo(final).Length > 0)
        {
            WriteNfo(video, page, final);
            await _store.SaveAsync(new ArchiveRecord { Bvid = video.Bvid, Cid = page.Cid, Title = video.Title, Status = "completed", FilePath = final }, ct).ConfigureAwait(false);
            return;
        }

        Directory.CreateDirectory(folder);
        var record = new ArchiveRecord { Bvid = video.Bvid, Cid = page.Cid, Title = video.Title, Status = "downloading" };
        await _store.SaveAsync(record, ct).ConfigureAwait(false);
        var videoPart = Path.Combine(folder, "video.part.m4s");
        var audioPart = Path.Combine(folder, "audio.part.m4s");
        var outputPart = Path.Combine(folder, "video.part.mp4");
        try
        {
            var streams = await _api.GetPlayStreamsAsync(video.Bvid, page.Cid, config.Quality, ct).ConfigureAwait(false);
            await _api.DownloadAsync(streams.Video.Url, videoPart, video.Bvid, ct).ConfigureAwait(false);
            if (streams.Audio is not null)
                await _api.DownloadAsync(streams.Audio.Url, audioPart, video.Bvid, ct).ConfigureAwait(false);
            await MuxAsync(FindFfmpeg(config.FfmpegPath), videoPart, streams.Audio is null ? null : audioPart, outputPart, ct).ConfigureAwait(false);
            if (!File.Exists(outputPart) || new FileInfo(outputPart).Length == 0)
                throw new IOException("FFmpeg 没有生成有效的输出文件。");
            File.Move(outputPart, final, true);
            WriteNfo(video, page, final);
            record.Status = "completed";
            record.FilePath = final;
            record.Error = null;
            await _store.SaveAsync(record, ct).ConfigureAwait(false);
            _logger.LogInformation("已归档 {Bvid} / {Cid}", video.Bvid, page.Cid);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BiliLoginException) { throw; }
        catch (BiliRateLimitException) { throw; }
        catch (Exception ex)
        {
            record.Status = "failed";
            record.Error = ex.Message;
            await _store.SaveAsync(record, ct).ConfigureAwait(false);
            throw;
        }
        finally
        {
            TryDelete(videoPart);
            TryDelete(audioPart);
            TryDelete(outputPart);
        }
    }

    private async Task MarkUnavailableAsync(FavoriteVideo video, CancellationToken ct)
    {
        var previous = await _store.GetAsync(video.Bvid, 0, ct).ConfigureAwait(false);
        if (previous?.Status == "unavailable") return;
        await _store.SaveAsync(new ArchiveRecord
        {
            Bvid = video.Bvid, Cid = 0, Title = video.Title,
            Status = "unavailable", Error = "收藏项已下架或失效，无法补回未下载的文件。"
        }, ct).ConfigureAwait(false);
    }

    private static async Task MuxAsync(string ffmpeg, string video, string? audio, string output, CancellationToken ct)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true
        } };
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", video };
        if (audio is not null) args.AddRange(["-i", audio]);
        args.AddRange(["-map", "0:v:0"]);
        if (audio is not null) args.AddRange(["-map", "1:a:0"]);
        args.AddRange(["-c", "copy", "-movflags", "+faststart", output]);
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("FFmpeg 失败：" + (await stderr.ConfigureAwait(false)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static string FindFfmpeg(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        const string jellyfin = "/usr/lib/jellyfin-ffmpeg/ffmpeg";
        return File.Exists(jellyfin) ? jellyfin : "ffmpeg";
    }

    internal static void WriteNfo(VideoInfo video, VideoPage page, string videoPath)
    {
        var nfoPath = Path.ChangeExtension(videoPath, ".nfo");
        var legacyPath = Path.Combine(Path.GetDirectoryName(videoPath)!, "movie.nfo");
        var title = video.Pages.Count == 1 ? video.Title : $"{video.Title} - P{page.Page:D2} {page.Part}";
        var id = video.Bvid + ":" + page.Cid;
        var plot = $"Bilibili {video.Bvid} / CID {page.Cid}";
        if (video.Uploader is { Mid: > 0 } creator)
            plot += $"\nUP主：{creator.Name}\nUP主页：https://space.bilibili.com/{creator.Mid}";
        var nfo = File.Exists(nfoPath) ? XDocument.Load(nfoPath) : new XDocument(new XElement("movie",
            new XElement("title", title),
            new XElement("plot", plot),
            new XElement("uniqueid", new XAttribute("type", "bilibili"), new XAttribute("default", "true"), id)));
        var root = nfo.Root ?? throw new InvalidDataException($"NFO 没有根元素：{nfoPath}");
        var changed = !File.Exists(nfoPath);
        if (video.Uploader is { Name: var name } uploader && !string.IsNullOrWhiteSpace(name))
        {
            var actor = root.Elements("actor").FirstOrDefault(x =>
                string.Equals((string?)x.Element("name"), name, StringComparison.Ordinal) &&
                string.Equals((string?)x.Element("role"), "UP主", StringComparison.Ordinal));
            if (actor is null)
            {
                actor = new XElement("actor", new XElement("name", name), new XElement("role", "UP主"),
                    new XElement("type", "Actor"));
                root.Add(actor);
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(uploader.Face) &&
                !string.Equals((string?)actor.Element("thumb"), uploader.Face, StringComparison.Ordinal))
            {
                actor.SetElementValue("thumb", uploader.Face);
                changed = true;
            }
        }
        if (changed) nfo.Save(nfoPath);

        // Remove only the exact three-field sidecar emitted by older plugin versions.
        // A user-edited movie.nfo is kept intact.
        if (legacyPath != nfoPath && File.Exists(legacyPath) && IsLegacyPluginNfo(legacyPath, video.Bvid, page.Cid))
            File.Delete(legacyPath);
    }

    private static bool IsLegacyPluginNfo(string path, string bvid, long cid)
    {
        XElement? root;
        try { root = XDocument.Load(path).Root; }
        catch (System.Xml.XmlException) { return false; }
        if (root?.Name != "movie" || root.HasAttributes) return false;
        var elements = root.Elements().ToArray();
        return elements.Length == 3 &&
            elements[0].Name == "title" && !elements[0].HasAttributes &&
            elements[1].Name == "plot" && !elements[1].HasAttributes &&
            elements[2].Name == "uniqueid" &&
            (string?)elements[2].Attribute("type") == "bilibili" &&
            (string?)elements[2].Attribute("default") == "true" &&
            elements[2].Value == bvid + ":" + cid &&
            elements[2].Attributes().Count() == 2 &&
            elements[1].Value == $"Bilibili {bvid} / CID {cid}";
    }

    private static void ValidateConfiguration(PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.ArchivePath)) throw new InvalidOperationException("请先设置归档目录。");
        if (!Path.IsPathFullyQualified(config.ArchivePath)) throw new InvalidOperationException("归档目录必须是容器内的绝对路径。");
        if (config.FolderIds.Count == 0) throw new InvalidOperationException("请先选择至少一个收藏夹。");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }
}
