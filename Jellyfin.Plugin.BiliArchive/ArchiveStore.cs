using System.Text.Json;
using System.Text;

namespace Jellyfin.Plugin.BiliArchive;

public sealed class ArchiveRecord
{
    public string Bvid { get; set; } = string.Empty;
    public long Cid { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public string? FilePath { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ArchiveStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _folder;
    private readonly string _stateFile;
    private readonly string _cookieFile;
    private Dictionary<string, ArchiveRecord>? _records;

    public ArchiveStore() : this(Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("插件尚未初始化。"))
    {
    }

    public ArchiveStore(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(_folder);
        _stateFile = Path.Combine(_folder, "archive-state.json");
        _cookieFile = Path.Combine(_folder, "bilibili-cookie.txt");
    }

    public string? ReadCookie() => File.Exists(_cookieFile) ? File.ReadAllText(_cookieFile) : null;

    public async Task SaveCookieAsync(string cookie, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var temp = _cookieFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
            };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temp, options))
            {
                var bytes = Encoding.UTF8.GetBytes(cookie);
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            }
            File.Move(temp, _cookieFile, true);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearCookieAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { if (File.Exists(_cookieFile)) File.Delete(_cookieFile); }
        finally { _gate.Release(); }
    }

    public async Task<ArchiveRecord?> GetAsync(string bvid, long cid, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Load();
            return _records!.GetValueOrDefault(Key(bvid, cid));
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ArchiveRecord>> ListAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Load();
            return _records!.Values.OrderByDescending(x => x.UpdatedAt).Select(Clone).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(ArchiveRecord record, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Load();
            record.UpdatedAt = DateTimeOffset.UtcNow;
            _records![Key(record.Bvid, record.Cid)] = record;
            var temp = _stateFile + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(_records, JsonOptions), ct).ConfigureAwait(false);
            File.Move(temp, _stateFile, true);
        }
        finally { _gate.Release(); }
    }

    private void Load()
    {
        if (_records is not null) return;
        _records = File.Exists(_stateFile)
            ? JsonSerializer.Deserialize<Dictionary<string, ArchiveRecord>>(File.ReadAllText(_stateFile)) ?? []
            : [];
    }

    private static string Key(string bvid, long cid) => bvid + ":" + cid;
    private static ArchiveRecord Clone(ArchiveRecord x) => new()
    {
        Bvid = x.Bvid, Cid = x.Cid, Title = x.Title, Status = x.Status,
        FilePath = x.FilePath, Error = x.Error, UpdatedAt = x.UpdatedAt
    };
}
