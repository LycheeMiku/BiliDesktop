using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BiliDesktop;

public partial class BackupWindow : Window
{
    private readonly BiliApi api;
    private readonly BackupStore? store;
    private readonly Action trigger;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private long? accountId;
    private long? foldersSession;
    private bool closed;
    private bool busy;
    private CancellationTokenSource? cancellation;

    public BackupWindow(BiliApi api, BackupStore? store, Action trigger)
    {
        this.api = api; this.store = store; this.trigger = trigger; InitializeComponent();
        AddSelected.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("自动备份该收藏夹"));
        AddAll.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("自动备份所有收藏夹"));
        AddAll.SetResourceReference(ToolTipProperty, Localizer.Key("备份当前列表中的所有收藏夹。"));
        BackupUnavailable.SetResourceReference(TextBlock.TextProperty, Localizer.Key("自动备份不可用。"));
        BackupUnavailable.Visibility = store is null ? Visibility.Visible : Visibility.Collapsed;
        try { DirectoryBox.Text = AppSettings.LoadDownloadDirectory(); } catch (Exception) { DirectoryBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos); }
        refresh.Tick += (_, _) => RefreshRules(); if (store is not null) { refresh.Start(); RefreshRules(); }
        SetBusy(false);
        Loaded += async (_, _) => await LoadFoldersAsync();
        Closed += (_, _) => { closed = true; cancellation?.Cancel(); refresh.Stop(); };
    }
    private void RefreshRules()
    {
        try { var id = (Rules.SelectedItem as BackupRule)?.Id; Rules.ItemsSource = store?.List(); if (id.HasValue && Rules.ItemsSource is IEnumerable<BackupRule> rules) Rules.SelectedItem = rules.FirstOrDefault(r => r.Id == id); }
        catch (Exception ex) { if (!closed) Status.Text = "读取规则失败：" + ex.Message; }
    }
    private void FolderKind_Changed(object sender, SelectionChangedEventArgs e) { if (IsInitialized && Folders is not null) { accountId = null; foldersSession = null; Folders.ItemsSource = null; Status.Text = ""; } }
    private void SetBusy(bool value)
    {
        busy = value; LoadFolders.IsEnabled = !value; FolderKind.IsEnabled = !value; Folders.IsEnabled = !value; Fetch.IsEnabled = !value; CancelFetch.IsEnabled = value && cancellation is not null;
        Interval.IsEnabled = !value && store is not null; BrowseDirectory.IsEnabled = !value && store is not null; AddSelected.IsEnabled = !value && store is not null; AddAll.IsEnabled = !value && store is not null; Rules.IsEnabled = store is not null; RuleActions.IsEnabled = store is not null;
    }
    private async void LoadFolders_Click(object sender, RoutedEventArgs e) => await LoadFoldersAsync();
    internal async Task LoadFoldersAsync()
    {
        if (busy) return;
        SetBusy(true); accountId = null; foldersSession = null; Folders.ItemsSource = null;
        try
        {
            Status.Text = "正在读取收藏夹…";
            var session = api.SessionVersion;
            var account = await api.GetAccountInfoAsync();
            var folders = FolderKind.SelectedIndex == 1 ? await api.GetCollectedFavoriteFoldersAsync() : await api.GetFavoriteFoldersAsync();
            if (closed) return;
            if (api.SessionVersion != session) throw new InvalidOperationException("登录会话已变化，请重新读取收藏夹。");
            accountId = account.Mid; foldersSession = session; Folders.ItemsSource = folders; Folders.SelectedIndex = folders.Count > 0 ? 0 : -1;
            Status.Text = $"{account.Name} · {folders.Count} 个收藏夹";
        }
        catch (Exception ex) { if (!closed) Status.Text = "读取失败：" + ex.Message; }
        finally { if (!closed) SetBusy(false); }
    }
    private void CancelFetch_Click(object sender, RoutedEventArgs e) { cancellation?.Cancel(); CancelFetch.IsEnabled = false; Status.Text = "正在停止读取…"; }
    private async void Fetch_Click(object sender, RoutedEventArgs e) => await FetchFolderAsync();
    internal async Task FetchFolderAsync()
    {
        if (busy || Folders.SelectedItem is not FavoriteFolder folder) { Status.Text = "请选择一个收藏夹。"; return; }
        var session = api.SessionVersion;
        if (foldersSession != session) { Status.Text = "登录会话已变化，请重新读取收藏夹。"; return; }
        using var token = new CancellationTokenSource(); cancellation = token; SetBusy(true); Status.Text = "正在读取收藏内容…";
        try
        {
            var result = await api.GetFavoriteContentsAsync(folder.Id, new Progress<int>(n => { if (!closed && !token.IsCancellationRequested) Status.Text = $"已读取 {n} 条…"; }), token.Token);
            if (closed || token.IsCancellationRequested) return;
            if (api.SessionVersion != session) throw new InvalidOperationException("登录会话已变化，请重新读取收藏夹。");
            Status.Text = $"视频 {result.Videos.Count}，失效 {result.Invalid}，暂不支持的类型 {result.Unsupported}";
            if (result.Videos.Count > 0) new BatchWindow(api, null, result.Videos, null, null) { Title = Localizer.Translate("收藏夹批量下载") }.Show();
        }
        catch (OperationCanceledException) { if (!closed) Status.Text = "已停止读取。"; }
        catch (Exception ex) { if (!closed) Status.Text = "读取失败：" + ex.Message; }
        finally { cancellation = null; if (!closed) SetBusy(false); }
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择备份目录", Multiselect = false };
        if (dialog.ShowDialog() == true) DirectoryBox.Text = dialog.FolderName;
    }
    private async void Add_Click(object sender, RoutedEventArgs e) => await AddRulesAsync(false);
    private async void AddAll_Click(object sender, RoutedEventArgs e) => await AddRulesAsync(true);
    private async Task AddRulesAsync(bool all)
    {
        try
        {
            if (store is null) throw new InvalidOperationException("自动备份不可用。");
            if (!accountId.HasValue || !foldersSession.HasValue || Folders.ItemsSource is not IEnumerable<FavoriteFolder> loaded) throw new InvalidOperationException("请先读取收藏夹。");
            var folders = all ? loaded.DistinctBy(f => f.Id).ToArray() : Folders.SelectedItem is FavoriteFolder selected ? [selected] : [];
            if (folders.Length == 0) throw new InvalidOperationException("请先读取并选择收藏夹。");
            var directory = DirectoryBox.Text;
            var minutes = Interval.SelectedIndex switch { 0 => 5, 1 => 15, 2 => 60, 3 => 360, 4 => 1440, _ => 15 };
            var expectedAccount = accountId.Value; var session = foldersSession.Value;
            if (api.SessionVersion != session) throw new InvalidOperationException("登录会话已变化，请重新读取收藏夹。");
            SetBusy(true);
            var account = await api.GetAccountInfoAsync();
            if (closed) return;
            if (api.SessionVersion != session || account.Mid != expectedAccount) throw new InvalidOperationException("登录会话已变化，请重新读取收藏夹。");
            var added = store.AddMany(account.Mid, folders, directory, minutes);
            RefreshRules(); Status.Text = $"添加规则 {added} 条，已存在 {folders.Length - added} 条"; if (added > 0) trigger();
        }
        catch (Exception ex) { if (!closed) Status.Text = "添加失败：" + ex.Message; }
        finally { if (!closed) SetBusy(false); }
    }
    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (store is null) return;
        if (Rules.SelectedItem is not BackupRule rule) { Status.Text = "请选择规则。"; return; }
        try { if (!rule.Enabled) { Status.Text = "请先启用规则。"; return; } if (rule.Status.StartsWith("正在", StringComparison.Ordinal)) { Status.Text = "规则正在检查。"; return; } store.RunNow(rule.Id); RefreshRules(); trigger(); Status.Text = "已安排检查"; }
        catch (Exception ex) { Status.Text = "操作失败：" + ex.Message; }
    }
    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (store is null) return;
        if (Rules.SelectedItem is not BackupRule rule) { Status.Text = "请选择规则。"; return; }
        try { store.SetEnabled(rule.Id, !rule.Enabled); RefreshRules(); if (!rule.Enabled) trigger(); Status.Text = rule.Enabled ? "已暂停；正在下载的文件会完成。" : "规则已启用"; }
        catch (Exception ex) { Status.Text = "操作失败：" + ex.Message; }
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (store is null) return;
        if (Rules.SelectedItem is not BackupRule rule) { Status.Text = "请选择规则。"; return; }
        try { store.Remove(rule.Id); RefreshRules(); Status.Text = "规则已删除；正在下载的文件会完成。"; }
        catch (Exception ex) { Status.Text = "操作失败：" + ex.Message; }
    }
}
