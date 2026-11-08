using System.IO;

namespace BiliDesktop;

public enum BatchFormat { Automatic, Mp4, Matroska }

public static class BatchPolicy
{
    public static bool NeedsMatroska(AudioStream audio) => audio.Quality == 30251 || audio.Codec.StartsWith("flac", StringComparison.OrdinalIgnoreCase);
    public static bool ChooseMatroska(BatchFormat format, AudioStream? audio) => format switch
    {
        BatchFormat.Automatic => audio is not null && NeedsMatroska(audio),
        BatchFormat.Mp4 => false,
        BatchFormat.Matroska => true,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
    public static bool OutputAlreadyExists(BatchFormat format, string mp4Path, string mkvPath) => format switch
    {
        BatchFormat.Automatic => File.Exists(mp4Path) || File.Exists(mkvPath),
        BatchFormat.Mp4 => File.Exists(mp4Path),
        BatchFormat.Matroska => File.Exists(mkvPath),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public static string FileName(string bvid, int number, string label, bool audioOnly, bool matroska = false)
    {
        var course = System.Text.RegularExpressions.Regex.IsMatch(bvid, @"^course-ss[1-9][0-9]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if ((!course && BiliApi.ExtractBvid(bvid) != bvid) || number < 1) throw new ArgumentException("视频标识或分 P 序号无效。");
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var clean = new string(label.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length > 100) clean = clean[..100].TrimEnd(' ', '.');
        return $"{bvid}_P{number:D3}_{clean}{(matroska ? ".mkv" : audioOnly ? ".m4a" : ".mp4")}";
    }

    public static VideoStream? ChooseVideo(IReadOnlyList<VideoStream> streams, VideoStream? preferred)
    {
        if (preferred is not null)
        {
            var exact = streams.FirstOrDefault(v => v.Quality == preferred.Quality && v.Codec == preferred.Codec);
            if (exact is not null) return exact;
            var lower = streams.Where(v => v.Quality <= preferred.Quality).OrderByDescending(v => v.Quality).ThenByDescending(v => v.Bandwidth).FirstOrDefault();
            if (lower is not null) return lower;
        }
        return streams.OrderByDescending(v => v.Quality).ThenByDescending(v => v.Bandwidth).FirstOrDefault();
    }

    public static AudioStream? ChooseAudio(IReadOnlyList<AudioStream> streams, AudioStream? preferred)
    {
        if (preferred is not null)
        {
            var exact = streams.FirstOrDefault(a => a.Quality == preferred.Quality && a.Codec == preferred.Codec);
            if (exact is not null) return exact;
            var lower = streams.Where(a => a.Bandwidth <= preferred.Bandwidth).OrderByDescending(a => a.Bandwidth).FirstOrDefault();
            if (lower is not null) return lower;
        }
        return streams.OrderByDescending(a => a.Bandwidth).FirstOrDefault();
    }
}
