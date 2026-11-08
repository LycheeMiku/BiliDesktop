using System.Windows.Controls;

namespace BiliDesktop;

public partial class AudioOptionsControl : UserControl
{
    public AudioOptionsControl()
    {
        InitializeComponent();
        Group.SetResourceReference(HeaderedContentControl.HeaderProperty, Localizer.Key("音频封面与元数据"));
        Cover.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("嵌入视频封面"));
        Artist.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("UP 主作为歌手"));
        Title.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("视频或分 P 名称作为歌曲名"));
        Album.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("合集或视频标题作为专辑名"));
    }
    public AudioMetadataOptions Options => new(Cover.IsChecked == true, Artist.IsChecked == true, Title.IsChecked == true, Album.IsChecked == true);
}
