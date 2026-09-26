using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QRCoder;

namespace Jellyfin.Plugin.BiliArchive;

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("BiliArchive")]
public sealed class BiliArchiveController : ControllerBase
{
    private readonly BiliApi _api;
    private readonly ArchiveStore _store;
    private readonly SyncStateStore _syncStore;
    private readonly ArchiveService _archive;

    public BiliArchiveController(BiliApi api, ArchiveStore store, SyncStateStore syncStore, ArchiveService archive)
    {
        _api = api;
        _store = store;
        _syncStore = syncStore;
        _archive = archive;
    }

    [HttpPost("qr")]
    public async Task<ActionResult<object>> GenerateQr(CancellationToken ct)
    {
        var qr = await _api.GenerateQrAsync(ct).ConfigureAwait(false);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(qr.Url, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(5);
        return Ok(new { key = qr.Key, svg });
    }

    [HttpPost("qr/poll")]
    public async Task<ActionResult<LoginPoll>> PollQr([FromBody] PollRequest request, CancellationToken ct)
    {
        var result = await _api.PollQrAsync(request.Key, ct).ConfigureAwait(false);
        if (result.Code == 0) await _syncStore.MarkLoginRestoredAsync(ct).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("status")]
    public async Task<ActionResult<object>> GetStatus(CancellationToken ct)
    {
        long? mid = null;
        if (!string.IsNullOrEmpty(_store.ReadCookie()))
        {
            try { mid = await _api.GetOwnMidAsync(ct).ConfigureAwait(false); }
            catch (BiliLoginException) { }
        }

        var records = await _store.ListAsync(ct).ConfigureAwait(false);
        var sync = await _syncStore.LoadAsync(ct).ConfigureAwait(false);
        var selected = Plugin.Instance?.Configuration.FolderIds ?? [];
        var nextChecks = selected.Where(sync.Folders.ContainsKey).Select(id => sync.Folders[id].NextCheckAt).ToArray();
        var now = DateTimeOffset.UtcNow;
        var activePending = sync.Pending.Values.Count(x => selected.Any(id => sync.Folders.TryGetValue(id, out var folder) && folder.KnownBvids.Contains(x.Bvid)));
        return Ok(new
        {
            version = typeof(BiliArchiveController).Assembly.GetName().Version?.ToString(),
            loggedIn = mid.HasValue,
            mid,
            running = _archive.IsRunning,
            completed = records.Count(x => x.Status == "completed"),
            failed = records.Count(x => x.Status == "failed"),
            unavailable = records.Count(x => x.Status == "unavailable"),
            pending = activePending,
            nextCheckAt = nextChecks.Length > 0 ? (nextChecks.Min() > now ? nextChecks.Min() : now) : (DateTimeOffset?)null,
            pausedUntil = sync.PausedUntil > now ? sync.PausedUntil : null,
            loginRequired = sync.LoginRequired
        });
    }

    [HttpPost("run")]
    public async Task<ActionResult<object>> RunNow(CancellationToken ct)
    {
        var started = await _archive.StartManualAsync(ct).ConfigureAwait(false);
        return started ? Accepted(new { started = true }) : Conflict(new { message = "归档任务已经在运行。" });
    }

    [HttpGet("folders")]
    public async Task<ActionResult<IReadOnlyList<FavoriteFolder>>> GetFolders(CancellationToken ct) =>
        Ok(await _api.GetFoldersAsync(ct).ConfigureAwait(false));

    [HttpGet("records")]
    public async Task<ActionResult<IReadOnlyList<ArchiveRecord>>> GetRecords(CancellationToken ct) =>
        Ok(await _store.ListAsync(ct).ConfigureAwait(false));

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await _store.ClearCookieAsync(ct).ConfigureAwait(false);
        await _syncStore.MarkLoggedOutAsync(ct).ConfigureAwait(false);
        return NoContent();
    }
}

public sealed class PollRequest
{
    public string Key { get; set; } = string.Empty;
}
