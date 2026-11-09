using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace BiliDesktop;

public enum DownloadState { Waiting, Running, Paused, Canceling, Failed, Canceled, Completed, Skipped }

public sealed class DownloadEntry : INotifyPropertyChanged
{
    private readonly Func<TransferControl, IProgress<string>, Task<string?>> work;
    private readonly DownloadQueue queue;
    private TransferControl? control;
    private DownloadState state = DownloadState.Waiting;
    private string detail = "等待下载任务…";
    private string destination;
    private TransferProgress? transfer;
    private int attempt;
    private string title;
    private bool isChecked;
    private bool retired;
    private bool pausedWhileWaiting;
    public long? BackupRuleId { get; }
    public bool IsChecked { get => isChecked; set { if (isChecked == value) return; isChecked = value; Notify(); } }
    public string Title { get => title; private set { title = value; Notify(); } }
    public string Destination { get => destination; set { destination = value; Notify(); } }
    public DownloadState State { get => state; private set { state = value; Notify(); Notify(nameof(StateText)); Notify(nameof(CanPause)); Notify(nameof(CanRetry)); } }
    public string StateText => State == DownloadState.Running ? Detail : Localizer.Translate(State switch { DownloadState.Waiting => "排队中", DownloadState.Paused => "已暂停", DownloadState.Canceling => "正在取消", DownloadState.Failed => "失败", DownloadState.Canceled => "已取消", DownloadState.Skipped => "已跳过", _ => "完成" });
    public string Detail { get => detail; private set { detail = value; Notify(); Notify(nameof(StateText)); } }
    public double Percent => State == DownloadState.Completed ? 100 : transfer is { Total: > 0 } p ? Math.Clamp(100d * p.Completed / p.Total, 0, 100) : 0;
    public string ProgressText => transfer is { Total: > 0 } p ? $"{Localizer.Translate(p.Stage)} {p.Completed / 1048576d:F1}/{p.Total / 1048576d:F1} MiB" : "—";
    public string Speed => transfer is { BytesPerSecond: > 0 } p && State == DownloadState.Running ? $"{p.BytesPerSecond / 1048576d:F2} MiB/s" : "—";
    public string Connections => transfer is { } p && State == DownloadState.Running ? p.Connections.ToString() : "—";
    public string Eta => transfer is { Total: > 0, BytesPerSecond: > 0 } p && State == DownloadState.Running ? TimeSpan.FromSeconds(Math.Clamp((p.Total - p.Completed) / (double)p.BytesPerSecond, 0, 359999)).ToString(@"hh\:mm\:ss") : "—";
    public bool CanPause => State is DownloadState.Waiting or DownloadState.Running && control?.CanPause == true;
    public bool CanRetry => !retired && State is (DownloadState.Failed or DownloadState.Canceled or DownloadState.Skipped);
    public Task Completion { get; private set; } = Task.CompletedTask;
    public Exception? Failure { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void NotifyProgress() { Notify(nameof(Percent)); Notify(nameof(ProgressText)); Notify(nameof(Speed)); Notify(nameof(Connections)); Notify(nameof(Eta)); Notify(nameof(CanPause)); }
    internal DownloadEntry(DownloadQueue queue, string title, string destination, Func<TransferControl, IProgress<string>, Task<string?>> work, long? backupRuleId = null)
    { this.queue = queue; this.title = title; this.destination = destination; this.work = work; BackupRuleId = backupRuleId; }
    internal void Start()
    {
        if (retired || State is not (DownloadState.Waiting or DownloadState.Failed or DownloadState.Canceled or DownloadState.Skipped)) return;
        control?.Dispose(); transfer = null; Failure = null; NotifyProgress(); State = DownloadState.Waiting; Detail = "等待下载任务…";
        var generation = ++attempt;
        var current = new TransferControl { Identity = (name, path) => { if (generation == attempt) { Title = name; Destination = path; } }, Progress = new Progress<TransferProgress>(p => { if (generation == attempt && State is (DownloadState.Running or DownloadState.Waiting)) { transfer = p; State = DownloadState.Running; NotifyProgress(); } }) };
        control = current;
        Completion = RunAsync(current);
    }
    private async Task RunAsync(TransferControl current)
    {
        var generation = attempt;
        var messages = new Progress<string>(message => { if (generation == attempt && State is not (DownloadState.Completed or DownloadState.Failed or DownloadState.Canceled or DownloadState.Canceling or DownloadState.Skipped)) { if (State == DownloadState.Waiting && message != "等待下载任务…") State = DownloadState.Running; Detail = Localizer.Dynamic(message); Notify(nameof(CanPause)); } });
        try { var output = await work(current, messages); if (!string.IsNullOrEmpty(output)) Destination = output; State = current.IsSkipped ? DownloadState.Skipped : DownloadState.Completed; Detail = Localizer.Translate(current.IsSkipped ? "已跳过" : "下载完成"); transfer = null; NotifyProgress(); if (!current.IsSkipped) queue.SaveHistory(); }
        catch (OperationCanceledException) { State = DownloadState.Canceled; Detail = Localizer.Translate("已取消"); transfer = null; NotifyProgress(); }
        catch (Exception) when (current.Token.IsCancellationRequested) { State = DownloadState.Canceled; Detail = Localizer.Translate("已取消"); transfer = null; NotifyProgress(); }
        catch (Exception ex) { Failure = ex; State = DownloadState.Failed; Detail = Localizer.Dynamic("下载失败：" + ex.Message); transfer = null; NotifyProgress(); }
    }
    public void Pause() { if (control?.Pause() == true) { pausedWhileWaiting = State == DownloadState.Waiting; State = DownloadState.Paused; NotifyProgress(); } }
    public void Resume() { if (State == DownloadState.Paused) { State = pausedWhileWaiting ? DownloadState.Waiting : DownloadState.Running; control?.Resume(); NotifyProgress(); } }
    public void Cancel() { if (State is DownloadState.Waiting or DownloadState.Running or DownloadState.Paused) { State = DownloadState.Canceling; control?.Cancel(); NotifyProgress(); } }
    public void Retry() => Start();
    internal void Retire() { retired = true; Notify(nameof(CanRetry)); Cancel(); }
    internal void MarkCompleted() { State = DownloadState.Completed; Detail = Localizer.Translate("下载完成"); }
}

public sealed class DownloadQueue
{
    public static DownloadQueue Shared { get; } = new();
    public ObservableCollection<DownloadEntry> Items { get; } = [];
    private readonly string historyPath;
    private bool historyReadable = true;
    public string? HistoryWarning { get; private set; }
    public bool HasActive => Items.Any(x => x.State is DownloadState.Waiting or DownloadState.Running or DownloadState.Paused or DownloadState.Canceling);
    public DownloadQueue(string? path = null)
    {
        historyPath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliDesktop", "download-history.json");
        try
        {
            if (!File.Exists(historyPath)) return;
            var history = JsonSerializer.Deserialize<List<HistoryItem>>(File.ReadAllText(historyPath)) ?? throw new InvalidDataException();
            if (history.Any(item => item is null || string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Path) || !Path.IsPathFullyQualified(item.Path))) throw new InvalidDataException();
            foreach (var item in history) _ = Path.GetFullPath(item.Path);
            foreach (var item in history) { var entry = new DownloadEntry(this, item.Title, item.Path, (_, _) => Task.FromResult<string?>(null)); entry.MarkCompleted(); Items.Add(entry); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException) { historyReadable = false; HistoryWarning = "下载历史无法读取，原文件已保留；本次完成记录不会写入。"; }
    }
    private sealed record HistoryItem(string Title, string Path);
    public DownloadEntry Enqueue(string title, string path, Func<TransferControl, IProgress<string>, Task<string?>> work, long? backupRuleId = null)
    {
        var item = new DownloadEntry(this, title, path, work, backupRuleId); Items.Insert(0, item); item.Start(); return item;
    }
    public void Remove(DownloadEntry item) { if (item.State is DownloadState.Waiting or DownloadState.Running or DownloadState.Paused or DownloadState.Canceling) throw new InvalidOperationException("请先取消下载。"); Items.Remove(item); SaveHistory(); }
    internal void SaveHistory()
    {
        if (!historyReadable) return;
        var temp = historyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!); File.WriteAllText(temp, JsonSerializer.Serialize(Items.Where(x => x.State == DownloadState.Completed).Select(x => new HistoryItem(x.Title, x.Destination)))); File.Move(temp, historyPath, true); HistoryWarning = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { HistoryWarning = "下载历史保存失败，请检查目录权限。"; }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
