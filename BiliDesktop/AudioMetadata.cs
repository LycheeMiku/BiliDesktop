namespace BiliDesktop;

public record AudioMetadataOptions(bool EmbedCover = false, bool Artist = false, bool Title = false, bool Album = false)
{
    public bool Enabled => EmbedCover || Artist || Title || Album;
    public AudioMetadata? ForVideo(VideoInfo video, VideoPage? page, int? collectionNumber = null, string? collectionTitle = null, string? collectionItemTitle = null)
    {
        var multipart = DownloadNaming.NeedsFolder(video);
        int? number = null;
        if (multipart && page is not null)
        {
            for (var index = 0; index < video.Pages.Count; index++) if (video.Pages[index].Cid == page.Cid) { number = index + 1; break; }
        }
        else if (collectionNumber is > 0 || video.CollectionNumber is > 0) number = collectionNumber is > 0 ? collectionNumber : video.CollectionNumber;
        if (!Enabled && number is null) return null;
        var trackTitle = DownloadNaming.AudioTitle(video, page, collectionItemTitle);
        if (Title && string.IsNullOrWhiteSpace(trackTitle)) throw new InvalidOperationException("无法获取视频标题，不能写入歌曲名。");
        string? cover = null;
        if (EmbedCover)
        {
            var raw = video.CoverUrl.StartsWith("//", StringComparison.Ordinal) ? "https:" + video.CoverUrl : video.CoverUrl;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) throw new InvalidOperationException("无法获取视频封面，不能嵌入音频。");
            cover = uri.Scheme == "http" ? new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri.AbsoluteUri : uri.AbsoluteUri;
        }
        var album = !string.IsNullOrWhiteSpace(collectionTitle) ? collectionTitle : !string.IsNullOrWhiteSpace(video.CollectionTitle) ? video.CollectionTitle : video.Title;
        return new(cover, Artist && video.OwnerId is > 0 && !string.IsNullOrWhiteSpace(video.Owner) ? video.Owner : null, Title ? trackTitle : null, Album && !string.IsNullOrWhiteSpace(album) ? album : null, number);
    }
}
public record AudioMetadata(string? CoverUrl, string? Artist, string? Title, string? Album, int? TrackNumber = null);
