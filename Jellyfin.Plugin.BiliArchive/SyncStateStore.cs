using System.Text.Json;

namespace Jellyfin.Plugin.BiliArchive;

public sealed class PendingVideo
{
    public string Bvid { get; set; } = string.Empty;
    public DateTimeOffset DiscoveredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.MinValue;
    public int Failures { get; set; }
    public bool IsNewFavorite { get; set; }
}

public sealed class FolderSyncState
{
    public HashSet<string> KnownBvids { get; set; } = [];
    public bool HasSnapshot { get; set; }
    public Dictionary<string, int> PageCounts { get; set; } = [];
    public DateTimeOffset NextCheckAt { get; set; } = DateTimeOffset.MinValue;
    public int IdleChecks { get; set; }
    public int ReconcilePage { get; set; } = 1;
    public DateTimeOffset NextReconcileAt { get; set; } = DateTimeOffset.MinValue;
}

public sealed class SyncState
{
    public Dictionary<long, FolderSyncState> Folders { get; set; } = [];
    public Dictionary<string, PendingVideo> Pending { get; set; } = [];
    public DateTimeOffset? PausedUntil { get; set; }
    public int RateLimitCount { get; set; }
    public bool LoginRequired { get; set; }
}

public sealed class SyncStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public SyncStateStore() : this(Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("插件尚未初始化。"))
    {
    }

    public SyncStateStore(string folder)
    {
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "sync-state.json");
    }

    public async Task<SyncState> LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return new SyncState();
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<SyncState>(stream, Options, ct).ConfigureAwait(false) ?? new SyncState();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(SyncState state, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var temp = _path + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, state, Options, ct).ConfigureAwait(false);
            File.Move(temp, _path, true);
        }
        finally { _gate.Release(); }
    }

    public async Task MarkLoginRestoredAsync(CancellationToken ct)
    {
        var state = await LoadAsync(ct).ConfigureAwait(false);
        state.LoginRequired = false;
        state.PausedUntil = null;
        foreach (var folder in state.Folders.Values) folder.NextCheckAt = DateTimeOffset.MinValue;
        await SaveAsync(state, ct).ConfigureAwait(false);
    }

    public async Task MarkLoggedOutAsync(CancellationToken ct)
    {
        var state = await LoadAsync(ct).ConfigureAwait(false);
        state.LoginRequired = true;
        await SaveAsync(state, ct).ConfigureAwait(false);
    }
}

public static class SyncPlanner
{
    public static int ApplyMembership(FolderSyncState folder, SyncState state, IEnumerable<string> bvids,
        IReadOnlySet<string> completed, IReadOnlySet<string> failed, DateTimeOffset now,
        int minMinutes, int maxMinutes, double jitter)
    {
        var current = bvids.ToHashSet(StringComparer.Ordinal);
        var additions = current.Except(folder.KnownBvids, StringComparer.Ordinal).ToArray();
        foreach (var bvid in additions)
        {
            if ((!completed.Contains(bvid) || failed.Contains(bvid)) && !state.Pending.ContainsKey(bvid))
                state.Pending[bvid] = new PendingVideo
                {
                    Bvid = bvid, DiscoveredAt = now, IsNewFavorite = folder.HasSnapshot
                };
            else if (folder.HasSnapshot && state.Pending.TryGetValue(bvid, out var queued))
            {
                queued.IsNewFavorite = true;
                queued.DiscoveredAt = now;
            }
        }

        folder.KnownBvids = current;
        folder.HasSnapshot = true;
        folder.IdleChecks = additions.Length > 0 ? 0 : Math.Min(5, folder.IdleChecks + 1);
        folder.NextCheckAt = now.Add(NextCheckDelay(folder.IdleChecks, minMinutes, maxMinutes, jitter));
        return additions.Length;
    }

    public static TimeSpan NextCheckDelay(int idleChecks, int minMinutes, int maxMinutes, double jitter)
    {
        minMinutes = Math.Clamp(minMinutes, 15, 240);
        maxMinutes = Math.Clamp(maxMinutes, minMinutes, 1440);
        var multiplier = 1L << Math.Min(Math.Max(0, idleChecks), 5);
        var minutes = Math.Min(maxMinutes, minMinutes * multiplier);
        return TimeSpan.FromMinutes(minutes * Math.Clamp(jitter, 0.9, 1.1));
    }

    public static TimeSpan RetryDelay(int failures, double jitter)
    {
        var hours = Math.Min(24, 1 << Math.Min(Math.Max(0, failures - 1), 5));
        return TimeSpan.FromHours(hours * Math.Clamp(jitter, 0.9, 1.1));
    }

    public static TimeSpan UnavailableRetryDelay(int failures) => failures switch
    {
        <= 1 => TimeSpan.FromDays(1),
        2 => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(30)
    };

    public static TimeSpan RateLimitDelay(int occurrences, double jitter)
    {
        var hours = Math.Min(24, 6 << Math.Min(Math.Max(0, occurrences - 1), 2));
        return TimeSpan.FromHours(hours * Math.Clamp(jitter, 0.9, 1.1));
    }
}
