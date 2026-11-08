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
    Task DownloadWithinSlotAsync(VideoStream video, AudioStream audio, string destination, CancellationToken cancellation, Func<bool> canStart);
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
    public Task DownloadWithinSlotAsync(VideoStream video, AudioStream audio, string destination, CancellationToken cancellation, Func<bool> canStart)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!canStart()) throw new InvalidOperationException("下载规则或登录会话已变化，未启动任务。");
        return MediaDownload.DownloadWithinSlotAsync(video.Urls, audio.Urls, null, destination, api.UserAgent, new Progress<string>());
    }
}

public sealed class BackupEngine(BackupStore store, IBackupGateway gateway)
{
    private readonly SemaphoreSlim runLock = new(1, 1);
    public event Action? Changed;
    private void Update(long id, string status, long? next = null) { store.SetStatus(id, status, next); Changed?.Invoke(); }

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
            var favorites = await gateway.FavoritesAsync(rule.FolderId, cancellation);
            if (gateway.SessionVersion != session) throw new BackupSessionChangedException();
            var completed = 0; var existing = 0; var failed = 0; string? firstError = null;
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
                        var mp4Path = Path.Combine(rule.Directory, BatchPolicy.FileName(video.Bvid, index + 1, page.Label, false));
                        var mkvPath = Path.Combine(rule.Directory, BatchPolicy.FileName(video.Bvid, index + 1, page.Label, false, true));
                        if (store.IsCompleted(rule.Id, video.Bvid, page.Cid)) continue;
                        if (File.Exists(mp4Path) || File.Exists(mkvPath)) { existing++; continue; }
                        try
                        {
                            Update(rule.Id, $"正在备份 {video.Bvid} P{index + 1}");
                            await gateway.InDownloadSlotAsync(async () =>
                            {
                                if (File.Exists(mp4Path) || File.Exists(mkvPath)) { existing++; return; }
                                var streams = await gateway.StreamsAsync(video.Bvid, page);
                                if (streams.IsPreview) throw new InvalidOperationException("当前账号只能获取预览，未作为完整视频备份。");
                                var selectedVideo = BatchPolicy.ChooseVideo(streams.Video, null) ?? throw new InvalidOperationException("没有可用视频流。");
                                var selectedAudio = BatchPolicy.ChooseAudio(streams.Audio, null) ?? throw new InvalidOperationException("没有可用音频流。");
                                var path = BatchPolicy.NeedsMatroska(selectedAudio) ? mkvPath : mp4Path;
                                cancellation.ThrowIfCancellationRequested();
                                if (!store.Enabled(rule.Id)) return;
                                if (gateway.SessionVersion != session) throw new BackupSessionChangedException();
                                await gateway.DownloadWithinSlotAsync(selectedVideo, selectedAudio, path, cancellation, () => store.Enabled(rule.Id) && gateway.SessionVersion == session);
                                if (!File.Exists(path)) throw new IOException("下载完成后未找到输出文件。");
                                store.MarkCompleted(rule.Id, video.Bvid, page.Cid, path); completed++;
                            }, cancellation, () => store.Enabled(rule.Id) && gateway.SessionVersion == session);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (BackupSessionChangedException) { throw; }
                        catch (Exception ex) { failed++; firstError ??= ex.Message; }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (BackupSessionChangedException) { throw; }
                catch (Exception ex) { failed++; firstError ??= ex.Message; }
                await Task.Delay(200, cancellation);
            }
            var summary = $"新增 {completed}，失败 {failed}，已有但未登记 {existing}，失效 {favorites.Invalid}，其他类型 {favorites.Unsupported}";
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
