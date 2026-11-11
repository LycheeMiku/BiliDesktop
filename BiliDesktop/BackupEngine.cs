using System.IO;

namespace BiliDesktop;

internal sealed class BackupSessionChangedException() : Exception("登录会话已变化，等待下次检查。");

public interface IBackupGateway
{
    long SessionVersion { get; }
    Task<AccountInfo> AccountAsync();
    Task<FavoriteContents> FavoritesAsync(long folder, CancellationToken cancellation);
    Task<VideoInfo> VideoAsync(string bvid);
    Task<MediaStreams> StreamsAsync(string bvid, VideoPage page);
    Task InDownloadSlotAsync(Func<Task> work, CancellationToken cancellation, Func<bool> canStart);
    Task InBackupDownloadSlotAsync(Func<Task> work, CancellationToken cancellation, Func<bool> canStart, string title, string directory, long ruleId) => InDownloadSlotAsync(work, cancellation, canStart);
    Task DownloadWithinSlotAsync(VideoStream video, AudioStream? audio, string destination, CancellationToken cancellation, Func<bool> canStart);
    Task<bool> IsCompleteVideoAsync(string path, double? duration, CancellationToken cancellation) => MediaDownload.ValidateCompleteVideoAsync(path, duration, cancellation);
    Task<bool> IsCompleteSilentVideoAsync(string path, double? duration, CancellationToken cancellation) => Task.FromResult(false);
}

public interface IKeywordGateway : IBackupGateway
{
    Task<VideoSearchPage> SearchAsync(string keyword, int page);
    Task InKeywordDownloadSlotAsync(Func<Task> work, CancellationToken cancellation, Func<bool> canStart, string title, string directory) => InDownloadSlotAsync(work, cancellation, canStart);
}

public sealed class BiliBackupGateway(BiliApi api) : IKeywordGateway
{
    public long SessionVersion => api.SessionVersion;
    public Task<AccountInfo> AccountAsync() => api.GetAccountInfoAsync();
    public Task<FavoriteContents> FavoritesAsync(long folder, CancellationToken cancellation) => api.GetFavoriteContentsAsync(folder, cancellation: cancellation);
    public Task<VideoInfo> VideoAsync(string bvid) => api.GetVideoAsync(bvid);
    public Task<MediaStreams> StreamsAsync(string bvid, VideoPage page) => api.GetStreamsForPageAsync(bvid, page);
    public Task<VideoSearchPage> SearchAsync(string keyword, int page) => api.SearchVideosAsync(keyword, page, "pubdate");
    public Task InDownloadSlotAsync(Func<Task> work, CancellationToken cancellation, Func<bool> canStart) => MediaDownload.InSlotAsync(work, cancellation, canStart);
    public Task<bool> IsCompleteSilentVideoAsync(string path, double? duration, CancellationToken cancellation) => MediaDownload.ValidateCompleteVideoAsync(path, duration, cancellation, requireAudio: false);
    public Task DownloadWithinSlotAsync(VideoStream video, AudioStream? audio, string destination, CancellationToken cancellation, Func<bool> canStart)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!canStart()) throw new InvalidOperationException("下载规则或登录会话已变化，未启动任务。");
        return MediaDownload.DownloadWithinSlotAsync(video.Urls, audio?.Urls, null, destination, api.UserAgent, new Progress<string>(), legacySegments: video.LegacySegments ?? audio?.LegacySegments);
    }
}

