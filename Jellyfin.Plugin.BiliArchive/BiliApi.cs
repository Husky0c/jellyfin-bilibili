using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.BiliArchive;

public sealed record LoginQr(string Key, string Url);
public sealed record LoginPoll(int Code, string Message);
public sealed record FavoriteFolder(long Id, string Title, int Count);
public sealed record FavoriteVideo(string Bvid, string Title, bool Available, int PageCount);
public sealed record FavoritePage(IReadOnlyList<FavoriteVideo> Items, bool HasMore);
public sealed record FavoriteIds(IReadOnlyList<string> Bvids, int ResourceCount);
public sealed record VideoPage(int Cid, int Page, string Part);
public sealed record VideoInfo(string Bvid, string Title, string Cover, IReadOnlyList<VideoPage> Pages);
public sealed record DashStream(string Url, int Quality, long Bandwidth);
public sealed record PlayStreams(DashStream Video, DashStream Audio);

public sealed class BiliApi
{
    private static readonly HttpClient Client = CreateClient();
    private static readonly int[] MixinTable =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
        37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4,
        22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52
    ];

    private readonly ArchiveStore _store;

    public BiliApi(ArchiveStore store) => _store = store;

    public async Task<LoginQr> GenerateQrAsync(CancellationToken ct)
    {
        using var result = await GetJsonAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate", false, ct).ConfigureAwait(false);
        var data = GetData(result.RootElement);
        return new LoginQr(data.GetProperty("qrcode_key").GetString()!, data.GetProperty("url").GetString()!);
    }

    public async Task<LoginPoll> PollQrAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 128 || !key.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'))
        {
            throw new ArgumentException("无效的二维码会话。", nameof(key));
        }

        using var request = NewRequest("https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=" + Uri.EscapeDataString(key), false);
        using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var data = GetData(json.RootElement);
        var code = data.GetProperty("code").GetInt32();
        if (code == 0)
        {
            var cookies = response.Headers.TryGetValues("Set-Cookie", out var headers)
                ? ExtractCookies(headers)
                : string.Empty;
            if (!cookies.Contains("SESSDATA=", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("扫码成功，但 B 站未返回 SESSDATA Cookie。请重新扫码。");
            }

            await _store.SaveCookieAsync(cookies, ct).ConfigureAwait(false);
        }

        return new LoginPoll(code, data.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty);
    }

    public async Task<long> GetOwnMidAsync(CancellationToken ct)
    {
        using var nav = await GetJsonAsync("https://api.bilibili.com/x/web-interface/nav", true, ct).ConfigureAwait(false);
        var data = GetData(nav.RootElement);
        if (!data.GetProperty("isLogin").GetBoolean()) throw new BiliLoginException();
        return data.GetProperty("mid").GetInt64();
    }

    public async Task<IReadOnlyList<FavoriteFolder>> GetFoldersAsync(CancellationToken ct)
    {
        var mid = await GetOwnMidAsync(ct).ConfigureAwait(false);
        using var json = await GetJsonAsync($"https://api.bilibili.com/x/v3/fav/folder/created/list-all?up_mid={mid}", true, ct).ConfigureAwait(false);
        var data = GetData(json.RootElement);
        if (data.ValueKind == JsonValueKind.Null) return [];
        var list = data.GetProperty("list");
        if (list.ValueKind == JsonValueKind.Null) return [];
        return list.EnumerateArray()
            .Select(x => new FavoriteFolder(x.GetProperty("id").GetInt64(), x.GetProperty("title").GetString() ?? string.Empty, x.GetProperty("media_count").GetInt32()))
            .ToArray();
    }

    public async Task<FavoriteIds> GetFolderIdsAsync(long folderId, CancellationToken ct)
    {
        using var json = await GetJsonAsync($"https://api.bilibili.com/x/v3/fav/resource/ids?media_id={folderId}&platform=web", true, ct).ConfigureAwait(false);
        var data = GetData(json.RootElement);
        if (data.ValueKind == JsonValueKind.Null) return new FavoriteIds([], 0);
        var items = data.EnumerateArray().ToArray();
        var bvids = items.Where(x => x.TryGetProperty("type", out var type) && type.GetInt32() == 2)
            .Select(x => x.TryGetProperty("bvid", out var bv) ? bv.GetString() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new FavoriteIds(bvids, items.Length);
    }

    public async Task<FavoritePage> GetFolderVideoPageAsync(long folderId, int page, CancellationToken ct)
    {
        using var json = await GetJsonAsync($"https://api.bilibili.com/x/v3/fav/resource/list?media_id={folderId}&pn={page}&ps=20&order=mtime", true, ct).ConfigureAwait(false);
        var data = GetData(json.RootElement);
        var result = new List<FavoriteVideo>();
        if (data.TryGetProperty("medias", out var medias) && medias.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in medias.EnumerateArray())
            {
                var bvid = item.TryGetProperty("bvid", out var bv) ? bv.GetString() : null;
                if (!string.IsNullOrWhiteSpace(bvid))
                {
                    var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? bvid : bvid;
                    var available = !item.TryGetProperty("attr", out var attr) || attr.GetInt32() == 0;
                    var pageCount = item.TryGetProperty("page", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : 0;
                    result.Add(new FavoriteVideo(bvid, title, available, pageCount));
                }
            }
        }

        return new FavoritePage(result, data.GetProperty("has_more").GetBoolean());
    }

    public async IAsyncEnumerable<FavoriteVideo> GetFolderVideosAsync(long folderId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (var page = 1; ; page++)
        {
            var result = await GetFolderVideoPageAsync(folderId, page, ct).ConfigureAwait(false);
            foreach (var item in result.Items) yield return item;
            if (!result.HasMore) break;
            await Task.Delay(1200, ct).ConfigureAwait(false);
        }
    }

    public async Task<VideoInfo> GetVideoAsync(string bvid, CancellationToken ct)
    {
        using var json = await GetJsonAsync("https://api.bilibili.com/x/web-interface/view?bvid=" + Uri.EscapeDataString(bvid), true, ct).ConfigureAwait(false);
        var data = GetData(json.RootElement);
        var pages = data.GetProperty("pages").EnumerateArray()
            .Select(x => new VideoPage(x.GetProperty("cid").GetInt32(), x.GetProperty("page").GetInt32(), x.GetProperty("part").GetString() ?? string.Empty))
            .ToArray();
        return new VideoInfo(bvid, data.GetProperty("title").GetString() ?? bvid, data.GetProperty("pic").GetString() ?? string.Empty, pages);
    }

    public async Task<PlayStreams> GetPlayStreamsAsync(string bvid, int cid, int maxQuality, CancellationToken ct)
    {
        using var nav = await GetJsonAsync("https://api.bilibili.com/x/web-interface/nav", true, ct).ConfigureAwait(false);
        var wbi = GetData(nav.RootElement).GetProperty("wbi_img");
        var img = Path.GetFileNameWithoutExtension(new Uri(wbi.GetProperty("img_url").GetString()!).AbsolutePath);
        var sub = Path.GetFileNameWithoutExtension(new Uri(wbi.GetProperty("sub_url").GetString()!).AbsolutePath);
        var combined = img + sub;
        var mixin = new string(MixinTable.Select(i => combined[i]).Take(32).ToArray());
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["bvid"] = bvid,
            ["cid"] = cid.ToString(CultureInfo.InvariantCulture),
            ["fnval"] = "16",
            ["fourk"] = "1",
            ["qn"] = maxQuality.ToString(CultureInfo.InvariantCulture),
            ["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
        };
        var query = string.Join("&", parameters.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        var signature = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(query + mixin))).ToLowerInvariant();
        using var json = await GetJsonAsync("https://api.bilibili.com/x/player/wbi/playurl?" + query + "&w_rid=" + signature, true, ct).ConfigureAwait(false);
        var dash = GetData(json.RootElement).GetProperty("dash");
        var video = PickStream(dash.GetProperty("video"), maxQuality);
        var audio = PickStream(dash.GetProperty("audio"), int.MaxValue);
        return new PlayStreams(video, audio);
    }

    public async Task DownloadAsync(string url, string destination, string bvid, CancellationToken ct)
    {
        using var request = NewRequest(url, true);
        request.Headers.Referrer = new Uri("https://www.bilibili.com/video/" + bvid);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new BiliRateLimitException((int)response.StatusCode);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
        if (output.Length == 0) throw new IOException("下载返回空文件。");
    }

    private async Task<JsonDocument> GetJsonAsync(string url, bool authenticated, CancellationToken ct)
    {
        using var request = NewRequest(url, authenticated);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await Client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            throw new BiliRateLimitException((int)response.StatusCode);
        response.EnsureSuccessStatusCode();
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), cancellationToken: timeout.Token).ConfigureAwait(false);
        var code = json.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        if (code == -101) { json.Dispose(); throw new BiliLoginException(); }
        if (code is -412 or -352) { json.Dispose(); throw new BiliRateLimitException(code); }
        if (code != 0) { json.Dispose(); throw new BiliApiException(code); }
        return json;
    }

    private HttpRequestMessage NewRequest(string url, bool authenticated)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/130.0.0.0 Safari/537.36");
        request.Headers.Referrer = new Uri("https://www.bilibili.com/");
        if (authenticated)
        {
            var cookie = _store.ReadCookie();
            if (string.IsNullOrEmpty(cookie)) throw new BiliLoginException();
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        return request;
    }

    private static JsonElement GetData(JsonElement root) => root.GetProperty("data");

    private static DashStream PickStream(JsonElement streams, int maxQuality)
    {
        var options = streams.EnumerateArray().Select(x => new DashStream(
            x.TryGetProperty("baseUrl", out var u) ? u.GetString()! : x.GetProperty("base_url").GetString()!,
            x.GetProperty("id").GetInt32(),
            x.GetProperty("bandwidth").GetInt64())).ToArray();
        if (options.Length == 0) throw new InvalidOperationException("B 站没有返回可下载的 DASH 流；可能需要会员权限。");
        return options.Where(x => x.Quality <= maxQuality).DefaultIfEmpty(options.MinBy(x => x.Quality)!)
            .MaxBy(x => (x.Quality, x.Bandwidth))!;
    }

    private static string ExtractCookies(IEnumerable<string> headers)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "sid", "buvid3", "buvid4" };
        return string.Join("; ", headers.Select(x => x.Split(';', 2)[0]).Where(x => allowed.Contains(x.Split('=', 2)[0])));
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false });
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }
}

public sealed class BiliLoginException : Exception
{
    public BiliLoginException() : base("B 站登录未完成或 Cookie 已过期，请重新扫码登录。") { }
}

public sealed class BiliRateLimitException : Exception
{
    public BiliRateLimitException(int code) : base($"B 站风控或限流（{code}），本轮同步已停止。") { }
}

public sealed class BiliApiException : Exception
{
    public BiliApiException(int code) : base($"B 站 API 返回错误码 {code}。") => Code = code;

    public int Code { get; }
}
