using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;

namespace BiliDesktop;

public static class KeywordPolicy
{
    public static bool Matches(SearchVideo item, KeywordRule rule)
    {
        bool Contains(string term) => item.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
            || item.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
            || item.Tags.Contains(term, StringComparison.OrdinalIgnoreCase);
        return rule.MatchMode == KeywordMatchMode.All ? rule.Keywords.All(Contains) : rule.Keywords.Any(Contains);
    }
    public static string Folder(KeywordRule rule) => Path.Combine(rule.Directory, DownloadNaming.SafeComponent($"关键词-规则{rule.Id}-{rule.Keywords[0]}"));
    public static string BaseName(VideoInfo video, VideoPage part, int index, VideoStream stream)
    {
        var title = DownloadNaming.SafeComponent(video.Title);
        if (DownloadNaming.NeedsFolder(video)) title += $"-{index:D3}-{DownloadNaming.SafeComponent(part.Label)}";
        var codec = DownloadNaming.SafeComponent(stream.Codec);
        if (codec.Length > 28) codec = codec[..28];
        var suffix = $"-{StreamLabels.Video(stream.Quality)}-{codec}-CID{part.Cid}";
        var max = Math.Max(1, 100 - suffix.Length);
        if (title.Length > max) title = title[..max].TrimEnd(' ', '.');
        return DownloadNaming.SafeComponent(title + suffix);
    }
}

