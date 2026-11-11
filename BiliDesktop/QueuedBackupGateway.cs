using System.IO;

namespace BiliDesktop;

// Automatic rules already refresh stream URLs while holding a global slot. Route that
// existing operation through the same queue without acquiring a second slot.
public sealed class QueuedBackupGateway(BiliApi api, DownloadQueue? taskQueue = null) : IKeywordGateway
{
    private readonly BiliBackupGateway inner = new(api);
    private readonly AsyncLocal<Context?> current = new();
    private sealed class Context(TransferControl control, IProgress<string> progress)
    {
        public TransferControl Control { get; } = control;
        public IProgress<string> Progress { get; } = progress;
        public string? Destination { get; set; }
    }
    public long SessionVersion => inner.SessionVersion;
    public Task<AccountInfo> AccountAsync() => inner.AccountAsync();
    public Task<FavoriteContents> FavoritesAsync(long folder, CancellationToken cancellation) => inner.FavoritesAsync(folder, cancellation);
    public Task<VideoInfo> VideoAsync(string bvid) => inner.VideoAsync(bvid);
    public Task<MediaStreams> StreamsAsync(string bvid, VideoPage page)
    {
        current.Value?.Progress.Report("正在获取视频下载地址…");
        return api.GetStreamsForPageAsync(bvid, page, current.Value?.Control.Token ?? CancellationToken.None);
    }
    public Task<VideoSearchPage> SearchAsync(string keyword, int page) => inner.SearchAsync(keyword, page);
    public Task InDownloadSlotAsync(Func<Task> work, CancellationToken cancellation, Func<bool> canStart) => EnqueueAsync(work, cancellation, canStart, Localizer.Translate("自动下载"), AppSettings.LoadDownloadDirectory());
    public Task InBackupDownloadSlotAsync(Func<Task> work, CancellationToken cancellation, Func<bool> canStart, string title, string directory, long ruleId) => EnqueueAsync(work, cancellation, canStart, title, directory, ruleId);
    public Task InKeywordDownloadSlotAsync(Func<Task> work, CancellationToken cancellation, Func<bool> canStart, string title, string directory) => EnqueueAsync(work, cancellation, canStart, title, directory);
    private async Task EnqueueAsync(Func<Task> work, CancellationToken cancellation, Func<bool> canStart, string title, string directory, long? backupRuleId = null)
    {
        var entry = (taskQueue ?? DownloadQueue.Shared).Enqueue(title, directory, async (control, progress) =>
        {
            using var registration = cancellation.Register(control.Cancel);
            var previous = current.Value; var context = new Context(control, progress); current.Value = context;
            try { await MediaDownload.InSlotAsync(work, control.Token, canStart, control); if (context.Destination is null) control.MarkSkipped(); return context.Destination; }
            finally { current.Value = previous; }
        }, backupRuleId);
        await entry.Completion;
        cancellation.ThrowIfCancellationRequested();
        if (entry.State == DownloadState.Canceled) throw new InvalidOperationException("用户取消下载。");
        if (entry.Failure is Exception failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    public Task<bool> IsCompleteSilentVideoAsync(string path, double? duration, CancellationToken cancellation) => inner.IsCompleteSilentVideoAsync(path, duration, cancellation);
    public Task DownloadWithinSlotAsync(VideoStream video, AudioStream? audio, string destination, CancellationToken cancellation, Func<bool> canStart)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!canStart()) throw new InvalidOperationException("下载规则或登录会话已变化，未启动任务。");
        var context = current.Value ?? throw new InvalidOperationException("自动下载任务上下文不可用。");
        context.Destination = destination; context.Control.Identity?.Invoke(Path.GetFileName(destination), destination);
        return MediaDownload.DownloadWithinSlotAsync(video.Urls, audio?.Urls, null, destination, api.UserAgent, context.Progress, control: context.Control, legacySegments: video.LegacySegments ?? audio?.LegacySegments);
    }
}
