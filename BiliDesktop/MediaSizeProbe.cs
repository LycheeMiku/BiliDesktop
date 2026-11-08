using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;

namespace BiliDesktop;

public static class MediaSizeProbe
{
    private static readonly HttpClient Client = new(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(12) };
    public static async Task<long?> GetSizeAsync(IReadOnlyList<string> urls, string userAgent, CancellationToken cancellation = default, HttpClient? client = null)
    {
        foreach (var url in urls.Take(3))
        {
            if (cancellation.IsCancellationRequested) return null;
            var size = await GetSizeAsync(url, userAgent, cancellation, client);
            if (size.HasValue) return size;
        }
        return null;
    }
    public static async Task<long?> GetSizeAsync(string url, string userAgent, CancellationToken cancellation = default, HttpClient? client = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        client ??= Client;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.Referrer = new Uri("https://www.bilibili.com/");
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!response.IsSuccessStatusCode) return null;
            return response.Content.Headers.ContentRange?.Length ?? (response.StatusCode == System.Net.HttpStatusCode.OK ? response.Content.Headers.ContentLength : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { return null; }
    }
}
