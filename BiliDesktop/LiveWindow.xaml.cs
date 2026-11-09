using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using BililiveRecorder.Core.Config;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.IO;

namespace BiliDesktop;

public partial class LiveWindow : Window
{
    private readonly LiveManager manager;
    private readonly Action<string> openDirectory;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ObservableCollection<LiveRoomRow> roomRows = [];
    private string? shownRecorderError;
    private bool roomBusy;
    private int roomActionVersion;
    public LiveWindow(LiveManager manager) : this(manager, null) { }
    internal LiveWindow(LiveManager manager, Action<string>? openDirectory)
    {
        this.manager = manager; this.openDirectory = openDirectory ?? (path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
        InitializeComponent(); DirectoryBox.Text = manager.DirectoryPath; Rooms.ItemsSource = roomRows;
        SelectColumn.Header = Localizer.Translate("选择");
        LiveStatusColumn.Header = Localizer.Translate("直播状态"); RecordingStatusColumn.Header = Localizer.Translate("录制状态");
        BandwidthColumn.Header = Localizer.Translate("实时带宽"); RatioColumn.Header = Localizer.Translate("录制速度");
        OpenDirectoryButton.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty, Localizer.Key("打开录制目录"));
        SetupOptions();
        timer.Tick += (_, _) => RefreshRooms(); timer.Start(); RefreshRooms();
        Closed += (_, _) => timer.Stop();
    }
    private void RefreshRooms()
    {
        try
        {
            var rooms = manager.Rooms();
            foreach (var row in roomRows.Where(row => rooms.All(room => room.Id != row.Id)).ToArray()) roomRows.Remove(row);
            foreach (var room in rooms)
            {
                var row = roomRows.FirstOrDefault(item => item.Id == room.Id);
                if (row is null) roomRows.Add(new LiveRoomRow(room)); else row.Update(room);
            }
            if (manager.LastError is string error) { shownRecorderError = error; Status.Text = error; }
            else if (shownRecorderError is string previous) { if (Status.Text == previous || Status.Text == Localizer.Dynamic(previous)) Status.Text = ""; shownRecorderError = null; }
        }
        catch (Exception ex) { Status.Text = "刷新失败：" + ex.Message; }
    }
    private void Directory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择录制目录", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        try { manager.SetDirectory(dialog.FolderName); DirectoryBox.Text = manager.DirectoryPath; Status.Text = "录制目录已更新"; }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void OpenDirectory_Click(object sender, RoutedEventArgs e)
    {
        try { var path = manager.DirectoryPath; if (!Directory.Exists(path)) throw new DirectoryNotFoundException(Localizer.Translate("录制目录不存在。")); openDirectory(path); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (roomBusy) return;
        SetRoomBusy(true);
        try { Status.Text = "正在获取直播间信息…"; var id = await manager.AddRoomAsync(Input.Text); Input.Clear(); RefreshRooms(); Rooms.SelectedItem = roomRows.FirstOrDefault(r => r.Id == id); Status.Text = "直播间已添加"; }
        catch (Exception ex) { RefreshRooms(); Status.Text = ex.Message; }
        finally { SetRoomBusy(false); }
    }
    private void Input_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        if (!roomBusy) Add_Click(sender, e);
    }
    private void SetRoomBusy(bool value) { roomBusy = value; Input.IsEnabled = !value; AddRoomButton.IsEnabled = !value; RefreshRoomButton.IsEnabled = !value; StartRoomButton.IsEnabled = !value; AutoButton.IsEnabled = !value; RemoveRoomButton.IsEnabled = !value; }
    private Guid[] SelectedIds() => roomRows.Where(row => row.IsChecked).Select(row => row.Id).ToArray() is { Length: > 0 } checkedIds ? checkedIds : Rooms.SelectedItem is LiveRoomRow row ? [row.Id] : [];
    private void WithSelected(Action<Guid> action, string success, bool allowBusy = false)
    {
        if (roomBusy && !allowBusy) return;
        var ids = SelectedIds(); if (ids.Length == 0) { Status.Text = "请选择直播间。"; return; }
        var errors = new List<string>(); foreach (var id in ids) { try { action(id); } catch (Exception ex) { errors.Add(ex.Message); } }
        RefreshRooms(); Status.Text = errors.Count == 0 ? success : string.Join("；", errors.Distinct());
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (roomBusy) return;
        var ids = SelectedIds(); if (ids.Length == 0) { Status.Text = "请选择直播间。"; return; }
        var version = ++roomActionVersion;
        SetRoomBusy(true);
        try { Status.Text = "正在获取直播间信息…"; var errors = new List<string>(); foreach (var id in ids) { if (version != roomActionVersion) break; try { await manager.StartAsync(id, () => version == roomActionVersion); } catch (Exception ex) { errors.Add(ex.Message); } } RefreshRooms(); if (version == roomActionVersion) Status.Text = errors.Count == 0 ? "已请求开始录制" : string.Join("；", errors.Distinct()); }
        finally { SetRoomBusy(false); }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (roomBusy) return;
        var ids = SelectedIds(); if (ids.Length == 0) { Status.Text = "请选择直播间。"; return; }
        var version = roomActionVersion;
        SetRoomBusy(true);
        try { Status.Text = "正在获取直播间信息…"; var errors = new List<string>(); foreach (var id in ids) { try { await manager.RefreshAsync(id); } catch (Exception ex) { errors.Add(ex.Message); } } RefreshRooms(); if (version == roomActionVersion) Status.Text = errors.Count == 0 ? "已请求刷新直播间状态" : "刷新失败：" + string.Join("；", errors.Distinct()); }
        finally { SetRoomBusy(false); }
    }
    private void Stop_Click(object sender, RoutedEventArgs e) { if (SelectedIds().Length == 0) { Status.Text = "请选择直播间。"; return; } ++roomActionVersion; WithSelected(manager.Stop, "已请求停止录制", allowBusy: true); }
    private void Auto_Click(object sender, RoutedEventArgs e) => WithSelected(manager.ToggleAuto, "自动录制设置已更新");
    private void Remove_Click(object sender, RoutedEventArgs e) => WithSelected(manager.RemoveRoom, "直播间已移除");
    private void Events_Click(object sender, RoutedEventArgs e)
    {
        try { new LiveEventsWindow(manager.ReadEvents()).Show(); }
        catch (Exception ex) { Status.Text = "读取录制记录失败：" + ex.Message; }
    }
    private sealed record Choice(string Label, string Value) { public override string ToString() => Label; }
    private void Mode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ModeDescription is null || ModeBox.SelectedItem is not Choice mode) return;
        ModeDescription.Text = Localizer.Translate(mode.Value == "RawData"
            ? "原始数据模式：直接保存直播流，不进行修复，也不支持分段；适合保留原始数据以便排查问题。"
            : "标准模式：处理并尝试修复直播流，支持分段；适合日常录制。遇到异常时仍可能产生片段。");
    }
    private void SetupOptions()
    {
        ModeLabel.Text = Localizer.Translate("录制模式"); QualityLabel.Text = Localizer.Translate("画质预设"); CodecLabel.Text = Localizer.Translate("编码预设");
        PriorityLabel.Text = Localizer.Translate("画质优先级"); CutLabel.Text = Localizer.Translate("分段");
        TitleCutBox.Content = Localizer.Translate("标题变化时分段"); DanmakuBox.Content = Localizer.Translate("保存弹幕"); CoverBox.Content = Localizer.Translate("保存直播封面"); SaveOptionsButton.Content = Localizer.Translate("保存录制设置");
        ModeBox.ItemsSource = new[] { new Choice(Localizer.Translate("标准模式"), "Standard"), new Choice(Localizer.Translate("原始数据"), "RawData") };
        QualityBox.ItemsSource = new[] { new Choice(Localizer.Translate("最高可用"), "30000"), new Choice("Dolby · 30000", "30000"), new Choice("4K · 20000", "20000"), new Choice(Localizer.Translate("原画") + " · 10000", "10000"), new Choice(Localizer.Translate("蓝光") + " · 400", "400"), new Choice(Localizer.Translate("超清") + " · 250", "250"), new Choice(Localizer.Translate("高清") + " · 150", "150") };
        CodecBox.ItemsSource = new[] { new Choice(Localizer.Translate("AVC 优先"), "avc"), new Choice(Localizer.Translate("HEVC 优先"), "hevc"), new Choice(Localizer.Translate("仅 AVC"), "avc-only"), new Choice(Localizer.Translate("仅 HEVC"), "hevc-only") };
        CutBox.ItemsSource = new[] { new Choice(Localizer.Translate("不分段"), "Disabled"), new Choice(Localizer.Translate("按时间（分钟）"), "ByTime"), new Choice(Localizer.Translate("按大小（MB）"), "BySize") };
        var options = manager.Options; ModeBox.SelectedIndex = options.Mode == RecordMode.RawData ? 1 : 0; CutBox.SelectedIndex = options.Cut switch { CuttingMode.ByTime => 1, CuttingMode.BySize => 2, _ => 0 };
        var normalized = string.Join(",", options.Quality.Split(',').Select(x => x.Trim()));
        var match = (from quality in QualityBox.Items.Cast<Choice>().Select((item, index) => (item, index))
                     from codec in CodecBox.Items.Cast<Choice>().Select((item, index) => (item, index))
                     where string.Equals(BuildPriority(quality.item.Value, codec.item.Value), normalized, StringComparison.OrdinalIgnoreCase)
                     select (quality.index, CodecIndex: codec.index)).FirstOrDefault((-1, -1));
        QualityBox.SelectedIndex = match.Item1; CodecBox.SelectedIndex = match.Item2;
        PriorityBox.Text = options.Quality; CutAmountBox.Text = options.CutAmount.ToString(); TitleCutBox.IsChecked = options.CutOnTitle; DanmakuBox.IsChecked = options.Danmaku; CoverBox.IsChecked = options.SaveCover;
    }
    private void Preset_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PriorityBox is null || QualityBox.SelectedItem is not Choice quality || CodecBox.SelectedItem is not Choice codec) return;
        PriorityBox.Text = BuildPriority(quality.Value, codec.Value);
    }
    private static string BuildPriority(string quality, string codec)
    {
        var levels = new[] { 30000, 20000, 10000, 400, 250, 150 }.Where(q => q <= int.Parse(quality));
        var formats = codec switch { "hevc" => new[] { "hevc", "avc" }, "avc-only" => new[] { "avc" }, "hevc-only" => new[] { "hevc" }, _ => new[] { "avc", "hevc" } };
        return string.Join(",", levels.SelectMany(level => formats.Select(format => format + level)));
    }
    private void SaveOptions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ModeBox.SelectedItem is not Choice mode || CutBox.SelectedItem is not Choice cut || !uint.TryParse(CutAmountBox.Text, out var amount)) throw new ArgumentException("录制设置无效，请检查画质优先级和分段数值。");
            manager.SetOptions(new LiveRecordingOptions(Enum.Parse<RecordMode>(mode.Value), PriorityBox.Text.Trim(), Enum.Parse<CuttingMode>(cut.Value), amount, TitleCutBox.IsChecked == true, DanmakuBox.IsChecked == true, CoverBox.IsChecked == true));
            Status.Text = "录制设置已保存";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
}