public sealed class KeywordEngine(BackupStore store, IKeywordGateway gateway, Func<TimeSpan, CancellationToken, Task>? rateWait = null)
{
    private readonly SemaphoreSlim runLock = new(1, 1);
    private long lastSearchTick;
    private void Update(long id, string status, long? next = null) => store.SetKeywordStatus(id, status, next);
    public async Task RunDueAsync(CancellationToken cancellation = default)
    {
        if (!await runLock.WaitAsync(0, cancellation)) return;
        try
        {
            foreach (var rule in store.ListKeywords().Where(r => r.Enabled && r.NextRun <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            {
                cancellation.ThrowIfCancellationRequested();
                if (store.KeywordEnabled(rule.Id)) await RunRuleAsync(rule, cancellation);
            }
        }
        finally { runLock.Release(); }
    }
    private async Task<bool> WaitForSearchAsync(KeywordRule rule, long session, CancellationToken cancellation)
    {
        if (lastSearchTick != 0)
        {
            var remaining = TimeSpan.FromMilliseconds(rule.QueryDelayMs) - Stopwatch.GetElapsedTime(lastSearchTick);
            if (remaining > TimeSpan.Zero) await (rateWait ?? Task.Delay)(remaining, cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
        if (!store.KeywordEnabled(rule.Id)) return false;
        if (gateway.SessionVersion != session) throw new BackupSessionChangedException();
        lastSearchTick = Stopwatch.GetTimestamp();
        return true;
    }
    private static bool IsRateLimited(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.PreconditionFailed }
        || ex.Message.Contains("HTTP 412", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("接口错误 -352", StringComparison.OrdinalIgnoreCase);
    private static long RetryAt(KeywordRule rule, bool rateLimited = false) => DateTimeOffset.UtcNow
        .AddSeconds(Math.Max(rateLimited ? 300 : 60, Math.Min(rule.EffectiveIntervalSeconds, 900))).ToUnixTimeSeconds();
    private async Task RunRuleAsync(KeywordRule rule, CancellationToken cancellation)
    {
        var pending = new List<Task>();
        var completed = 0; var failed = 0; var existing = 0; string? firstError = null;
        try
        {
            if (!Directory.Exists(rule.Directory)) throw new DirectoryNotFoundException("下载目录不存在。");
            var session = gateway.SessionVersion;
            var seen = new HashSet<string>(StringComparer.Ordinal); var capped = false; var missingDates = 0;
            bool CanStart() => store.KeywordEnabled(rule.Id) && gateway.SessionVersion == session;
            void CheckSession()
            {
                cancellation.ThrowIfCancellationRequested();
                if (gateway.SessionVersion != session) throw new BackupSessionChangedException();
            }
            void RecordFailure(Exception ex) { Interlocked.Increment(ref failed); Interlocked.CompareExchange(ref firstError, ex.Message, null); }
            async Task DownloadPartAsync(VideoInfo video, VideoPage part, int number, string legacyMp4, string legacyMkv)
            {
                try
                {
                    var title = video.Pages.Count > 1 ? $"{video.Title} · P{number} {part.Label}" : video.Title;
                    await gateway.InKeywordDownloadSlotAsync(async () =>
                    {
                        if (store.KeywordCompleted(rule.Id, video.Bvid, part.Cid) || File.Exists(legacyMp4) || File.Exists(legacyMkv)) { Interlocked.Increment(ref existing); return; }
                        var streams = await gateway.StreamsAsync(video.Bvid, part);
                        if (streams.IsPreview) throw new InvalidOperationException("当前账号只能获取预览，未作为完整视频下载。");
                        var selectedVideo = BatchPolicy.ChooseVideo(streams.Video, null) ?? throw new InvalidOperationException("没有可用视频流。");
                        var selectedAudio = BatchPolicy.ChooseAudio(streams.Audio, null) ?? throw new InvalidOperationException("没有可用音频流。");
                        var directory = KeywordPolicy.Folder(rule);
                        var basename = KeywordPolicy.BaseName(video, part, number, selectedVideo);
                        var mp4Path = Path.Combine(directory, basename + ".mp4");
                        var mkvPath = Path.Combine(directory, basename + ".mkv");
                        if (File.Exists(mp4Path) || File.Exists(mkvPath)) { Interlocked.Increment(ref existing); return; }
                        var path = BatchPolicy.NeedsMatroska(selectedAudio) ? mkvPath : mp4Path;
                        CheckSession();
                        if (!store.KeywordEnabled(rule.Id)) return;
                        Directory.CreateDirectory(directory);
                        await gateway.DownloadWithinSlotAsync(selectedVideo, selectedAudio, path, cancellation, CanStart);
                        if (!File.Exists(path)) throw new IOException("下载完成后未找到输出文件。");
                        store.MarkKeywordCompleted(rule.Id, video.Bvid, part.Cid, path);
                        Interlocked.Increment(ref completed);
                    }, cancellation, CanStart, title, KeywordPolicy.Folder(rule));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception ex) { RecordFailure(ex); }
            }
            async Task EnqueueVideoAsync(SearchVideo item)
            {
                try
                {
                    CheckSession();
                    var video = await gateway.VideoAsync(item.Bvid);
                    CheckSession();
                    if (!store.KeywordEnabled(rule.Id)) return;
                    if (video.Pages.Count == 0) throw new InvalidOperationException("视频没有可访问的分 P。");
                    for (var index = 0; index < video.Pages.Count; index++)
                    {
                        CheckSession();
                        if (!store.KeywordEnabled(rule.Id)) return;
                        var part = video.Pages[index]; var number = index + 1;
                        if (store.KeywordCompleted(rule.Id, video.Bvid, part.Cid)) continue;
                        var legacyMp4 = Path.Combine(rule.Directory, BatchPolicy.FileName(video.Bvid, number, part.Label, false));
                        var legacyMkv = Path.Combine(rule.Directory, BatchPolicy.FileName(video.Bvid, number, part.Label, false, true));
                        if (File.Exists(legacyMp4) || File.Exists(legacyMkv)) { Interlocked.Increment(ref existing); continue; }
                        Update(rule.Id, $"正在下载 {video.Bvid} P{number}");
                        pending.Add(DownloadPartAsync(video, part, number, legacyMp4, legacyMkv));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (BackupSessionChangedException) { throw; }
                catch (Exception ex) { RecordFailure(ex); }
            }
            try
            {
                foreach (var term in rule.Keywords)
                for (var page = 1; page <= 50; page++)
                {
                    if (!await WaitForSearchAsync(rule, session, cancellation)) return;
                    Update(rule.Id, $"正在搜索第 {page} 页…");
                    var result = await gateway.SearchAsync(term, page);
                    CheckSession();
                    missingDates += result.Items.Count(x => x.PublishUnix <= 0);
                    foreach (var item in result.Items)
                    {
                        if (!store.KeywordEnabled(rule.Id)) return;
                        if (item.PublishUnix >= rule.SinceUnix && KeywordPolicy.Matches(item, rule) && seen.Add(item.Bvid)) await EnqueueVideoAsync(item);
                    }
                    if (result.Items.Count == 0 && page < result.PageCount) throw new InvalidOperationException("搜索分页提前结束，请重试。");
                    if (page == 50 && result.PageCount >= 50 && result.Items.Any(x => x.PublishUnix <= 0 || x.PublishUnix >= rule.SinceUnix)) capped = true;
                    if (result.Items.Count == 0 || result.Items.All(x => x.PublishUnix > 0 && x.PublishUnix < rule.SinceUnix) || page >= result.PageCount) break;
                }
            }
            finally { await Task.WhenAll(pending); }
            var status = $"匹配 {seen.Count}，新增 {completed}，失败 {failed}，已有但未登记 {existing}";
            if (capped) status += "；搜索达到 50 页上限，可能漏检";
            if (missingDates > 0) status += $"；跳过 {missingDates} 条发布时间未知的结果";
            if (firstError is not null) status += "；" + firstError;
            Update(rule.Id, status, failed > 0 ? RetryAt(rule) : DateTimeOffset.UtcNow.AddSeconds(rule.EffectiveIntervalSeconds).ToUnixTimeSeconds());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Update(rule.Id, "检查失败：" + ex.Message, RetryAt(rule, IsRateLimited(ex))); }
    }
}
