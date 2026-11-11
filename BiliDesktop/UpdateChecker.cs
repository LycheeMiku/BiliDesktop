using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BiliDesktop;

public static class AppVersion
{
    public const string Current = "beta4";
}

public record AvailableRelease(string Version, DateTimeOffset PublishedAt, string Notes)
{
    public Uri GithubUrl => new("https://github.com/LycheeMiku/BiliDesktop/releases/tag/" + Uri.EscapeDataString(Version));
    public static Uri AlternateUrl { get; } = new("https://openlist.20070831.xyz/biliDown%20Releases");
}

public sealed class UpdateChecker
{
    private const string ApiUrl = "https://api.github.com/repos/LycheeMiku/BiliDesktop/releases?per_page=100";
    private static readonly string[] Mirrors = ["https://gh.dpik.top", "https://gh-proxy.com", "https://hk.gh-proxy.com", "https://edgeone.gh-proxy.com"];
    private static readonly HashSet<string> AllowedRedirectHosts = new(StringComparer.OrdinalIgnoreCase) { "api.github.com", "gh.dpik.top", "gh-proxy.com", "hk.gh-proxy.com", "edgeone.gh-proxy.com", "hk.gh-proxy.org", "edgeone.gh-proxy.org" };
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient client;
    private readonly TimeSpan endpointTimeout;
    public UpdateChecker() : this(SharedClient, TimeSpan.FromSeconds(8)) { }
    internal UpdateChecker(HttpClient client, TimeSpan endpointTimeout) { this.client = client; this.endpointTimeout = endpointTimeout; }
    public static bool IsMainlandTimeZone(string id) => id.Equals("China Standard Time", StringComparison.OrdinalIgnoreCase) || id.Equals("Xinjiang Standard Time", StringComparison.OrdinalIgnoreCase) || id.Equals("Asia/Shanghai", StringComparison.OrdinalIgnoreCase) || id.Equals("Asia/Chongqing", StringComparison.OrdinalIgnoreCase) || id.Equals("Asia/Chungking", StringComparison.OrdinalIgnoreCase) || id.Equals("Asia/Harbin", StringComparison.OrdinalIgnoreCase) || id.Equals("Asia/Urumqi", StringComparison.OrdinalIgnoreCase) || id.Equals("Asia/Kashgar", StringComparison.OrdinalIgnoreCase);
    internal static IReadOnlyList<Uri> Sources(string zone) => new[] { new Uri(ApiUrl) }.Concat(IsMainlandTimeZone(zone) ? Mirrors.Select(x => new Uri(x + "/" + ApiUrl)) : []).ToArray();
    public async Task<AvailableRelease?> CheckAsync(string zone, CancellationToken cancellation = default)
    {
        Exception? last = null;
        foreach (var source in Sources(zone))
        {
            cancellation.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(endpointTimeout);
            try { return ParseReleases(await ReadAsync(source, timeout.Token), AppVersion.Current); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException or FormatException or TimeoutException) { last = ex; }
        }
        throw new InvalidOperationException("更新检查失败，请检查网络连接后重试。", last);
    }
    private async Task<string> ReadAsync(Uri source, CancellationToken cancellation)
    {
        var uri = source;
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", "BiliDesktop-UpdateChecker/" + AppVersion.Current);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (response.Headers.Location is null || redirects == 3) throw new InvalidDataException("Invalid update redirect.");
                var target = new Uri(uri, response.Headers.Location);
                if (target.Scheme != Uri.UriSchemeHttps || !target.IsDefaultPort || target.UserInfo.Length > 0 || !AllowedRedirectHosts.Contains(target.IdnHost)) throw new InvalidDataException("Invalid update redirect.");
                uri = target; continue;
            }
            response.EnsureSuccessStatusCode();
            const int max = 2 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > max) throw new InvalidDataException("Update response is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellation);
            using var output = new MemoryStream(); var buffer = new byte[16384];
            while (true) { var count = await input.ReadAsync(buffer, cancellation); if (count == 0) break; if (output.Length + count > max) throw new InvalidDataException("Update response is too large."); await output.WriteAsync(buffer.AsMemory(0, count), cancellation); }
            return System.Text.Encoding.UTF8.GetString(output.ToArray());
        }
        throw new InvalidDataException("Too many update redirects.");
    }
    internal static AvailableRelease? ParseReleases(string json, string currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid release list.");
        if (!ReleaseVersion.TryParse(currentVersion, out var current)) throw new InvalidDataException("Invalid current version.");
        AvailableRelease? best = null; ReleaseVersion? bestVersion = null; var published = 0; var recognized = 0;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("draft", out var draft) || draft.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Invalid release entry.");
            if (draft.GetBoolean()) continue;
            published++;
            if (!item.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid release version.");
            var tag = tagElement.GetString() ?? "";
            if (!ReleaseVersion.TryParse(tag, out var version)) continue;
            recognized++;
            if (!item.TryGetProperty("published_at", out var dateElement) || dateElement.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(dateElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) throw new InvalidDataException("Invalid release date.");
            var notes = item.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "";
            if (version.CompareTo(current) > 0 && (bestVersion is null || version.CompareTo(bestVersion) > 0)) { best = new(tag, date, notes); bestVersion = version; }
        }
        if (published > 0 && recognized == 0) throw new InvalidDataException("Unrecognized release versions.");
        return best;
    }
    private sealed record ReleaseVersion(int[] Core, int Stage, int StageNumber) : IComparable<ReleaseVersion>
    {
        public static bool TryParse(string text, out ReleaseVersion version)
        {
            version = null!;
            if (text.Length is < 1 or > 100) return false;
            var match = Regex.Match(text.Trim(), @"^v?(?:(?<core>\d+(?:\.\d+){0,3})(?:[-._]?(?<stage>alpha|beta|preview|rc)(?:[.-]?(?<seq>\d+))?)?|(?<stage>alpha|beta|preview|rc)(?:[.-]?(?<seq>\d+))?)(?:\+[0-9a-z.-]+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return false;
            var core = new int[4]; var parts = match.Groups["core"].Value.Split('.', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++) if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out core[i])) return false;
            var stage = match.Groups["stage"].Value.ToLowerInvariant() switch { "alpha" => 0, "preview" => 0, "beta" => 1, "rc" => 2, "" => 3, _ => -1 };
            var seq = 0; if (match.Groups["seq"].Success && !int.TryParse(match.Groups["seq"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out seq)) return false;
            // Historical betaN releases use assembly version 0.N.0-betaN.
            if (parts.Length == 0 && match.Groups["stage"].Value.Equals("beta", StringComparison.OrdinalIgnoreCase)) core[1] = seq;
            version = new(core, stage, seq); return true;
        }
        public int CompareTo(ReleaseVersion? other)
        {
            if (other is null) return 1;
            for (var i = 0; i < Core.Length; i++) { var result = Core[i].CompareTo(other.Core[i]); if (result != 0) return result; }
            var stage = Stage.CompareTo(other.Stage); return stage != 0 ? stage : StageNumber.CompareTo(other.StageNumber);
        }
    }
}
