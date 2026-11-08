using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace BiliDesktop;

public sealed class SearchResultItem(SearchVideo video) : INotifyPropertyChanged
{
    private BitmapSource? preview;
    public SearchVideo Video { get; } = video;
    public string Info => $"{Video.Author}  ·  {Video.Duration}";
    public string ViewsLabel => $"{Localizer.Translate("播放量")}  {(Video.Views.HasValue ? Video.Views.Value.ToString("N0") : "—")}";
    public BitmapSource? Preview { get => preview; set { preview = value; PropertyChanged?.Invoke(this, new(nameof(Preview))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class SearchWindow : Window
{
    private readonly BiliApi api;
    private string currentKeyword = "";
    private string currentOrder = "totalrank";
    private int page;
    private int pageCount;
    private int version;
    private int detailVersion;
    private bool closed;
    private CancellationTokenSource? coverCancellation;
    private CancellationTokenSource? detailCancellation;
    private long? selectedAuthorId;
    private IReadOnlyList<SearchResultItem> currentItems = [];
    public string? SelectedBvid { get; private set; }
    public event Action<string>? VideoSelected;

    public SearchWindow(BiliApi api)
    {
        this.api = api; InitializeComponent();
        DetailsGroup.SetResourceReference(HeaderedContentControl.HeaderProperty, Localizer.Key("视频详情"));
        DetailState.Text = Localizer.Translate("选择视频查看详情");
        Closed += (_, _) => { closed = true; ++version; ClearPage(); };
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        currentKeyword = Keyword.Text.Trim();
        currentOrder = Order.SelectedIndex switch { 1 => "pubdate", 2 => "click", 3 => "stow", _ => "totalrank" };
        await SearchAsync(1);
    }
    private void Keyword_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Search_Click(sender, e); } }
    private async void Previous_Click(object sender, RoutedEventArgs e) => await SearchAsync(page - 1);
    private async void Next_Click(object sender, RoutedEventArgs e) => await SearchAsync(page + 1);
    private void ClearDetails()
    {
        ++detailVersion; detailCancellation?.Cancel(); detailCancellation = null;
        selectedAuthorId = null; AuthorName.Text = ""; AuthorLink.IsEnabled = false; DetailAuthor.Visibility = Visibility.Collapsed;
        DetailTitle.Text = ""; DetailMeta.Text = ""; DetailStats.Text = ""; DetailDescription.Text = ""; DetailState.Text = Localizer.Translate("选择视频查看详情");
    }
    private void ClearPage()
    {
        coverCancellation?.Cancel(); coverCancellation = null;
        foreach (var item in currentItems) item.Preview = null;
        currentItems = []; Results.ItemsSource = null; ClearDetails();
    }
    internal async Task SearchAsync(int requested)
    {
        var request = ++version;
        ClearPage(); Previous.IsEnabled = false; Next.IsEnabled = false; State.Text = Localizer.Translate("正在搜索…");
        try
        {
            var result = await api.SearchVideosAsync(currentKeyword, requested, currentOrder);
            if (closed || request != version) return;
            page = result.Page; pageCount = result.PageCount;
            currentItems = result.Items.Select(item => new SearchResultItem(item)).ToArray(); Results.ItemsSource = currentItems;
            Previous.IsEnabled = page > 1; Next.IsEnabled = page < pageCount;
            PageLabel.Text = pageCount > 0 ? string.Format(Localizer.Translate("第 {0} / {1} 页"), page, pageCount) : Localizer.Translate("无结果");
            State.Text = string.Format(Localizer.Translate("本页 {0} 条"), result.Items.Count);
            var source = new CancellationTokenSource(); coverCancellation = source;
            _ = LoadCoversAsync(currentItems, request, source);
        }
        catch (Exception ex) { if (!closed && request == version) { State.Text = Localizer.Dynamic("搜索失败：" + ex.Message); PageLabel.Text = ""; } }
    }
    private async Task LoadCoversAsync(IReadOnlyList<SearchResultItem> items, int request, CancellationTokenSource source)
    {
        using var slots = new SemaphoreSlim(4, 4);
        async Task LoadAsync(SearchResultItem item)
        {
            if (string.IsNullOrWhiteSpace(item.Video.CoverUrl)) return;
            try
            {
                await slots.WaitAsync(source.Token);
                try
                {
                    var bytes = await api.GetCoverPreviewAsync(item.Video.CoverUrl, source.Token);
                    if (closed || request != version || source.IsCancellationRequested) return;
                    using var stream = new MemoryStream(bytes);
                    var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 288; image.StreamSource = stream; image.EndInit(); image.Freeze();
                    if (!closed && request == version && !source.IsCancellationRequested) item.Preview = image;
                }
                finally { slots.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* A missing or invalid cover must not block search. */ }
        }
        try { await Task.WhenAll(items.Select(LoadAsync)); }
        finally { if (ReferenceEquals(coverCancellation, source)) coverCancellation = null; source.Dispose(); }
    }
    private async void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ClearDetails();
        if (Results.SelectedItem is not SearchResultItem item || closed) return;
        DetailTitle.Text = item.Video.Title;
        SetAuthor(item.Video.Author, item.Video.AuthorId);
        DetailMeta.Text = $"{item.Video.Bvid}  ·  {item.Video.Duration}";
        DetailStats.Text = item.ViewsLabel;
        DetailDescription.Text = item.Video.Description;
        DetailState.Text = Localizer.Translate("正在读取视频详情…");
        var request = detailVersion; var searchRequest = version; var session = api.SessionVersion;
        var source = new CancellationTokenSource(); detailCancellation = source;
        try
        {
            var details = await api.GetSearchVideoDetailsAsync(item.Video.Bvid, source.Token);
            if (closed || request != detailVersion || searchRequest != version || source.IsCancellationRequested || api.SessionVersion != session || !ReferenceEquals(Results.SelectedItem, item)) return;
            RenderDetails(details, item.Video);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed && request == detailVersion && searchRequest == version) DetailState.Text = Localizer.Translate("详情读取失败：") + ex.Message; }
        finally { if (ReferenceEquals(detailCancellation, source)) detailCancellation = null; source.Dispose(); }
    }
    private void RenderDetails(SearchVideoDetails d, SearchVideo fallback)
    {
        static string L(string name) => Localizer.Translate(name);
        static string Stat(string name, long? count) => $"{L(name)} {(count.HasValue ? count.Value.ToString("N0") : "—")}";
        var published = d.PublishUnix is > 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(d.PublishUnix.Value).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";
        var duration = fallback.Duration;
        if (d.DurationSeconds is >= 0 and <= 3155760000) { var time = TimeSpan.FromSeconds(d.DurationSeconds.Value); duration = time.TotalHours >= 1 ? $"{(long)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}" : $"{time.Minutes:D2}:{time.Seconds:D2}"; }
        DetailTitle.Text = string.IsNullOrEmpty(d.Title) ? fallback.Title : d.Title;
        SetAuthor(string.IsNullOrEmpty(d.Author) ? fallback.Author : d.Author, d.AuthorId is > 0 ? d.AuthorId : fallback.AuthorId);
        DetailMeta.Text = $"{d.Bvid}\n{L("发布时间")}  {published}\n{L("时长")}  {duration}" + (string.IsNullOrEmpty(d.Category) ? "" : $"\n{L("分区")}  {d.Category}");
        DetailStats.Text = string.Join("  ·  ", new[] { Stat("播放量", d.Views ?? fallback.Views), Stat("点赞", d.Likes), Stat("投币", d.Coins), Stat("收藏数", d.Favorites), Stat("评论数", d.Replies), Stat("弹幕数", d.Danmaku), Stat("分享数", d.Shares) });
        DetailDescription.Text = L("视频简介") + "\n" + (string.IsNullOrWhiteSpace(d.Description) ? "—" : d.Description);
        DetailState.Text = "";
    }
    private void SetAuthor(string name, long? id)
    {
        selectedAuthorId = id is > 0 ? id : null;
        AuthorName.Text = string.IsNullOrWhiteSpace(name) ? "—" : name;
        DetailAuthor.Visibility = Visibility.Visible; AuthorLink.IsEnabled = selectedAuthorId.HasValue;
        AuthorLink.ToolTip = Localizer.Translate(selectedAuthorId.HasValue ? "查询该 UP 主的投稿" : "无法获取 UP 主 UID");
    }
    private void AuthorLink_Click(object sender, RoutedEventArgs e)
    {
        if (selectedAuthorId is not long id || id <= 0 || Results.SelectedItem is not SearchResultItem) return;
        new UploaderWindow(api, id, autoFetch: true).Show();
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is not SearchResultItem result) return;
        SelectedBvid = result.Video.Bvid; VideoSelected?.Invoke(SelectedBvid); Close();
    }
}
