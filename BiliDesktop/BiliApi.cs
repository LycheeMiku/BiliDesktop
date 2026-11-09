using System.Net.Http;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BiliDesktop;

public record VideoPage(long Cid, string Label, string? Bvid = null, long? EpisodeId = null, long? CourseAid = null, bool Available = true, bool CheckPaidPreview = false, double? DurationSeconds = null);
public record VideoInfo(string Bvid, string Title, string Owner, string CoverUrl, IReadOnlyList<VideoPage> Pages, long? OwnerId, long? CollectionId, string? CollectionTitle = null, int? CollectionNumber = null, string? CollectionItemTitle = null);
public record CollectionVideo(string Bvid, string Title, string? CollectionTitle = null, int? CollectionNumber = null);
public record AccountInfo(long Mid, string Name);
public record FavoriteFolder(long Id, string Title, int Count)
{
    public string Label => $"{Title} · {Count}";
}
public record FavoriteContents(IReadOnlyList<CollectionVideo> Videos, int Invalid, int Unsupported);
public record VideoStream(int Quality, string Codec, long Bandwidth, string Url, IReadOnlyList<string> BackupUrls)
{
    public string Label => $"{StreamLabels.Video(Quality)} · {Codec} · {Bandwidth / 1000} kbps";
    public IReadOnlyList<string> Urls => new[] { Url }.Concat(BackupUrls).Distinct(StringComparer.Ordinal).ToArray();
}
public record AudioStream(int Quality, string Codec, long Bandwidth, string Url, IReadOnlyList<string> BackupUrls)
{
    public string Label => $"{StreamLabels.Audio(Quality)} · {Codec} · {Bandwidth / 1000} kbps";
    public IReadOnlyList<string> Urls => new[] { Url }.Concat(BackupUrls).Distinct(StringComparer.Ordinal).ToArray();
}
public record MediaStreams(IReadOnlyList<VideoStream> Video, IReadOnlyList<AudioStream> Audio, bool IsPreview = false);
public record QrLogin(string Url, string Key);
public record QrPoll(int Code, string? Cookie, DateTimeOffset? Expires = null);
public record SearchVideo(string Bvid, string Title, string Author, string Duration, long PublishUnix = 0, string Description = "", string Tags = "", string CoverUrl = "", long? Views = null, long? AuthorId = null)
{
    public string Label => $"{Title}  ·  {Author}  ·  {Duration}";
}
public record VideoSearchPage(int Page, int PageCount, IReadOnlyList<SearchVideo> Items);
public record SearchVideoDetails(string Title, string Author, string Bvid, string Description, string Category, long? PublishUnix, long? DurationSeconds, long? Views, long? Likes, long? Coins, long? Favorites, long? Replies, long? Danmaku, long? Shares, long? AuthorId = null);

public static class StreamLabels
{
    public static string Video(int quality) => quality switch
    {
        16 => "360P", 32 => "480P", 64 => "720P", 74 => "720P60", 80 => "1080P", 100 => "1080P AI", 112 => "1080P+", 116 => "1080P60", 120 => "4K", 125 => "HDR", 126 => "Dolby Vision", 127 => "8K", 129 => "HDR Vivid", _ => $"Q{quality}"
    };
    public static string Audio(int quality) => quality switch
    {
        30216 => "64K", 30232 => "132K", 30280 => "192K", 30250 => "Dolby Atmos", 30251 => "Hi-Res", _ => $"A{quality}"
    };
}

public sealed class BiliApi
{
    public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    public string UserAgent { get; private set; } = DefaultUserAgent;
    private static readonly int[] Mixin = [46,47,18,2,53,8,23,32,15,50,10,31,58,3,45,35,27,43,5,49,33,9,42,19,29,28,14,39,12,38,41,13,37,48,7,16,24,55,40,61,26,17,0,1,60,51,30,4,22,25,54,21,56,59,6,63,57,62,11,36,20,34,44,52];
    private readonly HttpClient http;
    private string cookie = "";
    private string guestBuvid = "";
    private long sessionVersion;
    public long SessionVersion => Interlocked.Read(ref sessionVersion);
    internal string RecorderCookie => guestBuvid.Length > 0 && !cookie.Split(';').Any(c => c.TrimStart().StartsWith("buvid3=", StringComparison.OrdinalIgnoreCase))
        ? (string.IsNullOrEmpty(cookie) ? "buvid3=" + guestBuvid : cookie + "; buvid3=" + guestBuvid) : cookie;

