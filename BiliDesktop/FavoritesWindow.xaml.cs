using System.Windows;
using System.Windows.Controls;

namespace BiliDesktop;

public partial class FavoritesWindow : Window
{
    private readonly BiliApi api;
    private CancellationTokenSource? cancellation;
    private bool closed;
    private bool busy;
    public FavoritesWindow(BiliApi api)
    {
        this.api = api; InitializeComponent();
        Loaded += async (_, _) => await LoadFoldersAsync();
        Closed += (_, _) => { closed = true; cancellation?.Cancel(); };
    }
    private void SetBusy(bool value)
    {
        busy = value; Refresh.IsEnabled = !value; FolderKind.IsEnabled = !value; Fetch.IsEnabled = !value; Folders.IsEnabled = !value; Cancel.IsEnabled = value && cancellation is not null;
    }
    private void FolderKind_Changed(object sender, SelectionChangedEventArgs e) { if (IsInitialized && Folders is not null) { Folders.ItemsSource = null; Status.Text = ""; } }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadFoldersAsync();
    private async Task LoadFoldersAsync()
    {
        if (busy) return;
        SetBusy(true); Status.Text = "正在读取收藏夹…";
        try
        {
            var session = api.SessionVersion;
            var folders = FolderKind.SelectedIndex == 1 ? await api.GetCollectedFavoriteFoldersAsync() : await api.GetFavoriteFoldersAsync();
            if (closed) return;
            if (session != api.SessionVersion) throw new InvalidOperationException("登录会话已变化，请重新读取收藏夹。");
            Folders.ItemsSource = folders; Status.Text = $"共 {folders.Count} 个收藏夹";
        }
        catch (Exception ex) { if (!closed) Status.Text = "读取失败：" + ex.Message; }
        finally { if (!closed) SetBusy(false); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { cancellation?.Cancel(); Cancel.IsEnabled = false; Status.Text = "正在停止读取…"; }
    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        if (busy || Folders.SelectedItem is not FavoriteFolder folder) { Status.Text = "请选择一个收藏夹。"; return; }
        using var token = new CancellationTokenSource(); cancellation = token; SetBusy(true); Status.Text = "正在读取收藏内容…";
        try
        {
            var result = await api.GetFavoriteContentsAsync(folder.Id, new Progress<int>(n => { if (!closed && !token.IsCancellationRequested) Status.Text = $"已读取 {n} 条…"; }), token.Token);
            if (closed || token.IsCancellationRequested) return;
            Status.Text = $"视频 {result.Videos.Count}，失效 {result.Invalid}，暂不支持的类型 {result.Unsupported}";
            if (result.Videos.Count == 0) return;
            new BatchWindow(api, null, result.Videos, null, null) { Title = Localizer.Translate("收藏夹批量下载") }.Show();
        }
        catch (OperationCanceledException) { if (!closed) Status.Text = "已停止读取。"; }
        catch (Exception ex) { if (!closed) Status.Text = "读取失败：" + ex.Message; }
        finally { cancellation = null; if (!closed) SetBusy(false); }
    }
}