internal sealed class LiveRoomRow(LiveRoomState state) : INotifyPropertyChanged
{
    private LiveRoomState current = state;
    private bool isChecked;
    public Guid Id => current.Id;
    public int RoomId => current.RoomId;
    public string Name => current.Name;
    public string Title => current.Title;
    public bool Streaming => current.Streaming;
    public bool Recording => current.Recording;
    public bool AutoRecord => current.AutoRecord;
    public string LiveStatus => Localizer.Translate(Streaming ? "直播中" : "未开播");
    public string RecordingStatus => Localizer.Translate(Recording ? "录制中" : "未录制");
    public string AutoRecordStatus => Localizer.Translate(AutoRecord ? "已启用" : "已关闭");
    public string BandwidthText => Recording && current.NetworkMibps is double value && double.IsFinite(value) && value >= 0 ? $"{value:F2} Mib/s" : "—";
    public string RatioText => Recording && current.DurationRatio is double value && double.IsFinite(value) && value >= 0 && value <= double.MaxValue / 100 ? $"{value * 100:F0}%" : "—";
    public string RatioHelp => Localizer.Translate("录制速度是收到的直播时长与实际经过时间的比例；100% 表示跟上直播，原始数据模式不提供此统计。");
    public bool IsChecked { get => isChecked; set { if (isChecked == value) return; isChecked = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void Update(LiveRoomState state) { if (current == state) return; current = state; Changed(""); }
}
