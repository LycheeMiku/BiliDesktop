using System.Diagnostics;
using System.IO;
using System.Windows;

namespace BiliDesktop;

public partial class DownloadQueueWindow : Window
{
    private readonly DownloadQueue queue;
    private readonly Func<bool> confirmDelete;
    private readonly Action<string> openFile;
    private readonly Action<string> openFolder;
    private string? actionError;
    private int deletionCount;
    public DownloadQueueWindow(DownloadQueue queue) : this(queue, null, null, null) { }
    internal DownloadQueueWindow(DownloadQueue queue, Func<bool>? confirmDelete, Action<string>? openFile, Action<string>? openFolder)
    {
        this.queue = queue;
        this.confirmDelete = confirmDelete ?? (() => MessageBox.Show(string.Format(Localizer.Translate("确定永久删除所选的 {0} 个文件？"), deletionCount), Localizer.Translate("删除文件"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
        this.openFile = openFile ?? (path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
        this.openFolder = openFolder ?? OpenLocation;
        InitializeComponent(); Title = Localizer.Translate("下载任务"); Tasks.ItemsSource = queue.Items;
        SelectAll.ToolTip = Localizer.Translate("全选");
        NameColumn.Header = Localizer.Translate("名称"); StatusColumn.Header = Localizer.Translate("状态"); ProgressColumn.Header = Localizer.Translate("进度"); BytesColumn.Header = Localizer.Translate("当前流"); SpeedColumn.Header = Localizer.Translate("速度"); ConnectionsColumn.Header = Localizer.Translate("连接数"); EtaColumn.Header = Localizer.Translate("剩余时间");
        PauseButton.Content = Localizer.Translate("暂停"); ResumeButton.Content = Localizer.Translate("继续"); RetryButton.Content = Localizer.Translate("重试"); CancelButton.Content = Localizer.Translate("取消下载"); OpenButton.Content = Localizer.Translate("打开文件"); FolderButton.Content = Localizer.Translate("打开所在文件夹"); RemoveButton.Content = Localizer.Translate("移除记录"); DeleteButton.Content = Localizer.Translate("删除文件");
        Tasks.SelectionChanged += (_, _) => { actionError = null; RefreshSelection(); };
        System.Collections.Specialized.NotifyCollectionChangedEventHandler changed = (_, _) => RefreshSelection();
        queue.Items.CollectionChanged += changed;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => RefreshSelection(); timer.Start(); Closed += (_, _) => { timer.Stop(); queue.Items.CollectionChanged -= changed; }; RefreshSelection();
    }
    private DownloadEntry[] Targets()
    {
        var selected = queue.Items.Where(x => x.IsChecked).ToArray();
        return selected.Length > 0 ? selected : Tasks.SelectedItem is DownloadEntry item ? [item] : [];
    }
    private static bool IsDeletable(DownloadEntry item) => item.State == DownloadState.Completed && File.Exists(item.Destination);
    private static bool CanRemove(DownloadEntry item) => item.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Canceled or DownloadState.Skipped;
    internal static string ContainingFolder(string destination)
    {
        var path = Path.GetFullPath(destination);
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) throw new DirectoryNotFoundException("下载目录不存在。");
        return directory;
    }
    private static bool HasFolder(DownloadEntry item) { try { _ = ContainingFolder(item.Destination); return true; } catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { return false; } }
    private void RefreshSelection()
    {
        var checkedCount = queue.Items.Count(x => x.IsChecked); var entries = Targets();
        SelectAll.IsChecked = checkedCount == 0 ? false : checkedCount == queue.Items.Count ? true : null;
        SelectAll.IsEnabled = queue.Items.Count > 0;
        PauseButton.IsEnabled = entries.Any(x => x.CanPause);
        ResumeButton.IsEnabled = entries.Any(x => x.State == DownloadState.Paused);
        RetryButton.IsEnabled = entries.Any(x => x.State is DownloadState.Failed or DownloadState.Canceled or DownloadState.Skipped);
        CancelButton.IsEnabled = entries.Any(x => x.State is DownloadState.Waiting or DownloadState.Running or DownloadState.Paused);
        OpenButton.IsEnabled = entries.Any(IsDeletable);
        FolderButton.IsEnabled = entries.Any(HasFolder);
        RemoveButton.IsEnabled = entries.Any(CanRemove);
        DeleteButton.IsEnabled = entries.Any(IsDeletable);
        Status.Text = actionError ?? (checkedCount > 0 ? string.Format(Localizer.Translate("已勾选 {0} 项"), checkedCount) : entries.FirstOrDefault()?.Detail ?? (queue.HistoryWarning is string warning ? Localizer.Translate(warning) : ""));
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var value = SelectAll.IsChecked == true;
        foreach (var item in queue.Items) item.IsChecked = value;
        actionError = null; RefreshSelection();
    }
    private void Check_Click(object sender, RoutedEventArgs e) { actionError = null; RefreshSelection(); }
    private void Perform(Func<DownloadEntry, bool> eligible, Action<DownloadEntry> action, DownloadEntry[]? snapshot = null)
    {
        var items = (snapshot ?? Targets()).Where(eligible).ToArray(); var failures = 0; string? first = null;
        actionError = null;
        foreach (var item in items) try { action(item); } catch (Exception ex) { failures++; first ??= ex.Message; }
        if (failures > 0) actionError = string.Format(Localizer.Translate("操作失败 {0} 项：{1}"), failures, first);
        RefreshSelection();
    }
    private void Pause_Click(object sender, RoutedEventArgs e) => Perform(x => x.CanPause, x => x.Pause());
    private void Resume_Click(object sender, RoutedEventArgs e) => Perform(x => x.State == DownloadState.Paused, x => x.Resume());
    private void Retry_Click(object sender, RoutedEventArgs e) => Perform(x => x.State is DownloadState.Failed or DownloadState.Canceled or DownloadState.Skipped, x => x.Retry());
    private void Cancel_Click(object sender, RoutedEventArgs e) => Perform(x => x.State is DownloadState.Waiting or DownloadState.Running or DownloadState.Paused, x => x.Cancel());
    private void Open_Click(object sender, RoutedEventArgs e) => Perform(IsDeletable, x => openFile(x.Destination));
    private static void OpenLocation(string directory) => Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    private void Folder_Click(object sender, RoutedEventArgs e) => Perform(HasFolder, x => openFolder(ContainingFolder(x.Destination)));
    private void Remove_Click(object sender, RoutedEventArgs e) => Perform(CanRemove, queue.Remove);
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        actionError = null;
        var selected = Targets().Where(IsDeletable).ToArray(); deletionCount = selected.Length;
        if (deletionCount == 0 || !confirmDelete()) { RefreshSelection(); return; }
        Perform(IsDeletable, item => { File.Delete(item.Destination); queue.Remove(item); }, selected);
    }
}