public sealed class BackupEngine(BackupStore store, IBackupGateway gateway)
{
    private readonly SemaphoreSlim runLock = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, DateTime LastWriteUtc, double? Duration, long Session)> verified = new(StringComparer.OrdinalIgnoreCase);
    public event Action? Changed;
    private void Update(long id, string status, long? next = null) { store.SetStatus(id, status, next); Changed?.Invoke(); }
    public static string FolderDirectory(string root, string title, long folderId) => Path.Combine(root, DownloadNaming.SafeComponent(title) + " [" + folderId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]");
    public static string FolderDirectory(BackupRule rule) => FolderDirectory(rule.Directory, rule.FolderTitle, rule.FolderId);
    private async Task<bool> IsCompleteAsync(string path, double? duration, CancellationToken cancellation, Func<Task<MediaStreams>> sourceStreams)
    {
        var before = new FileInfo(path);
        if (!before.Exists || before.Length == 0) return false;
        var session = gateway.SessionVersion;
        if (verified.TryGetValue(before.FullName, out var cached) && cached == (before.Length, before.LastWriteTimeUtc, duration, session)) return true;
        if (!await gateway.IsCompleteVideoAsync(before.FullName, duration, cancellation))
        {
            if (!await gateway.IsCompleteSilentVideoAsync(before.FullName, duration, cancellation)) return false;
            var streams = await sourceStreams();
            if (streams.IsPreview || streams.Video.Count == 0 || streams.Audio.Count != 0 || gateway.SessionVersion != session) return false;
        }
        var after = new FileInfo(path);
        if (!after.Exists || after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc) return false;
        verified[after.FullName] = (after.Length, after.LastWriteTimeUtc, duration, session);
        return true;
    }
    private static bool InsideDirectory(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    private static string AvailablePath(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!; var name = Path.GetFileNameWithoutExtension(path); var extension = Path.GetExtension(path);
        for (var n = 1; ; n++) { var candidate = Path.Combine(directory, $"{name}-恢复-{n}{extension}"); if (!File.Exists(candidate)) return candidate; }
    }

    public async Task RunDueAsync(CancellationToken cancellation = default)
    {
        if (!await runLock.WaitAsync(0, cancellation)) return;
        try
        {
            foreach (var rule in store.List().Where(r => r.Enabled && r.NextRun <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!store.Enabled(rule.Id)) continue;
                await RunRuleAsync(rule, cancellation);
            }
        }
        finally { runLock.Release(); }
    }

    private async Task RunRuleAsync(BackupRule rule, CancellationToken cancellation)
    {
        try
        {
            Update(rule.Id, "正在检查账号和收藏夹…");
            var session = gateway.SessionVersion;
            var account = await gateway.AccountAsync();
            if (gateway.SessionVersion != session) throw new BackupSessionChangedException();
            if (account.Mid != rule.AccountId) throw new InvalidOperationException("当前登录账号与规则所属账号不一致。");
            if (!Directory.Exists(rule.Directory)) throw new DirectoryNotFoundException("备份目录不存在。");
            var outputDirectory = FolderDirectory(rule);
            Directory.CreateDirectory(outputDirectory);
            var favorites = await gateway.FavoritesAsync(rule.FolderId, cancellation);
            if (gateway.SessionVersion != session) throw new BackupSessionChangedException();
            var completed = 0; var existing = 0; var failed = 0; string? firstError = null;
            var pending = new List<Task>();
            var scheduled = new HashSet<(string, long)>();
            var errorLock = new object();
            void Failure(Exception ex) { Interlocked.Increment(ref failed); lock (errorLock) firstError ??= ex.Message; }
            async Task DownloadPageAsync(VideoInfo video, VideoPage page, int pageNumber, string mp4Path, string mkvPath)
            {
                try
                {
                    await gateway.InBackupDownloadSlotAsync(async () =>
                    {
                        foreach (var candidate in new[] { mp4Path, mkvPath })
                        {
                            if (!await IsCompleteAsync(candidate, page.DurationSeconds, cancellation, () => gateway.StreamsAsync(video.Bvid, page))) continue;
                            if (!store.Enabled(rule.Id)) return;
                            if (gateway.SessionVersion != session) throw new BackupSessionChangedException();
                            store.MarkCompleted(rule.Id, video.Bvid, page.Cid, candidate);
                            store.RecordFavoriteFile(rule.AccountId, rule.FolderId, video.Bvid, page.Cid, candidate);
                            Interlocked.Increment(ref existing); return;
                        }
                        var streams = await gateway.StreamsAsync(video.Bvid, page);
                        if (streams.IsPreview) throw new InvalidOperationException("当前账号只能获取预览，未作为完整视频备份。");
                        var selectedVideo = BatchPolicy.ChooseVideo(streams.Video, null) ?? throw new InvalidOperationException("没有可用视频流。");
                        var selectedAudio = BatchPolicy.ChooseAudio(streams.Audio, null);
                        var path = AvailablePath(selectedAudio is not null && BatchPolicy.NeedsMatroska(selectedAudio) ? mkvPath : mp4Path);
                        cancellation.ThrowIfCancellationRequested();
                        if (!store.Enabled(rule.Id)) return;
                        if (gateway.SessionVersion != session) throw new BackupSessionChangedException();
                        await gateway.DownloadWithinSlotAsync(selectedVideo, selectedAudio, path, cancellation, () => store.Enabled(rule.Id) && gateway.SessionVersion == session);
                        if (!File.Exists(path)) throw new IOException("下载完成后未找到输出文件。");
                        store.MarkCompleted(rule.Id, video.Bvid, page.Cid, path); store.RecordFavoriteFile(rule.AccountId, rule.FolderId, video.Bvid, page.Cid, path); Interlocked.Increment(ref completed);
                    }, cancellation, () => store.Enabled(rule.Id) && gateway.SessionVersion == session, $"{video.Title} P{pageNumber}", outputDirectory, rule.Id);
                }
                catch (OperationCanceledException) { throw; }
                catch (BackupSessionChangedException) { throw; }
                catch (Exception ex) { Failure(ex); }
            }
            try
            {
            foreach (var favorite in favorites.Videos)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!store.Enabled(rule.Id)) return;
                if (gateway.SessionVersion != session) throw new BackupSessionChangedException();
                try
                {
                    var video = await gateway.VideoAsync(favorite.Bvid);
                    if (video.Pages.Count == 0) throw new InvalidOperationException("视频没有可访问的分 P。");
                    for (var index = 0; index < video.Pages.Count; index++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (!store.Enabled(rule.Id)) return;
                        var page = video.Pages[index];
                        if (!scheduled.Add((video.Bvid, page.Cid))) continue;
                        var mp4Path = Path.Combine(outputDirectory, BatchPolicy.FileName(video.Bvid, index + 1, page.Label, false));
                        var mkvPath = Path.Combine(outputDirectory, BatchPolicy.FileName(video.Bvid, index + 1, page.Label, false, true));
                        var candidates = new List<string?> { store.FavoriteFile(rule.AccountId, rule.FolderId, video.Bvid, page.Cid), store.CompletedPath(rule.Id, video.Bvid, page.Cid), mp4Path, mkvPath };
                        var ambiguousTitle = favorites.Videos.Count(f => string.Equals(f.Title, favorite.Title, StringComparison.OrdinalIgnoreCase)) > 1;
                        if (!ambiguousTitle && page.DurationSeconds is > 0)
                        {
                            var stem = DownloadNaming.BaseName(video, page, index + 1, null, null) + "-";
                            foreach (var folder in new[] { DownloadNaming.Folder(outputDirectory, video), DownloadNaming.Folder(rule.Directory, video) }.Distinct(StringComparer.OrdinalIgnoreCase))
                                if (Directory.Exists(folder)) candidates.AddRange(Directory.EnumerateFiles(folder).Where(path => (Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase)) && Path.GetFileNameWithoutExtension(path).StartsWith(stem, StringComparison.OrdinalIgnoreCase)));
                        }
                        var found = false;
                        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            if (!InsideDirectory(rule.Directory, candidate!) || !await IsCompleteAsync(candidate!, page.DurationSeconds, cancellation, () => gateway.StreamsAsync(video.Bvid, page))) continue;
                            if (!store.Enabled(rule.Id)) return;
                            if (gateway.SessionVersion != session) throw new BackupSessionChangedException();
                            store.MarkCompleted(rule.Id, video.Bvid, page.Cid, candidate!);
                            store.RecordFavoriteFile(rule.AccountId, rule.FolderId, video.Bvid, page.Cid, candidate!);
                            Interlocked.Increment(ref existing); found = true; break;
                        }
                        if (found) continue;
                        if (!store.Enabled(rule.Id)) return;
                        if (gateway.SessionVersion != session) throw new BackupSessionChangedException();
                        Update(rule.Id, $"正在备份 {video.Bvid} P{index + 1}");
                        pending.Add(DownloadPageAsync(video, page, index + 1, mp4Path, mkvPath));
                        var limit = MediaDownload.CurrentOptions.MaxConcurrentTasks;
                        if (limit > 0 && pending.Count >= limit)
                        {
                            var finished = await Task.WhenAny(pending);
                            pending.Remove(finished);
                            await finished;
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (BackupSessionChangedException) { throw; }
                catch (Exception ex) { Failure(ex); }
                await Task.Delay(200, cancellation);
            }
            }
            finally { await Task.WhenAll(pending); }
            var summary = $"新增 {completed}，失败 {failed}，已有完整 {existing}，失效 {favorites.Invalid}，其他类型 {favorites.Unsupported}";
            if (firstError is not null) summary += "；" + firstError;
            var retryMinutes = failed > 0 ? Math.Min(rule.IntervalMinutes, 15) : rule.IntervalMinutes;
            Update(rule.Id, summary, DateTimeOffset.UtcNow.AddMinutes(retryMinutes).ToUnixTimeSeconds());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Update(rule.Id, "检查失败：" + ex.Message, DateTimeOffset.UtcNow.AddMinutes(Math.Min(rule.IntervalMinutes, 15)).ToUnixTimeSeconds());
        }
    }
}
