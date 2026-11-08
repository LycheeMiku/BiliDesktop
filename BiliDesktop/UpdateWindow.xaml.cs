using System.Diagnostics;
using System.Windows;

namespace BiliDesktop;

public partial class UpdateWindow : Window
{
    private readonly Action<Uri> openLink;
    private AvailableRelease? release;
    private string? error;
    public UpdateWindow(AvailableRelease? release, string? error = null) : this(release, error, null) { }
    internal UpdateWindow(AvailableRelease? release, string? error, Action<Uri>? openLink)
    {
        this.openLink = openLink ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        InitializeComponent();
        Localizer.LanguageChanged += RefreshText;
        Closed += (_, _) => Localizer.LanguageChanged -= RefreshText;
        SetResult(release, error);
    }
    internal void SetResult(AvailableRelease? value, string? failure)
    {
        release = value; error = failure; LinkStatus.Text = ""; RefreshText();
    }
    private void RefreshText()
    {
        var available = release is not null;
        Title = Localizer.Translate(available ? "发现新版本" : "更新检查失败"); Heading.Text = Title;
        VersionInfo.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        GithubButton.Visibility = AlternateButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        LaterButton.Content = Localizer.Translate(available ? "暂不更新" : "关闭");
        GithubButton.Content = Localizer.Translate("前往 GitHub Release"); AlternateButton.Content = Localizer.Translate("备用下载");
        if (release is { } item)
        {
            CurrentVersion.Text = Localizer.Translate("当前版本") + "：" + AppVersion.Current;
            NewVersion.Text = Localizer.Translate("新版本") + "：" + item.Version;
            Published.Text = Localizer.Translate("发布日期") + "：" + item.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            NotesLabel.Text = Localizer.Translate("发布说明");
            Notes.Text = string.IsNullOrWhiteSpace(item.Notes) ? Localizer.Translate("未提供发布说明。") : item.Notes;
        }
        else Notes.Text = Localizer.Translate(error ?? "更新检查失败，请检查网络连接后重试。");
    }
    private void Later_Click(object sender, RoutedEventArgs e) => Close();
    private void Github_Click(object sender, RoutedEventArgs e) { if (release is not null) Open(release.GithubUrl); }
    private void Alternate_Click(object sender, RoutedEventArgs e) => Open(AvailableRelease.AlternateUrl);
    private void Open(Uri uri)
    {
        try { openLink(uri); Close(); }
        catch (Exception ex) { LinkStatus.Text = Localizer.Translate("无法打开链接：") + ex.Message; }
    }
}
