using System.Diagnostics;
using System.Text;
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
            await MigrateExistingArchivesAsync(config.ArchivePath, ct).ConfigureAwait(false);
            var state = await _syncStore.LoadAsync(ct).ConfigureAwait(false);
            var records = await _store.ListAsync(ct).ConfigureAwait(false);
            var unavailableRecords = records.Where(x => x.Cid == 0 && x.Status == "unavailable")
                .ToDictionary(x => x.Bvid, StringComparer.Ordinal);
            if (ApplyUnavailableCooldown(state, unavailableRecords))
                await _syncStore.SaveAsync(state, ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            if (state.LoginRequired || state.PausedUntil > now)
            {
                progress?.Report(100);
                return;
            }

            foreach (var id in config.FolderIds.Distinct())
                state.Folders.TryAdd(id, new FolderSyncState());
            await QueueMetadataBackfillAsync(state, config.ArchivePath, config.FolderIds, ct).ConfigureAwait(false);

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

                if (pollDue || reconcileDue)
                {
                    unavailableRecords = (await _store.ListAsync(ct).ConfigureAwait(false))
                        .Where(x => x.Cid == 0 && x.Status == "unavailable")
                        .ToDictionary(x => x.Bvid, StringComparer.Ordinal);
                    if (ApplyUnavailableCooldown(state, unavailableRecords))
                        await _syncStore.SaveAsync(state, ct).ConfigureAwait(false);
                }
                await ProcessPendingAsync(state, config, progress, force,
                    unavailableRecords.Keys.ToHashSet(StringComparer.Ordinal), ct).ConfigureAwait(false);
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

    private async Task ProcessPendingAsync(SyncState state, PluginConfiguration config, IProgress<double>? progress,
        bool force, IReadOnlySet<string> unavailableBvids, CancellationToken ct)
    {
        var due = SelectPending(state, config.FolderIds, DateTimeOffset.UtcNow, force, unavailableBvids);
        for (var index = 0; index < due.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var pending = due[index];
            try
            {
                var video = await _api.GetVideoAsync(pending.Bvid, ct).ConfigureAwait(false);
                if (video.Pages.Count == 0) throw new InvalidDataException("B 站未返回任何视频分 P。");
                await MigrateVideoArchivesAsync(video, config.ArchivePath, ct).ConfigureAwait(false);
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
                var unavailable = IsUnavailableVideoError(ex);
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

    internal static PendingVideo[] SelectPending(SyncState state, IReadOnlyCollection<long> folderIds, DateTimeOffset now,
        bool force, IReadOnlySet<string>? unavailableBvids = null) =>
        state.Pending.Values.Where(x => x.NextAttemptAt <= now || (force && !(unavailableBvids?.Contains(x.Bvid) ?? false)))
            .Where(x => IsSelected(x.Bvid, state, folderIds))
            .OrderByDescending(x => x.IsNewFavorite).ThenBy(x => x.NextAttemptAt).ThenBy(x => x.DiscoveredAt).Take(10).ToArray();

    internal static bool ApplyUnavailableCooldown(SyncState state, IReadOnlyDictionary<string, ArchiveRecord> unavailableRecords)
    {
        var changed = false;
        foreach (var pending in state.Pending.Values)
        {
            if (pending.Failures <= 0 || !unavailableRecords.TryGetValue(pending.Bvid, out var record)) continue;
            var nextAttempt = record.UpdatedAt.Add(SyncPlanner.UnavailableRetryDelay(pending.Failures));
            if (pending.NextAttemptAt >= nextAttempt) continue;
            pending.NextAttemptAt = nextAttempt;
            changed = true;
        }
        return changed;
    }

    internal async Task QueueMetadataBackfillAsync(SyncState state, string archivePath,
        IReadOnlyCollection<long> folderIds, CancellationToken ct)
    {
        var records = await _store.ListAsync(ct).ConfigureAwait(false);
        var added = 0;
        foreach (var group in records.Where(x => x.Status == "completed" && x.FilePath is not null &&
                     File.Exists(x.FilePath) && IsInArchiveRoot(x.FilePath, archivePath))
                 .GroupBy(x => x.Bvid, StringComparer.Ordinal))
        {
            if (state.Pending.ContainsKey(group.Key) || !IsSelected(group.Key, state, folderIds) ||
                !group.Any(x => NeedsMetadataBackfill(x) || !x.DanmakuFetched)) continue;
            state.Pending[group.Key] = new PendingVideo { Bvid = group.Key };
            if (++added == 10) break;
        }
        if (added > 0) await _syncStore.SaveAsync(state, ct).ConfigureAwait(false);
    }

    private static bool NeedsMetadataBackfill(ArchiveRecord record)
    {
        var path = Path.ChangeExtension(record.FilePath!, ".nfo");
        if (!File.Exists(path)) return true;
        try
        {
            var plot = (string?)XDocument.Load(path).Root?.Element("plot");
            if (plot is null) return true;
            return IsLegacyPluginPlot(plot, record.Bvid, record.Cid);
        }
        catch (System.Xml.XmlException) { return false; }
    }

    private static bool IsLegacyPluginPlot(string? plot, string bvid, long cid) =>
        plot is not null && Regex.IsMatch(plot,
            "^Bilibili " + Regex.Escape(bvid) + " / CID " + cid +
            @"(?:\nUP主：[^\n]*\nUP主页：https://space\.bilibili\.com/[0-9]+)?$",
            RegexOptions.CultureInvariant);

    internal static bool IsUnavailableVideoError(Exception ex) =>
        ex is BiliApiException { Code: -404 or 62002 or 62012 };

    private static void ScheduleRetry(PendingVideo pending, bool unavailable = false)
    {
        if (pending.Failures < int.MaxValue) pending.Failures++;
        var delay = unavailable ? SyncPlanner.UnavailableRetryDelay(pending.Failures) : SyncPlanner.RetryDelay(pending.Failures, Jitter());
        pending.NextAttemptAt = DateTimeOffset.UtcNow.Add(delay);
        pending.IsNewFavorite = false;
    }

    private static double Jitter() => 0.9 + Random.Shared.NextDouble() * 0.2;

    private static bool IsSelected(string bvid, SyncState state, IEnumerable<long> folderIds) =>
        folderIds.Any(id => state.Folders.TryGetValue(id, out var folder) && folder.KnownBvids.Contains(bvid));

    private async Task ArchivePageAsync(VideoInfo video, VideoPage page, PluginConfiguration config, CancellationToken ct)
    {
        var prior = await _store.GetAsync(video.Bvid, page.Cid, ct).ConfigureAwait(false);
        var multiPage = video.Pages.Count > 1 ||
            prior?.FilePath?.Contains("[boxset]", StringComparison.OrdinalIgnoreCase) == true;
        var final = MoviePath(config.ArchivePath, video.Bvid, video.Title, page.Page, page.Cid, multiPage, page.Part);
        var folder = Path.GetDirectoryName(final)!;
        if (prior is { Status: "completed", FilePath: not null } && File.Exists(prior.FilePath))
        {
            WriteNfo(video, page, prior.FilePath);
            if (multiPage && IsInArchiveRoot(prior.FilePath, config.ArchivePath) &&
                prior.FilePath.Contains("[boxset]", StringComparison.OrdinalIgnoreCase))
                WriteCollectionMetadata(config.ArchivePath, video.Bvid, video.Title, video.Description);
            await EnsureDanmakuAsync(video, page, prior, prior.FilePath, ct).ConfigureAwait(false);
            return;
        }
        if (File.Exists(final) && new FileInfo(final).Length > 0)
        {
            WriteNfo(video, page, final);
            if (multiPage) WriteCollectionMetadata(config.ArchivePath, video.Bvid, video.Title, video.Description);
            var recovered = new ArchiveRecord { Bvid = video.Bvid, Cid = page.Cid, Title = video.Title, Status = "completed", FilePath = final };
            await _store.SaveAsync(recovered, ct).ConfigureAwait(false);
            await EnsureDanmakuAsync(video, page, recovered, final, ct).ConfigureAwait(false);
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
            if (multiPage) WriteCollectionMetadata(config.ArchivePath, video.Bvid, video.Title, video.Description);
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
            if (!File.Exists(final))
            {
                TryDeleteEmptyDirectory(folder);
                if (multiPage) TryDeleteEmptyDirectory(Path.GetDirectoryName(folder)!);
            }
        }
        if (record.Status == "completed")
            await EnsureDanmakuAsync(video, page, record, final, ct).ConfigureAwait(false);
    }

    private async Task EnsureDanmakuAsync(VideoInfo video, VideoPage page, ArchiveRecord record, string videoPath, CancellationToken ct)
    {
        if (record.DanmakuFetched) return;
        var subtitle = DanmakuPath(videoPath);
        try
        {
            if (!File.Exists(subtitle))
            {
                var comments = await _api.GetDanmakuAsync(video.Bvid, page.Cid, ct).ConfigureAwait(false);
                if (comments.Count > 0)
                {
                    var temp = subtitle + ".tmp";
                    try
                    {
                        await File.WriteAllTextAsync(temp, DanmakuConverter.ToAss(comments), ct).ConfigureAwait(false);
                        File.Move(temp, subtitle);
                    }
                    finally { TryDelete(temp); }
                }
            }
            record.DanmakuFetched = true;
            await _store.SaveAsync(record, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BiliLoginException) { throw; }
        catch (BiliRateLimitException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取 {Bvid} / {Cid} 弹幕失败，稍后重试", video.Bvid, page.Cid);
        }
    }

    internal static string DanmakuPath(string videoPath) => Path.ChangeExtension(videoPath, ".danmaku.ass");

    internal static string MoviePath(string archivePath, string bvid, string title, int page, long cid,
        bool multiPage, string? part = null)
    {
        var folder = CollectionFolder(archivePath, bvid, title, multiPage);
        if (!multiPage)
        {
            var name = Path.GetFileName(folder);
            return Path.Combine(folder, name + ".mp4");
        }
        var pageName = $"P{page:D2}" + (string.IsNullOrWhiteSpace(part) ? string.Empty :
            $" - {SafeName(part, "分P", 100)}") + $" (CID {cid})";
        return Path.Combine(folder, pageName, pageName + ".mp4");
    }

    private static string CollectionFolder(string archivePath, string bvid, string title, bool multiPage) =>
        Path.Combine(archivePath, $"{SafeName(title, bvid, 140)} [{bvid}]" + (multiPage ? " [boxset]" : string.Empty));

    private static string SafeName(string? value, string fallback, int maxUtf8Bytes)
    {
        var clean = Regex.Replace(value ?? string.Empty, "[\\x00-\\x1f\\x7f<>:\"/\\\\|?*]", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '.');
        if (clean.Length == 0) clean = fallback;
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var rune in clean.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxUtf8Bytes) break;
            result.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return result.ToString().TrimEnd(' ', '.');
    }

    internal async Task MigrateVideoArchivesAsync(VideoInfo video, string archivePath, CancellationToken ct)
    {
        var records = (await _store.ListAsync(ct).ConfigureAwait(false))
            .Where(x => x.Bvid == video.Bvid && x.Status == "completed" && x.FilePath is not null)
            .ToArray();
        var multiPage = video.Pages.Count > 1 || records.Length > 1 ||
            records.Any(x => x.FilePath!.Contains("[boxset]", StringComparison.OrdinalIgnoreCase));
        var oldCollectionFolders = records.Select(x => x.FilePath!)
            .Where(x => IsInArchiveRoot(x, archivePath))
            .Select(x => FindBoxSetFolder(x, archivePath))
            .Where(x => x is not null).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var page in video.Pages)
        {
            var record = records.FirstOrDefault(x => x.Cid == page.Cid);
            if (record is not null)
                await MoveArchiveAsync(record, archivePath, video.Title, page.Page, multiPage, page.Part, ct).ConfigureAwait(false);
        }
        if (multiPage)
            foreach (var oldFolder in oldCollectionFolders)
                MoveCollectionMetadata(oldFolder!, CollectionFolder(archivePath, video.Bvid, video.Title, true));
        if (multiPage && records.Any(x => x.FilePath is not null && File.Exists(x.FilePath) &&
                x.FilePath.Contains("[boxset]", StringComparison.OrdinalIgnoreCase) &&
                IsInArchiveRoot(x.FilePath, archivePath)))
            WriteCollectionMetadata(archivePath, video.Bvid, video.Title, video.Description);
    }

    internal async Task MigrateExistingArchivesAsync(string archivePath, CancellationToken ct)
    {
        var records = (await _store.ListAsync(ct).ConfigureAwait(false))
            .Where(x => x.Status == "completed" && x.FilePath is not null)
            .GroupBy(x => x.Bvid, StringComparer.Ordinal);
        foreach (var group in records)
        {
            var oldCollectionFolders = group.Select(x => x.FilePath!)
                .Where(x => IsInArchiveRoot(x, archivePath))
                .Select(x => FindBoxSetFolder(x, archivePath))
                .Where(x => x is not null).Distinct(StringComparer.Ordinal).ToArray();
            var entries = group.Select(x => (Record: x, Page: StoredPage(x)))
                .Where(x => x.Page > 0).ToArray();
            var multiPage = group.Count() > 1 ||
                group.Any(x => x.FilePath!.Contains("[boxset]", StringComparison.OrdinalIgnoreCase));
            foreach (var (record, page) in entries)
            {
                if (IsTitledLayout(record.FilePath!, archivePath, record.Bvid)) continue;
                try { await MoveArchiveAsync(record, archivePath, record.Title, page, multiPage,
                    StoredPart(record, page), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    _logger.LogWarning(ex, "整理旧归档 {Bvid} / {Cid} 失败，保留原文件", record.Bvid, record.Cid);
                }
            }
            if (multiPage)
                foreach (var oldFolder in oldCollectionFolders)
                {
                    try { MoveCollectionMetadata(oldFolder!, CollectionFolder(archivePath, group.Key, group.First().Title, true)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                    {
                        _logger.LogWarning(ex, "整理旧合集 {Bvid} 元数据失败，保留原文件", group.Key);
                    }
                }
            if (multiPage && entries.Any(x => x.Record.FilePath is not null && File.Exists(x.Record.FilePath) &&
                    x.Record.FilePath.Contains("[boxset]", StringComparison.OrdinalIgnoreCase) &&
                    IsInArchiveRoot(x.Record.FilePath, archivePath)))
                WriteCollectionMetadata(archivePath, group.Key, group.First().Title, string.Empty);
        }
    }

    private static int StoredPage(ArchiveRecord record)
    {
        var path = record.FilePath!;
        var legacy = LegacyPage(path, record.Cid);
        if (legacy > 0) return legacy;
        var name = Path.GetFileNameWithoutExtension(path);
        if (name == record.Bvid) return 1;
        if (name.EndsWith($" [{record.Bvid}]", StringComparison.Ordinal)) return 1;
        var part = Regex.Match(name, $"^P([0-9]+)-{record.Cid}$", RegexOptions.CultureInvariant);
        if (part.Success && int.TryParse(part.Groups[1].Value, out var partNumber)) return partNumber;
        part = Regex.Match(name, $@"^P([0-9]+)(?: - .+)? \(CID {record.Cid}\)$", RegexOptions.CultureInvariant);
        if (part.Success && int.TryParse(part.Groups[1].Value, out partNumber)) return partNumber;
        var match = Regex.Match(name, "^" + Regex.Escape(record.Bvid) + @"-cd([0-9]+)$",
            RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var page) ? page : 0;
    }

    private static int LegacyPage(string path, long cid)
    {
        var match = Regex.Match(Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty,
            $"^P([0-9]+)-{cid}$", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var page) ? page : 0;
    }

    private static string? StoredPart(ArchiveRecord record, int page)
    {
        var path = Path.ChangeExtension(record.FilePath!, ".nfo");
        if (!File.Exists(path)) return null;
        try
        {
            var title = (string?)XDocument.Load(path).Root?.Element("title");
            if (string.IsNullOrWhiteSpace(title)) return null;
            var prefix = $"P{page:D2} ";
            if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return title[prefix.Length..];
            prefix = record.Title + " - " + prefix;
            return title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? title[prefix.Length..] : null;
        }
        catch (System.Xml.XmlException) { return null; }
    }

    private static string? FindBoxSetFolder(string path, string archivePath)
    {
        var root = Path.GetFullPath(archivePath);
        for (var parent = Path.GetDirectoryName(path); parent is not null &&
             IsInArchiveRoot(parent, root); parent = Path.GetDirectoryName(parent))
        {
            if (Path.GetFileName(parent).EndsWith(" [boxset]", StringComparison.OrdinalIgnoreCase)) return parent;
        }
        return null;
    }

    private static bool IsTitledLayout(string path, string archivePath, string bvid)
    {
        if (!IsInArchiveRoot(path, archivePath)) return false;
        var relative = Path.GetRelativePath(archivePath, path);
        var rootName = relative.Split(Path.DirectorySeparatorChar)[0];
        return rootName.EndsWith($" [{bvid}]", StringComparison.Ordinal) ||
            rootName.EndsWith($" [{bvid}] [boxset]", StringComparison.Ordinal);
    }

    private static void MoveCollectionMetadata(string sourceFolder, string targetFolder)
    {
        if (Path.GetFullPath(sourceFolder) == Path.GetFullPath(targetFolder) || !Directory.Exists(sourceFolder) ||
            Directory.EnumerateFiles(sourceFolder, "*.mp4", SearchOption.AllDirectories).Any()) return;
        var oldMetadata = Path.Combine(sourceFolder, "collection.xml");
        var newMetadata = Path.Combine(targetFolder, "collection.xml");
        if (File.Exists(oldMetadata))
        {
            if (File.Exists(newMetadata)) return;
            Directory.CreateDirectory(targetFolder);
            File.Move(oldMetadata, newMetadata);
        }
        TryDeleteEmptyDirectory(sourceFolder);
    }

    private static bool IsInArchiveRoot(string path, string archivePath)
    {
        var root = Path.GetFullPath(archivePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal);
    }

    private async Task MoveArchiveAsync(ArchiveRecord record, string archivePath, string title, int page,
        bool multiPage, string? part, CancellationToken ct)
    {
        var source = record.FilePath!;
        var target = MoviePath(archivePath, record.Bvid, title, page, record.Cid, multiPage, part);
        if (!IsInArchiveRoot(source, archivePath) ||
            !File.Exists(source) || Path.GetFullPath(source) == Path.GetFullPath(target)) return;
        if (File.Exists(target)) throw new IOException($"归档迁移目标已存在，请检查：{target}");
        var sourceNfo = Path.ChangeExtension(source, ".nfo");
        var targetNfo = Path.ChangeExtension(target, ".nfo");
        var sourceDanmaku = DanmakuPath(source);
        var targetDanmaku = DanmakuPath(target);
        if (File.Exists(targetNfo))
            throw new IOException($"归档迁移 NFO 目标已存在，请检查：{targetNfo}");
        if (File.Exists(sourceDanmaku) && File.Exists(targetDanmaku))
            throw new IOException($"归档迁移弹幕目标已存在，请检查：{targetDanmaku}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var movieNfo = Path.Combine(Path.GetDirectoryName(source)!, "movie.nfo");
        var originalNfo = File.Exists(sourceNfo) ? sourceNfo : File.Exists(movieNfo) ? movieNfo : null;
        var movedDanmaku = false;
        File.Move(source, target);
        try
        {
            if (originalNfo is not null) File.Move(originalNfo, targetNfo);
            if (File.Exists(sourceDanmaku))
            {
                File.Move(sourceDanmaku, targetDanmaku);
                movedDanmaku = true;
            }
            NormalizeMovieNfo(record, page, multiPage, targetNfo);
            record.FilePath = target;
            await _store.SaveAsync(record, ct).ConfigureAwait(false);
        }
        catch
        {
            record.FilePath = source;
            if (movedDanmaku && File.Exists(targetDanmaku) && !File.Exists(sourceDanmaku))
                File.Move(targetDanmaku, sourceDanmaku);
            if (originalNfo is not null && File.Exists(targetNfo) && !File.Exists(originalNfo))
                File.Move(targetNfo, originalNfo);
            else if (originalNfo is null && File.Exists(targetNfo)) File.Delete(targetNfo);
            if (File.Exists(target) && !File.Exists(source)) File.Move(target, source);
            throw;
        }
        var sourceFolder = Path.GetDirectoryName(source)!;
        TryDeleteEmptyDirectory(sourceFolder);
        var parent = Path.GetDirectoryName(sourceFolder);
        if (parent is not null && IsInArchiveRoot(parent, archivePath)) TryDeleteEmptyDirectory(parent);
        _logger.LogInformation("已整理归档 {Bvid} / {Cid}：{Path}", record.Bvid, record.Cid, target);
    }

    private static void NormalizeMovieNfo(ArchiveRecord record, int page, bool multiPage, string path)
    {
        var fallbackTitle = multiPage ? $"P{page:D2}" : record.Title;
        var id = multiPage ? record.Bvid + ":" + record.Cid : record.Bvid;
        if (!File.Exists(path))
        {
            new XDocument(new XElement("movie", new XElement("title", fallbackTitle),
                new XElement("plot", $"Bilibili {record.Bvid} / CID {record.Cid}"),
                new XElement("uniqueid", new XAttribute("type", "bilibili"),
                    new XAttribute("default", "true"), id))).Save(path);
            return;
        }
        var nfo = XDocument.Load(path);
        var root = nfo.Root;
        if (root is null) return;
        var changed = false;
        var title = (string?)root.Element("title");
        if (multiPage && title == record.Title)
        {
            root.SetElementValue("title", fallbackTitle);
            changed = true;
        }
        else if (multiPage && title is not null && title.StartsWith(record.Title + " - ", StringComparison.Ordinal))
        {
            root.SetElementValue("title", title[(record.Title.Length + 3)..]);
            changed = true;
        }
        else if (!multiPage && title is not null && Regex.IsMatch(title,
                     "^" + Regex.Escape(record.Title) + @" - P[0-9]+ .+$", RegexOptions.CultureInvariant))
        {
            root.SetElementValue("title", record.Title);
            changed = true;
        }
        var uniqueId = root.Elements("uniqueid").FirstOrDefault(x => (string?)x.Attribute("type") == "bilibili");
        if (uniqueId is not null && uniqueId.Value != id &&
            (uniqueId.Value == record.Bvid || uniqueId.Value == record.Bvid + ":" + record.Cid))
        {
            uniqueId.Value = id;
            changed = true;
        }
        if (multiPage && root.Element("sorttitle") is null)
        {
            root.Add(new XElement("sorttitle", $"P{page:D2}"));
            changed = true;
        }
        if (changed) nfo.Save(path);
    }

    private static void WriteCollectionMetadata(string archivePath, string bvid, string title, string description)
    {
        var folder = CollectionFolder(archivePath, bvid, title, true);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "collection.xml");
        if (!File.Exists(path))
        {
            new XDocument(new XElement("Item", new XElement("LocalTitle", title),
                new XElement("Overview", description))).Save(path);
            return;
        }
        var xml = XDocument.Load(path);
        if (xml.Root?.Name != "Item") return;
        var changed = false;
        if (string.IsNullOrWhiteSpace((string?)xml.Root.Element("LocalTitle")))
        {
            xml.Root.SetElementValue("LocalTitle", title);
            changed = true;
        }
        if (string.IsNullOrWhiteSpace((string?)xml.Root.Element("Overview")) &&
            !string.IsNullOrWhiteSpace(description))
        {
            xml.Root.SetElementValue("Overview", description);
            changed = true;
        }
        if (changed) xml.Save(path);
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
        var multiPage = video.Pages.Count > 1 || videoPath.Contains("[boxset]", StringComparison.OrdinalIgnoreCase);
        var partTitle = string.IsNullOrWhiteSpace(page.Part) ? $"P{page.Page:D2}" : $"P{page.Page:D2} {page.Part}";
        var title = multiPage ? partTitle : video.Title;
        var id = multiPage ? video.Bvid + ":" + page.Cid : video.Bvid;
        var plot = multiPage ? $"{video.Title} · {partTitle}" : video.Title;
        if (!string.IsNullOrWhiteSpace(video.Description)) plot += "\n\n" + video.Description.Trim();
        plot += $"\n\nBilibili {video.Bvid} / CID {page.Cid}\nhttps://www.bilibili.com/video/{video.Bvid}?p={page.Page}";
        if (video.Uploader is { Mid: > 0 } creator)
        {
            plot += $"\nUP主：{creator.Name}\nUP主页：https://space.bilibili.com/{creator.Mid}";
        }
        var nfo = File.Exists(nfoPath) ? XDocument.Load(nfoPath) : new XDocument(new XElement("movie",
            new XElement("title", title),
            new XElement("plot", plot),
            new XElement("uniqueid", new XAttribute("type", "bilibili"), new XAttribute("default", "true"), id)));
        var root = nfo.Root ?? throw new InvalidDataException($"NFO 没有根元素：{nfoPath}");
        var changed = !File.Exists(nfoPath);
        var oldTitle = $"{video.Title} - P{page.Page:D2} {page.Part}";
        var currentTitle = (string?)root.Element("title");
        if (currentTitle == oldTitle || (multiPage && (currentTitle == video.Title ||
                currentTitle == $"P{page.Page:D2}")))
        {
            root.SetElementValue("title", title);
            changed = true;
        }
        var currentPlot = (string?)root.Element("plot");
        if (currentPlot is null || IsLegacyPluginPlot(currentPlot, video.Bvid, page.Cid))
        {
            root.SetElementValue("plot", plot);
            changed = true;
        }
        var uniqueId = root.Elements("uniqueid").FirstOrDefault(x => (string?)x.Attribute("type") == "bilibili");
        if (uniqueId is not null && uniqueId.Value != id &&
            (uniqueId.Value == video.Bvid || uniqueId.Value == video.Bvid + ":" + page.Cid))
        {
            uniqueId.Value = id;
            changed = true;
        }
        if (multiPage && root.Element("sorttitle") is null)
        {
            root.Add(new XElement("sorttitle", $"P{page.Page:D2}"));
            changed = true;
        }
        var image = !string.IsNullOrWhiteSpace(page.FirstFrame) ? page.FirstFrame : video.Cover;
        if (!string.IsNullOrWhiteSpace(image) && root.Element("thumb") is null)
        {
            root.Add(new XElement("thumb", image));
            changed = true;
        }
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

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
