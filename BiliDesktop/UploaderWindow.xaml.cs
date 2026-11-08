using System.Windows;

namespace BiliDesktop;

public partial class UploaderWindow : Window
{
    private readonly BiliApi api;
    private CancellationTokenSource? cancellation;
    private bool closed;
    public UploaderWindow(BiliApi api, long? initialMid, bool autoFetch = false)
    {
        this.api = api; InitializeComponent();
        if (initialMid is > 0) Input.Text = initialMid.Value.ToString();
        if (autoFetch && initialMid is > 0) Loaded += (_, _) => Fetch_Click(this, new RoutedEventArgs());
        Closed += (_, _) => { closed = true; cancellation?.Cancel(); };
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { cancellation?.Cancel(); Cancel.IsEnabled = false; Status.Text = "正在停止查询…"; }

    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        if (!BiliApi.TryParseUploader(Input.Text, out var mid)) { Status.Text = "请输入有效的 UID 或 UP 主主页链接。"; return; }
        using var token = new CancellationTokenSource();
        cancellation = token; Fetch.IsEnabled = false; Input.IsEnabled = false; Cancel.IsEnabled = true; Status.Text = "正在查询…";
        try
        {
            var items = await api.GetUploaderVideosAsync(mid, new Progress<int>(n => { if (!closed && !token.IsCancellationRequested) Status.Text = $"已读取 {n} 个视频…"; }), token.Token);
            if (closed || token.IsCancellationRequested) return;
            if (items.Count == 0) { Status.Text = "没有可访问的投稿视频。"; return; }
            Status.Text = $"共读取 {items.Count} 个视频";
            new BatchWindow(api, null, items, null, null) { Title = Localizer.Translate("UP 主批量下载") }.Show();
        }
        catch (OperationCanceledException) { if (!closed) Status.Text = "已停止查询。"; }
        catch (Exception ex) { if (!closed) Status.Text = "查询失败：" + ex.Message; }
        finally { cancellation = null; if (!closed) { Fetch.IsEnabled = true; Input.IsEnabled = true; Cancel.IsEnabled = false; } }
    }
}
