using System.IO;
using System.Text.RegularExpressions;

namespace BiliDesktop;

public static class DownloadNaming
{
    public static string SafeComponent(string text)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var clean = new string(text.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length > 100) clean = clean[..100].TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(clean) || clean is "." or "..") clean = "video";
        if (Regex.IsMatch(clean, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) clean = "_" + clean;
        return clean;
    }
    public static bool NeedsFolder(VideoInfo video) => video.Pages.Count > 1 || video.Pages.Any(x => x.EpisodeId.HasValue);
    public static string Folder(string root, VideoInfo video) => NeedsFolder(video) ? Path.Combine(root, SafeComponent(video.Title + " [" + video.Bvid + "]")) : root;
    public static string AudioTitle(VideoInfo video, VideoPage? page, string? collectionItemTitle = null)
    {
        if (page is not null && NeedsFolder(video)) return Regex.Replace(page.Label, @"^P\d+\s*·\s*", "", RegexOptions.CultureInvariant).Trim();
        return !string.IsNullOrWhiteSpace(video.CollectionItemTitle) ? video.CollectionItemTitle : !string.IsNullOrWhiteSpace(collectionItemTitle) ? collectionItemTitle : video.Title;
    }
    public static string AudioBaseName(VideoInfo video, VideoPage? page, string? collectionItemTitle = null) => SafeComponent(AudioTitle(video, page, collectionItemTitle));
    public static string BaseName(VideoInfo video, VideoPage? page, int pageNumber, VideoStream? stream, AudioStream? audio)
    {
        var title = NeedsFolder(video) && page is not null ? $"{pageNumber:D3}-{page.Label}" : video.Title;
        var quality = stream is null ? audio is null ? "" : "-" + StreamLabels.Audio(audio.Quality) : "-" + StreamLabels.Video(stream.Quality) + "-" + stream.Codec;
        return SafeComponent(title + quality);
    }
}
