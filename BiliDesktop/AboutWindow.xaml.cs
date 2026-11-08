using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace BiliDesktop;

public partial class AboutWindow : Window
{
    private readonly Action<Uri> openLink;
    public AboutWindow() : this(null) { }
    internal AboutWindow(Action<Uri>? openLink)
    {
        this.openLink = openLink ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        InitializeComponent();
        VersionValue.Text = "  " + AppVersion.Current;
        SetResourceReference(TitleProperty, Localizer.Key("关于"));
        VersionLabel.SetResourceReference(TextBlock.TextProperty, Localizer.Key("版本"));
        DeveloperLabel.SetResourceReference(TextBlock.TextProperty, Localizer.Key("开发者仓库"));
        ReferencesLabel.SetResourceReference(TextBlock.TextProperty, Localizer.Key("参考项目"));
        LiveLabel.SetResourceReference(TextBlock.TextProperty, Localizer.Key("直播录制"));
        VideoLabel.SetResourceReference(TextBlock.TextProperty, Localizer.Key("视频下载"));
    }
    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (e.Uri.Scheme != Uri.UriSchemeHttps || !e.Uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid link");
            openLink(e.Uri); LinkStatus.Text = "";
        }
        catch (Exception ex) { LinkStatus.Text = Localizer.Translate("无法打开链接：") + ex.Message; }
    }
}