    public BiliApi() : this(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { }
    public BiliApi(HttpMessageHandler handler) => http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

    public void SetCookie(string value)
    {
        if (value.Contains('\r') || value.Contains('\n')) throw new ArgumentException("Cookie 必须为单行。");
        cookie = value.Trim();
        Interlocked.Increment(ref sessionVersion);
    }

    public void SetUserAgent(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => c < 32 || c > 126)) throw new ArgumentException("User-Agent 不能为空，且只能包含单行可打印 ASCII 字符。");
        UserAgent = value.Trim();
    }

    private async Task<JsonElement> GetAsync(string url, bool allowAnonymousNav = false, Uri? referer = null, string rootProperty = "data", CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Referrer = referer ?? new Uri("https://www.bilibili.com/");
        var requestCookie = cookie;
        if (guestBuvid.Length > 0 && !cookie.Split(';').Any(c => c.TrimStart().StartsWith("buvid3=", StringComparison.Ordinal)))
            requestCookie = string.IsNullOrEmpty(cookie) ? "buvid3=" + guestBuvid : cookie + "; buvid3=" + guestBuvid;
        if (requestCookie.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", requestCookie);
        using var response = await http.SendAsync(request, cancellation);
        if ((int)response.StatusCode == 412) throw new InvalidOperationException("请求被平台拦截（HTTP 412）；请检查网络或登录状态后重试。");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var root = doc.RootElement;
        var code = root.GetProperty("code").GetInt32();
        if (code != 0 && !(allowAnonymousNav && code == -101)) throw new InvalidOperationException($"接口错误 {code}：" + (root.TryGetProperty("message", out var msg) ? msg.ToString() : "未知错误"));
        return root.GetProperty(rootProperty).Clone();
    }

    public async Task<string> GetAccountAsync()
        => (await GetAccountInfoAsync()).Name;

    public async Task<AccountInfo> GetAccountInfoAsync()
    {
        await EnsureDeviceAsync();
        var data = await GetAsync("https://api.bilibili.com/x/web-interface/nav");
        if (!data.GetProperty("isLogin").GetBoolean()) throw new InvalidOperationException("未登录或 Cookie 已失效");
        var mid = data.GetProperty("mid").GetInt64();
        if (mid <= 0) throw new InvalidOperationException("未获取到登录账号 UID。");
        return new(mid, data.GetProperty("uname").GetString() ?? "已登录");
    }

    public async Task<IReadOnlyList<FavoriteFolder>> GetFavoriteFoldersAsync()
    {
        var account = await GetAccountInfoAsync();
        var data = await GetAsync($"https://api.bilibili.com/x/v3/fav/folder/created/list-all?up_mid={account.Mid}&type=0");
        if (data.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("收藏夹列表不可访问。");
        var count = data.TryGetProperty("count", out var countValue) && countValue.TryGetInt32(out var n) ? n : 0;
        if (!data.TryGetProperty("list", out var list) || list.ValueKind == JsonValueKind.Null)
        {
            if (count > 0) throw new InvalidOperationException("收藏夹列表不完整，请重试。");
            return [];
        }
        if (list.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("收藏夹列表格式无效。");
        var folders = list.EnumerateArray().Select(f => new FavoriteFolder(f.GetProperty("id").GetInt64(), f.GetProperty("title").GetString() ?? "", f.GetProperty("media_count").GetInt32())).ToList();
        if (folders.Count < count) throw new InvalidOperationException("收藏夹列表不完整，请重试。");
        return folders;
    }

    public async Task<IReadOnlyList<FavoriteFolder>> GetCollectedFavoriteFoldersAsync()
    {
        var session = SessionVersion;
        var account = await GetAccountInfoAsync();
        var result = new List<FavoriteFolder>(); var seen = new HashSet<long>(); var observed = 0;
        for (var page = 1; page <= 1000; page++)
        {
            if (SessionVersion != session) throw new InvalidOperationException("登录会话已变化，请重新读取收藏夹。");
            var data = await GetAsync($"https://api.bilibili.com/x/v3/fav/folder/collected/list?up_mid={account.Mid}&pn={page}&ps=20");
            if (SessionVersion != session) throw new InvalidOperationException("登录会话已变化，请重新读取收藏夹。");
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("count", out var countValue) || !countValue.TryGetInt32(out var count) || count < 0) throw new InvalidOperationException("收藏夹列表格式无效。");
            if (!data.TryGetProperty("list", out var list) || list.ValueKind == JsonValueKind.Null)
            {
                if (observed < count) throw new InvalidOperationException("收藏夹列表不完整，请重试。");
                return result;
            }
            if (list.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("收藏夹列表格式无效。");
            var received = 0;
            foreach (var item in list.EnumerateArray())
            {
                received++; observed++;
                var id = item.GetProperty("id").GetInt64();
                if (id <= 0 || !seen.Add(id)) throw new InvalidOperationException("收藏夹列表不完整，请重试。");
                if (item.TryGetProperty("state", out var state) && state.TryGetInt32(out var value) && value != 0) continue;
                result.Add(new FavoriteFolder(id, item.GetProperty("title").GetString() ?? "", item.GetProperty("media_count").GetInt32()));
            }
            if (observed >= count) return result;
            if (received == 0) throw new InvalidOperationException("收藏夹列表不完整，请重试。");
            await Task.Delay(200);
        }
        throw new InvalidOperationException("收藏夹列表不完整，请重试。");
    }

    public async Task<FavoriteContents> GetFavoriteContentsAsync(long folderId, IProgress<int>? progress = null, CancellationToken cancellation = default)
    {
        if (folderId <= 0) throw new ArgumentException("收藏夹标识无效。");
        var items = new List<CollectionVideo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var invalid = 0; var unsupported = 0; var observed = 0; int? expected = null;
        for (var page = 1; page <= 1000; page++)
        {
            cancellation.ThrowIfCancellationRequested();
            var data = await GetAsync($"https://api.bilibili.com/x/v3/fav/resource/list?media_id={folderId}&pn={page}&ps=20&order=mtime&type=0&platform=web");
            cancellation.ThrowIfCancellationRequested();
            if (data.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("收藏夹不可访问。");
            if (data.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object && info.TryGetProperty("media_count", out var count) && count.TryGetInt32(out var total)) expected = total;
            var received = 0;
            if (data.TryGetProperty("medias", out var medias) && medias.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in medias.EnumerateArray())
                {
                    received++;
                    var kind = item.GetProperty("type").GetInt32();
                    if (kind != 2) { unsupported++; continue; }
                    var attr = item.TryGetProperty("attr", out var a) && a.TryGetInt32(out var parsed) ? parsed : 0;
                    var bvid = item.TryGetProperty("bvid", out var bv) && bv.ValueKind == JsonValueKind.String ? bv.GetString() ?? "" : "";
                    if (bvid.Length == 0 && item.TryGetProperty("bv_id", out bv) && bv.ValueKind == JsonValueKind.String) bvid = bv.GetString() ?? "";
                    if (attr is 1 or 9 || !Regex.IsMatch(bvid, @"^BV[0-9A-Za-z]{10}$", RegexOptions.CultureInvariant)) { invalid++; continue; }
                    if (seen.Add(bvid)) items.Add(new(bvid, item.GetProperty("title").GetString() ?? bvid));
                }
            }
            else if (data.TryGetProperty("medias", out medias) && medias.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) throw new InvalidOperationException("收藏夹内容格式无效。");
            observed += received; progress?.Report(observed);
            var more = data.TryGetProperty("has_more", out var hasMore) && hasMore.ValueKind == JsonValueKind.True;
            if (!more)
            {
                if (expected.HasValue && observed < expected.Value) throw new InvalidOperationException("收藏夹返回的条目数不足，无法确认完整列表，请重试。");
                return new(items, invalid, unsupported);
            }
            if (received == 0) throw new InvalidOperationException("收藏夹分页提前结束，无法确认完整列表。");
            await Task.Delay(200, cancellation);
        }
        throw new InvalidOperationException("收藏夹超过当前分页上限，未读取完整列表。");
    }

    public async Task<QrLogin> CreateQrLoginAsync()
    {
        var data = await GetAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate");
        return new(data.GetProperty("url").GetString()!, data.GetProperty("qrcode_key").GetString()!);
    }

    public async Task<QrPoll> PollQrLoginAsync(string key, CancellationToken cancellationToken)
    {
        var address = new Uri("https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=" + Uri.EscapeDataString(key));
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await http.SendAsync(request, cancellationToken);
        if ((int)response.StatusCode == 412) throw new InvalidOperationException("请求被平台拦截（HTTP 412）。");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (json.RootElement.GetProperty("code").GetInt32() != 0) throw new InvalidOperationException("扫码登录接口返回错误。");
        var code = json.RootElement.GetProperty("data").GetProperty("code").GetInt32();
        if (code != 0) return new(code, null);
        var jar = new CookieContainer();
        if (response.Headers.TryGetValues("Set-Cookie", out var headers))
            foreach (var header in headers) jar.SetCookies(address, header);
        var cookies = jar.GetCookieHeader(new Uri("https://www.bilibili.com/"));
        if (!cookies.Split(';').Any(c => c.TrimStart().StartsWith("SESSDATA=", StringComparison.Ordinal))) throw new InvalidOperationException("登录成功，但未收到有效的登录 Cookie。");
        var sessionCookie = jar.GetCookies(new Uri("https://www.bilibili.com/"))["SESSDATA"];
        DateTimeOffset? expires = sessionCookie is not null && sessionCookie.Expires != DateTime.MinValue ? new DateTimeOffset(sessionCookie.Expires.ToUniversalTime()) : null;
        return new(code, cookies, expires);
    }

    public static string ExtractBvid(string input)
    {
        var match = Regex.Match(input, @"(?<![A-Za-z0-9])BV[0-9A-Za-z]{10}(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) throw new ArgumentException("请输入包含 BV 号的视频链接或 BV 号。");
        return "BV" + match.Value[2..];
    }

    public async Task<string> ResolveVideoLinkAsync(string input)
    {
        input = input.Trim();
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || !uri.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase)) return input;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 5; i++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || !uri.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("短链接跳转地址不受支持。");
            if (!visited.Add(uri.AbsoluteUri)) throw new InvalidOperationException("短链接跳转循环。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is null) throw new InvalidOperationException("短链接没有返回受支持的跳转地址。");
            var next = new Uri(uri, response.Headers.Location);
            if (next.Scheme != Uri.UriSchemeHttps || !next.IsDefaultPort || next.UserInfo.Length != 0) throw new InvalidOperationException("短链接跳转地址不受支持。");
            if (next.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase)) { uri = next; continue; }
            if (!next.Host.Equals("www.bilibili.com", StringComparison.OrdinalIgnoreCase) && !next.Host.Equals("m.bilibili.com", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("短链接跳转地址不受支持。");
            if (next.Host.Equals("m.bilibili.com", StringComparison.OrdinalIgnoreCase)) next = new UriBuilder(next) { Host = "www.bilibili.com" }.Uri;
            return next.AbsoluteUri;
        }
        throw new InvalidOperationException("短链接跳转次数过多。");
    }

    public static bool TryParseAvid(string input, out long aid)
    {
        aid = 0; input = input.Trim();
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("www.bilibili.com", StringComparison.OrdinalIgnoreCase)) return false;
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !parts[0].Equals("video", StringComparison.OrdinalIgnoreCase)) return false;
            input = parts[1];
        }
        var match = Regex.Match(input, @"^av([0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && long.TryParse(match.Groups[1].Value, out aid) && aid > 0;
    }

    public static int RequestedPage(string input, int pageCount)
    {
        if (pageCount <= 0 || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.Equals("www.bilibili.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("m.bilibili.com", StringComparison.OrdinalIgnoreCase))) return 0;
        var values = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.StartsWith("p=", StringComparison.Ordinal)).ToArray();
        if (values.Length != 1 || !int.TryParse(values[0].AsSpan(2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var page) || page < 1 || page > pageCount) return 0;
        return page - 1;
    }

    public static bool TryParseCollectionLink(string input, out long mid, out long collectionId)
    {
        mid = 0; collectionId = 0;
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("space.bilibili.com", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !long.TryParse(parts[0], out mid) || mid <= 0 || parts[1] != "channel" || parts[2] != "collectiondetail") return false;
        var ids = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(x => x.StartsWith("sid=", StringComparison.Ordinal)).ToArray();
        return ids.Length == 1 && long.TryParse(ids[0][4..], out collectionId) && collectionId > 0;
    }

    public static bool TryParseUploader(string input, out long mid)
    {
        mid = 0; input = input.Trim();
        if (long.TryParse(input, out mid)) return mid > 0;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("space.bilibili.com", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 1 && long.TryParse(parts[0], out mid) && mid > 0;
    }

    public static bool TryParsePgc(string input, out string kind, out long id)
    {
        kind = ""; id = 0; input = input.Trim();
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("www.bilibili.com", StringComparison.OrdinalIgnoreCase)) return false;
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || parts[0] != "bangumi" || parts[1] != "play") return false;
            input = parts[2];
        }
        var match = Regex.Match(input, @"^(ss|ep)([0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !long.TryParse(match.Groups[2].Value, out id) || id <= 0) return false;
        kind = match.Groups[1].Value.ToLowerInvariant(); return true;
    }

    public static bool TryParseCourse(string input, out string kind, out long id)
    {
        kind = ""; id = 0;
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || !uri.Host.Equals("www.bilibili.com", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "cheese" || parts[1] != "play") return false;
        var match = Regex.Match(parts[2], @"^(ss|ep)([0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !long.TryParse(match.Groups[2].Value, out id) || id <= 0) return false;
        kind = match.Groups[1].Value.ToLowerInvariant(); return true;
    }

    public async Task<VideoInfo> GetCourseAsync(string input)
    {
        if (!TryParseCourse(input, out var kind, out var id)) throw new ArgumentException("请输入有效的课堂课程链接。");
        await EnsureDeviceAsync();
        var key = kind == "ss" ? "season_id" : "ep_id";
        var data = await GetAsync($"https://api.bilibili.com/pugv/view/web/season?{key}={id}");
        var seasonId = data.GetProperty("season_id").GetInt64();
        if (seasonId <= 0) throw new InvalidOperationException("课程标识无效。");
        var pages = new List<VideoPage>(); var seen = new HashSet<long>();
        void AddEpisodes(JsonElement items)
        {
            if (items.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("课程分集列表格式无效。");
            foreach (var episode in items.EnumerateArray())
            {
                static long number(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed) ? parsed : 0;
                var eid = number(episode, "id"); var cid = number(episode, "cid"); var aid = number(episode, "aid");
                if (eid <= 0 || !seen.Add(eid)) throw new InvalidOperationException("课程分集列表不完整，请重试。");
                var available = number(episode, "status") == 1 && cid > 0 && aid > 0;
                var title = episode.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String ? titleValue.GetString() ?? "" : "";
                pages.Add(new VideoPage(cid, $"{(available ? "" : "🔒 ")}{title}", null, eid, aid, available));
            }
        }
        if (!data.TryGetProperty("episodes", out var initial)) throw new InvalidOperationException("课程分集列表格式无效。");
        var pageInfo = data.TryGetProperty("episode_page", out var info) && info.ValueKind == JsonValueKind.Object ? info : default;
        var paginated = pageInfo.ValueKind == JsonValueKind.Object && pageInfo.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.True;
        if (!paginated)
        {
            AddEpisodes(initial);
            if (pageInfo.ValueKind == JsonValueKind.Object && pageInfo.TryGetProperty("total", out var total) && total.TryGetInt32(out var expected) && pages.Count < expected) throw new InvalidOperationException("课程分集列表不完整，请重试。");
        }
        else
        {
            for (var page = 1; page <= 1000; page++)
            {
                var response = await GetAsync($"https://api.bilibili.com/pugv/view/web/ep/list?season_id={seasonId}&pn={page}&ps=50");
                var before = pages.Count; AddEpisodes(response.GetProperty("items"));
                var meta = response.GetProperty("page");
                if (!meta.GetProperty("next").GetBoolean())
                {
                    if (meta.TryGetProperty("total", out var total) && total.TryGetInt32(out var expected) && pages.Count < expected) throw new InvalidOperationException("课程分集列表不完整，请重试。");
                    break;
                }
                if (pages.Count == before || page == 1000) throw new InvalidOperationException("课程分集列表不完整，请重试。");
                await Task.Delay(200);
            }
        }
        if (pages.Count == 0) throw new InvalidOperationException("课程没有可解析的分集。");
        var title = data.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
        var cover = data.TryGetProperty("cover", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
        var owner = data.TryGetProperty("up_info", out var up) && up.ValueKind == JsonValueKind.Object && up.TryGetProperty("uname", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
        return new VideoInfo($"course-ss{seasonId}", title, owner, cover, pages, null, null);
    }

    public async Task<VideoInfo> GetPgcAsync(string input)
    {
        if (!TryParsePgc(input, out var kind, out var id)) throw new ArgumentException("请输入有效的番剧 ss、ep 或链接。");
        await EnsureDeviceAsync();
        var key = kind == "ss" ? "season_id" : "ep_id";
        var data = await GetAsync($"https://api.bilibili.com/pgc/view/web/season?{key}={id}", rootProperty: "result");
        var pages = new List<VideoPage>(); var seen = new HashSet<long>(); var unavailable = 0;
        void AddEpisodes(JsonElement episodes, string prefix)
        {
            if (episodes.ValueKind != JsonValueKind.Array) return;
            foreach (var episode in episodes.EnumerateArray())
            {
                if (!episode.TryGetProperty("id", out var eidValue) || !eidValue.TryGetInt64(out var eid) || eid <= 0 || !seen.Add(eid)) continue;
                if (!episode.TryGetProperty("cid", out var cidValue) || !cidValue.TryGetInt64(out var cid) || cid <= 0 || !episode.TryGetProperty("bvid", out var bvValue) || bvValue.ValueKind != JsonValueKind.String) { unavailable++; continue; }
                var bvid = bvValue.GetString() ?? "";
                if (!Regex.IsMatch(bvid, @"^BV[0-9A-Za-z]{10}$", RegexOptions.CultureInvariant)) { unavailable++; continue; }
                string text(string name) => episode.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                pages.Add(new(cid, $"{prefix}{text("title")} {text("long_title")}".Trim(), bvid, eid));
            }
        }
        if (data.TryGetProperty("episodes", out var main)) AddEpisodes(main, "");
        if (data.TryGetProperty("section", out var sections) && sections.ValueKind == JsonValueKind.Array)
            foreach (var section in sections.EnumerateArray()) if (section.TryGetProperty("episodes", out var extras)) AddEpisodes(extras, section.TryGetProperty("title", out var t) ? (t.GetString() ?? "") + " · " : "");
        if (pages.Count == 0) throw new InvalidOperationException("没有可解析的剧集；内容可能尚未开放或不可访问。");
        var title = data.TryGetProperty("title", out var titleValue) ? titleValue.GetString() ?? "番剧" : "番剧";
        if (unavailable > 0) title += $"（{unavailable} 集不可解析）";
        var cover = data.TryGetProperty("cover", out var coverValue) ? coverValue.GetString() ?? "" : "";
        return new VideoInfo(pages[0].Bvid!, title, "番剧", cover, pages, null, null);
    }

    public async Task<VideoInfo> GetVideoAsync(string input)
    {
        var isAvid = TryParseAvid(input, out var aid);
        var bvid = isAvid ? null : ExtractBvid(input);
        await EnsureDeviceAsync();
        var query = isAvid ? $"aid={aid}" : $"bvid={Uri.EscapeDataString(bvid!)}";
        var data = await GetAsync($"https://api.bilibili.com/x/web-interface/view?{query}");
        if (isAvid)
        {
            bvid = data.TryGetProperty("bvid", out var bvValue) && bvValue.ValueKind == JsonValueKind.String ? bvValue.GetString() : null;
            if (bvid is null || !Regex.IsMatch(bvid, @"^BV[0-9A-Za-z]{10}$", RegexOptions.CultureInvariant)) throw new InvalidOperationException("接口未返回有效的 BV 号。");
        }
        var paid = data.TryGetProperty("rights", out var rights) && rights.ValueKind == JsonValueKind.Object && rights.TryGetProperty("ugc_pay", out var payment) && (payment.ValueKind == JsonValueKind.True || payment.ValueKind == JsonValueKind.Number && payment.TryGetInt32(out var paidFlag) && paidFlag != 0);
        var pages = data.GetProperty("pages").EnumerateArray().Select(p => new VideoPage(p.GetProperty("cid").GetInt64(), $"P{p.GetProperty("page").GetInt32()} · {p.GetProperty("part").GetString()}", CheckPaidPreview: paid, DurationSeconds: p.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds > 0 ? seconds : null)).ToList();
        var owner = data.GetProperty("owner");
        long? mid = owner.TryGetProperty("mid", out var midValue) && midValue.TryGetInt64(out var parsedMid) ? parsedMid : null;
        long? seasonId = data.TryGetProperty("ugc_season", out var season) && season.ValueKind == JsonValueKind.Object && season.TryGetProperty("id", out var id) && id.TryGetInt64(out var parsedId) ? parsedId : null;
        string? collectionTitle = null; string? collectionItemTitle = null; int? collectionNumber = null;
        if (season.ValueKind == JsonValueKind.Object)
        {
            if (season.TryGetProperty("title", out var st) && st.ValueKind == JsonValueKind.String) collectionTitle = st.GetString();
            if (season.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
            {
                var position = 0;
                foreach (var section in sections.EnumerateArray())
                {
                    if (section.ValueKind != JsonValueKind.Object || !section.TryGetProperty("episodes", out var episodes) || episodes.ValueKind != JsonValueKind.Array) continue;
                    foreach (var episode in episodes.EnumerateArray())
                    {
                        position++;
                        if (episode.ValueKind != JsonValueKind.Object || !episode.TryGetProperty("bvid", out var epBvid) || epBvid.ValueKind != JsonValueKind.String || !string.Equals(epBvid.GetString(), bvid, StringComparison.OrdinalIgnoreCase)) continue;
                        collectionNumber = position;
                        if (episode.TryGetProperty("title", out var epTitle) && epTitle.ValueKind == JsonValueKind.String) collectionItemTitle = epTitle.GetString();
                    }
                }
            }
        }
        return new VideoInfo(bvid!, data.GetProperty("title").GetString() ?? "", owner.GetProperty("name").GetString() ?? "", data.TryGetProperty("pic", out var pic) ? pic.GetString() ?? "" : "", pages, mid, seasonId, collectionTitle, collectionNumber, collectionItemTitle);
    }

    public async Task<IReadOnlyList<CollectionVideo>> GetCollectionAsync(long mid, long seasonId, IProgress<int>? progress = null)
    {
        if (mid <= 0 || seasonId <= 0) throw new ArgumentException("合集标识无效。");
        await EnsureDeviceAsync();
        var items = new List<CollectionVideo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var observed = 0; string? collectionTitle = null;
        for (var page = 1; page <= 1000; page++)
        {
            var query = await SignQueryAsync(new Dictionary<string, string> { ["mid"] = mid.ToString(), ["season_id"] = seasonId.ToString(), ["page_num"] = page.ToString(), ["page_size"] = "30" });
            var data = await GetAsync("https://api.bilibili.com/x/polymer/web-space/seasons_archives_list?" + query);
            if (data.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                var rawTitle = name.GetString();
                collectionTitle ??= rawTitle?.StartsWith("合集·", StringComparison.Ordinal) == true ? rawTitle[3..].TrimStart() : rawTitle;
            }
            var pageInfo = data.GetProperty("page");
            var total = pageInfo.GetProperty("total").GetInt32();
            var archives = data.GetProperty("archives");
            if (archives.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("合集列表格式无效。");
            var received = 0;
            foreach (var item in archives.EnumerateArray())
            {
                received++;
                var id = item.GetProperty("bvid").GetString() ?? "";
                if (!Regex.IsMatch(id, @"^BV[0-9A-Za-z]{10}$", RegexOptions.CultureInvariant)) throw new InvalidOperationException("合集包含无效视频标识。");
                if (seen.Add(id)) items.Add(new(id, item.GetProperty("title").GetString() ?? id, collectionTitle, items.Count + 1));
            }
            observed += received;
            progress?.Report(items.Count);
            if ((long)page * 30 >= total)
            {
                if (observed < total) throw new InvalidOperationException("合集返回的条目数不足，请重试。");
                return items;
            }
            if (received == 0) throw new InvalidOperationException("合集分页提前结束，请重试。");
        }
        throw new InvalidOperationException("合集超过当前分页上限，未读取完整列表。");
    }

    public async Task<IReadOnlyList<CollectionVideo>> GetUploaderVideosAsync(long mid, IProgress<int>? progress = null, CancellationToken cancellation = default)
    {
        if (mid <= 0) throw new ArgumentException("UP 主 UID 无效。");
        var session = SessionVersion;
        await EnsureDeviceAsync();
        if (SessionVersion != session) throw new InvalidOperationException("登录会话已变化，请重新查询。");
        var items = new List<CollectionVideo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var observed = 0;
        for (var page = 1; page <= 1000; page++)
        {
            cancellation.ThrowIfCancellationRequested();
            var query = await SignQueryAsync(new Dictionary<string, string> { ["mid"] = mid.ToString(), ["pn"] = page.ToString(), ["ps"] = "30", ["order"] = "pubdate", ["tid"] = "0" });
            var data = await GetAsync("https://api.bilibili.com/x/space/wbi/arc/search?" + query, referer: new Uri($"https://space.bilibili.com/{mid}/"), cancellation: cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (SessionVersion != session) throw new InvalidOperationException("登录会话已变化，请重新查询。");
            var total = data.GetProperty("page").GetProperty("count").GetInt32();
            var list = data.GetProperty("list").GetProperty("vlist");
            if (list.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("UP 主投稿列表格式无效。");
            var received = 0;
            foreach (var item in list.EnumerateArray())
            {
                received++;
                var id = item.GetProperty("bvid").GetString() ?? "";
                if (!Regex.IsMatch(id, @"^BV[0-9A-Za-z]{10}$", RegexOptions.CultureInvariant)) throw new InvalidOperationException("投稿列表包含无效视频标识。");
                if (seen.Add(id)) items.Add(new(id, item.GetProperty("title").GetString() ?? id));
            }
            observed += received;
            progress?.Report(items.Count);
            if ((long)page * 30 >= total)
            {
                if (observed < total || items.Count < total) throw new InvalidOperationException("投稿列表有缺失或重复，无法确认已读取全部视频，请稍后重试。");
                return items;
            }
            if (received == 0) throw new InvalidOperationException("投稿分页提前结束，无法确认完整列表。");
            await Task.Delay(200, cancellation);
        }
        throw new InvalidOperationException("投稿数量超过当前分页上限，未读取完整列表。");
    }

    private async Task<string> SignQueryAsync(IDictionary<string, string> values)
    {
        var nav = await GetAsync("https://api.bilibili.com/x/web-interface/nav", allowAnonymousNav: true);
        var wbi = nav.GetProperty("wbi_img");
        string stem(string name) => Path.GetFileNameWithoutExtension(new Uri(wbi.GetProperty(name).GetString()!).AbsolutePath);
        var raw = stem("img_url") + stem("sub_url");
        if (raw.Length < 64) throw new InvalidOperationException("WBI 密钥无效");
        var key = new string(Mixin.Select(i => raw[i]).Take(32).ToArray());
        return SignWbi(values, key, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public static string SignWbi(IDictionary<string, string> values, string key, long timestamp)
    {
        var parameters = new SortedDictionary<string, string>(values, StringComparer.Ordinal) { ["wts"] = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var query = string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(Regex.Replace(p.Value, "[!'()*]", ""))}"));
        var signature = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(query + key)));
        return $"{query}&w_rid={signature}";
    }

    private async Task EnsureDeviceAsync()
    {
        if (guestBuvid.Length != 0 || cookie.Split(';').Any(c => c.TrimStart().StartsWith("buvid3=", StringComparison.Ordinal))) return;
        var fingerprint = await GetAsync("https://api.bilibili.com/x/frontend/finger/spi");
        var candidate = fingerprint.GetProperty("b_3").GetString() ?? "";
        if (candidate.Length == 0 || candidate.Any(c => c < 33 || c > 126 || c is ';' or ',')) throw new InvalidOperationException("平台返回的设备标识无效。");
        guestBuvid = candidate;
    }

    public async Task<VideoSearchPage> SearchVideosAsync(string keyword, int page, string order)
    {
        keyword = keyword.Trim();
        if (keyword.Length is < 1 or > 200) throw new ArgumentException("请输入 1 至 200 个字符的关键词。");
        if (page is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(page));
        if (order is not ("totalrank" or "pubdate" or "click" or "stow")) throw new ArgumentException("排序方式无效。");
        await EnsureDeviceAsync();
        var query = await SignQueryAsync(new Dictionary<string, string> { ["search_type"] = "video", ["keyword"] = keyword, ["page"] = page.ToString(), ["order"] = order });
        var data = await GetAsync("https://api.bilibili.com/x/web-interface/wbi/search/type?" + query);
        var pages = data.TryGetProperty("numPages", out var count) && count.TryGetInt32(out var n) ? Math.Clamp(n, 0, 50) : 0;
        var items = new List<SearchVideo>();
        if (data.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in result.EnumerateArray())
            {
                if (!item.TryGetProperty("bvid", out var bv)) continue;
                var id = bv.GetString() ?? "";
                if (!Regex.IsMatch(id, @"^BV[0-9A-Za-z]{10}$", RegexOptions.CultureInvariant)) continue;
                string field(string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                var title = WebUtility.HtmlDecode(Regex.Replace(field("title"), @"</?em(?:\s+[^>]*)?>", "", RegexOptions.IgnoreCase));
                var published = item.TryGetProperty("pubdate", out var pubdate) && pubdate.TryGetInt64(out var timestamp) ? timestamp : 0;
                items.Add(new(id, title, field("author"), field("duration"), published, WebUtility.HtmlDecode(field("description")), field("tag"), field("pic"), NonNegativeNumber(item, "play"), NonNegativeNumber(item, "mid")));
            }
        }
        return new(page, pages, items);
    }

    private static long? NonNegativeNumber(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value)) return null;
        long n;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out n) && n >= 0) return n;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n) && n >= 0) return n;
        return null;
    }
    public async Task<SearchVideoDetails> GetSearchVideoDetailsAsync(string input, CancellationToken cancellation = default)
    {
        var bvid = ExtractBvid(input);
        await EnsureDeviceAsync(); cancellation.ThrowIfCancellationRequested();
        var data = await GetAsync($"https://api.bilibili.com/x/web-interface/view?bvid={Uri.EscapeDataString(bvid)}", cancellation: cancellation);
        string Text(JsonElement obj, string name) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var owner = data.TryGetProperty("owner", out var o) ? o : default;
        var stat = data.TryGetProperty("stat", out var s) ? s : default;
        return new(Text(data, "title"), Text(owner, "name"), bvid, Text(data, "desc"), Text(data, "tname"), NonNegativeNumber(data, "pubdate"), NonNegativeNumber(data, "duration"), NonNegativeNumber(stat, "view"), NonNegativeNumber(stat, "like"), NonNegativeNumber(stat, "coin"), NonNegativeNumber(stat, "favorite"), NonNegativeNumber(stat, "reply"), NonNegativeNumber(stat, "danmaku"), NonNegativeNumber(stat, "share"), NonNegativeNumber(owner, "mid"));
    }
    public async Task<byte[]> GetCoverPreviewAsync(string input, CancellationToken cancellation = default)
    {
        var url = input.StartsWith("//", StringComparison.Ordinal) ? "https:" + input : input;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Port is not (80 or 443) || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("封面地址无效。");
        var host = uri.IdnHost;
        if (!(host.Equals("hdslb.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".hdslb.com", StringComparison.OrdinalIgnoreCase) || host.Equals("biliimg.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".biliimg.com", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("封面地址无效。");
        if (uri.Scheme == "http") uri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent); request.Headers.Referrer = new Uri("https://www.bilibili.com/");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        const int maximum = 2 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("封面文件过大。");
        await using var source = await response.Content.ReadAsStreamAsync(cancellation);
        using var output = new MemoryStream(); var buffer = new byte[16384];
        while (true) { var read = await source.ReadAsync(buffer, cancellation); if (read == 0) break; if (output.Length + read > maximum) throw new InvalidDataException("封面文件过大。"); output.Write(buffer, 0, read); }
        return output.ToArray();
    }

    public async Task<MediaStreams> GetStreamsForPageAsync(string bvid, VideoPage page)
    {
        if (!page.Available) throw new InvalidOperationException("当前账号无权观看该课程分集。");
        var actualBvid = page.Bvid ?? bvid;
        var streams = await GetStreamsAsync(actualBvid, page.Cid, page.EpisodeId, page.CourseAid);
        if (!page.CheckPaidPreview || streams.IsPreview) return streams;
        var query = await SignQueryAsync(new Dictionary<string, string> { ["bvid"] = actualBvid, ["cid"] = page.Cid.ToString() });
        var player = await GetAsync("https://api.bilibili.com/x/player/wbi/v2?" + query);
        if (!player.TryGetProperty("is_ugc_pay_preview", out var preview) || preview.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException("无法确认付费视频是否为试看，请稍后重试。");
        return streams with { IsPreview = preview.GetBoolean() };
    }

    public async Task<MediaStreams> GetStreamsAsync(string bvid, long cid, long? episodeId = null, long? courseAid = null)
    {
        JsonElement data;
        if (courseAid.HasValue)
        {
            if (courseAid <= 0 || !episodeId.HasValue || episodeId <= 0 || cid <= 0) throw new ArgumentException("课程分集标识无效。");
            data = await GetAsync($"https://api.bilibili.com/pugv/player/web/playurl?avid={courseAid.Value}&ep_id={episodeId.Value}&cid={cid}&fnval=4048&fourk=1&qn=127");
        }
        else if (episodeId.HasValue)
            data = await GetAsync($"https://api.bilibili.com/pgc/player/web/playurl?ep_id={episodeId.Value}&cid={cid}&fnval=4048&fourk=1&qn=127", rootProperty: "result");
        else
        {
            var query = await SignQueryAsync(new Dictionary<string, string> { ["bvid"] = bvid, ["cid"] = cid.ToString(), ["fnval"] = "4048", ["fourk"] = "1", ["qn"] = "127" });
            data = await GetAsync($"https://api.bilibili.com/x/player/wbi/playurl?{query}");
        }
        static bool flag(JsonElement root, string name) => root.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number != 0);
        if (flag(data, "is_drm")) throw new InvalidOperationException("该内容使用当前版本不支持的 DRM 保护。");
        var preview = flag(data, "is_preview");
        if (!data.TryGetProperty("dash", out var dash) || dash.ValueKind != JsonValueKind.Object)
        {
            if (courseAid.HasValue && data.TryGetProperty("durl", out var segments) && segments.ValueKind == JsonValueKind.Array && segments.GetArrayLength() > 0) throw new InvalidOperationException("当前课程返回的分段流格式暂不支持。");
            return new([], [], preview);
        }
        static string baseUrl(JsonElement item)
        {
            foreach (var name in new[] { "baseUrl", "base_url" })
                if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!;
            throw new InvalidOperationException("媒体流缺少下载地址。");
        }
        static IReadOnlyList<string> backups(JsonElement item)
        {
            foreach (var name in new[] { "backupUrl", "backup_url" })
                if (item.TryGetProperty(name, out var urls) && urls.ValueKind == JsonValueKind.Array) return urls.EnumerateArray().Where(u => u.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(u.GetString())).Select(u => u.GetString()!).ToArray();
            return [];
        }
        var videos = new List<VideoStream>();
        if (dash.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Array)
            videos.AddRange(video.EnumerateArray().Select(v => new VideoStream(v.GetProperty("id").GetInt32(), v.GetProperty("codecs").GetString() ?? "", v.GetProperty("bandwidth").GetInt64(), baseUrl(v), backups(v))));
        var audio = new List<AudioStream>();
        static AudioStream audioStream(JsonElement a) => new(a.GetProperty("id").GetInt32(), a.GetProperty("codecs").GetString() ?? "", a.GetProperty("bandwidth").GetInt64(), baseUrl(a), backups(a));
        if (dash.TryGetProperty("audio", out var audios) && audios.ValueKind == JsonValueKind.Array)
            audio.AddRange(audios.EnumerateArray().Select(audioStream));
        if (dash.TryGetProperty("dolby", out var dolby) && dolby.ValueKind == JsonValueKind.Object && dolby.TryGetProperty("audio", out var dolbyAudio) && dolbyAudio.ValueKind == JsonValueKind.Array)
            audio.AddRange(dolbyAudio.EnumerateArray().Select(audioStream));
        if (dash.TryGetProperty("flac", out var flac) && flac.ValueKind == JsonValueKind.Object && flac.TryGetProperty("audio", out var flacAudio) && flacAudio.ValueKind == JsonValueKind.Object)
            audio.Add(audioStream(flacAudio));
        return new(videos.OrderByDescending(v => v.Quality).ThenByDescending(v => v.Bandwidth).ToList(), audio.DistinctBy(a => (a.Quality, a.Codec, a.Url)).OrderByDescending(a => a.Bandwidth).ToList(), preview);
    }
}
