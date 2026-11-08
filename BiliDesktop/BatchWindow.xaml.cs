using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace BiliDesktop;

public partial class BatchWindow : Window
{
    private readonly BiliApi api;
    private readonly VideoStream? preferredVideo;
    private readonly AudioStream? preferredAudio;
    private readonly List<BatchJob> jobs;
    private readonly VideoInfo? groupVideo;
    private readonly string? collectionTitle;
    private readonly DownloadQueue taskQueue;
    private CancellationTokenSource? cancellation;
    private bool running;
    internal bool IsRunning => running;

    public BatchWindow(BiliApi api, VideoInfo? video, IReadOnlyList<CollectionVideo>? collection, VideoStream? preferredVideo, AudioStream? preferredAudio, DownloadQueue? taskQueue = null)
    {
        if (video is null && collection is null) throw new ArgumentException("没有批量下载条目。");
        this.api = api; this.preferredVideo = preferredVideo; this.preferredAudio = preferredAudio; groupVideo = collection is null ? video : null; this.taskQueue = taskQueue ?? DownloadQueue.Shared;
        collectionTitle = collection is null ? null : (!string.IsNullOrWhiteSpace(video?.CollectionTitle) ? video.CollectionTitle : collection.Select(x => x.CollectionTitle).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)));
        InitializeComponent();
        QueueButton.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty, Localizer.Key("下载任务"));
        jobs = collection is null
            ? video!.Pages.Select((p, i) => new BatchJob { Number = i + 1, Bvid = p.Bvid ?? video.Bvid, Page = p, Label = p.Label }).ToList()
            : collection.Select((v, i) => new BatchJob { Number = i + 1, Bvid = v.Bvid, Label = v.Title, CollectionTitle = collectionTitle ?? v.CollectionTitle, CollectionNumber = v.CollectionNumber, CollectionItemTitle = v.CollectionNumber.HasValue ? v.Title : null }).ToList();
        if (collection is not null) Title = Localizer.Translate("合集批量下载");
        Jobs.ItemsSource = jobs;
        try { DirectoryBox.Text = AppSettings.LoadDownloadDirectory(); }
        catch (Exception ex) { DirectoryBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos); Summary.Text = "读取下载目录失败：" + ex.Message; }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择下载目录", Multiselect = false };
        if (dialog.ShowDialog() == true) DirectoryBox.Text = dialog.FolderName;
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var job in jobs) job.Selected = true; Jobs.Items.Refresh(); }
    private void SelectNone_Click(object sender, RoutedEventArgs e) { foreach (var job in jobs) job.Selected = false; Jobs.Items.Refresh(); }
    private void Queue_Click(object sender, RoutedEventArgs e) => WindowCoordinator.ShowDownloads(taskQueue);
    private void Mode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { if (AudioOptions is not null && Mode is not null) AudioOptions.Visibility = Mode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed; }
    private void Stop_Click(object sender, RoutedEventArgs e) { cancellation?.Cancel(); Stop.IsEnabled = false; Summary.Text = "已停止排队，正在等待已开始的任务…"; }
    private void Window_Closing(object sender, CancelEventArgs e) { if (running) { e.Cancel = true; Summary.Text = "请先停止排队并等待已开始的任务结束。"; } }

    private void SetRunning(bool value)
    {
        running = value; Start.IsEnabled = !value; Browse.IsEnabled = !value; SelectAll.IsEnabled = !value; SelectNone.IsEnabled = !value;
        Mode.IsEnabled = !value; OutputFormat.IsEnabled = !value; AudioOptions.IsEnabled = !value; Jobs.IsReadOnly = value; Stop.IsEnabled = value;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        Jobs.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        Jobs.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        var chosen = jobs.Where(j => j.Selected).ToArray();
        if (chosen.Length == 0) { Summary.Text = "请至少选择一个条目。"; return; }
        var directory = DirectoryBox.Text;
        if (!Directory.Exists(directory)) { Summary.Text = "请选择有效的下载目录。"; return; }
        directory = Path.GetFullPath(directory);
        if (groupVideo is not null && DownloadNaming.NeedsFolder(groupVideo))
        {
            directory = DownloadNaming.Folder(directory, groupVideo);
            try { Directory.CreateDirectory(directory); } catch (Exception ex) { Summary.Text = ex.Message; return; }
        }
        else if (!string.IsNullOrWhiteSpace(collectionTitle))
        {
            directory = Path.Combine(directory, DownloadNaming.SafeComponent(collectionTitle));
            try { Directory.CreateDirectory(directory); } catch (Exception ex) { Summary.Text = ex.Message; return; }
        }
        var mode = Mode.SelectedIndex;
        var audioOptions = mode == 2 ? AudioOptions.Options : null;
        var format = (BatchFormat)OutputFormat.SelectedIndex;
        var limit = chosen.Length;
        foreach (var job in chosen) { job.Finished = false; job.Status = "排队中"; }
        using var token = new CancellationTokenSource();
        cancellation = token;
        SetRunning(true); Summary.Text = $"共 {chosen.Length} 个任务";
        var ua = api.UserAgent;
        var engine = new BatchDownloadEngine(api, preferredVideo, preferredAudio);
        string? schedulerError = null;
        try
        {
            WindowCoordinator.ShowDownloads(taskQueue);
            await BatchScheduler.RunAsync(chosen, limit, async job =>
            {
                try
                {
                    job.HasError = false; job.OutputPath = null;
                    var capturedSession = api.SessionVersion;
                    var metadata = groupVideo;
                    if (job.Page is null) { job.Status = "正在读取视频分 P…"; metadata = await api.GetVideoAsync(job.Bvid); }
                    if (api.SessionVersion != capturedSession) throw new InvalidOperationException("登录会话已变化，请重新解析视频。");
                    var parts = job.Page is null ? metadata!.Pages.Select((page, index) => new BatchJob { Number = index + 1, Bvid = page.Bvid ?? job.Bvid, Label = page.Label, Page = page, CollectionTitle = job.CollectionTitle, CollectionNumber = job.CollectionNumber, CollectionItemTitle = job.CollectionItemTitle }).ToArray() : [job];
                    if (parts.Length == 0) throw new InvalidOperationException("视频没有可用的分 P。");
                    var targetDirectory = job.Page is null && metadata is not null ? DownloadNaming.Folder(directory, metadata) : directory;
                    Directory.CreateDirectory(targetDirectory);
                    var completed = 0; var skipped = 0; var failed = 0;
                    foreach (var part in parts)
                    {
                        var entry = taskQueue.Enqueue((metadata?.Title ?? job.Label) + " · " + part.Label, targetDirectory, async (control, progress) =>
                        {
                            if (api.SessionVersion != capturedSession) throw new InvalidOperationException("登录会话已变化，请重新解析视频。");
                            part.Finished = false; part.HasError = false; part.OutputPath = null;
                            await engine.RunJobAsync(part, targetDirectory, mode, format, ua, control, progress, metadata, useNaming: true, audioOptions: audioOptions);
                            if (part.HasError) throw new InvalidOperationException(part.Status);
                            if (part.OutputPath is null) control.MarkSkipped();
                            return part.OutputPath;
                        });
                        await entry.Completion;
                        if (entry.State == DownloadState.Completed) completed++;
                        else if (entry.State == DownloadState.Skipped) skipped++;
                        else failed++;
                        if (job.Page is null) job.Status = $"完成 {completed}，失败 {failed}，跳过 {skipped}";
                    }
                    if (job.Page is null) { job.HasError = failed > 0; job.Finished = true; }
                }
                catch (Exception ex) { job.HasError = true; job.Finished = true; job.Status = "失败：" + ex.Message; }
            }, job => { job.Status = "已停止排队"; job.Finished = true; }, token.Token);
        }
        catch (Exception ex) { schedulerError = ex.Message; }
        finally
        {
            cancellation = null; SetRunning(false);
            Summary.Text = schedulerError is null
                ? $"完成 {chosen.Count(j => j.Status.StartsWith("完成", StringComparison.Ordinal))} / {chosen.Length}，其余项目可查看状态后重试。"
                : "批量任务失败：" + schedulerError;
        }
    }

}
