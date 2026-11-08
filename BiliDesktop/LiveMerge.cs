using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace BiliDesktop;

public static class LiveMerge
{
    private record ProbeResult(string Signature, double Duration);
    private static async Task<ProbeResult> ProbeAsync(string path)
    {
        var json = await MediaDownload.RunAsync("ffprobe", ["-v", "error", "-show_streams", "-show_format", "-show_data_hash", "sha256", "-of", "json", path]);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var properties = new[] { "codec_type", "codec_name", "profile", "width", "height", "pix_fmt", "sample_rate", "channels", "channel_layout", "time_base", "extradata_hash" };
        var signatures = new List<string>();
        var videoCount = 0; var audioCount = 0;
        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            var type = stream.GetProperty("codec_type").GetString();
            if (type is not ("video" or "audio")) continue;
            if (type == "video") videoCount++; else audioCount++;
            if (!stream.TryGetProperty("extradata_hash", out var hash) || hash.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(hash.GetString()))
                throw new InvalidOperationException("无法确认流的编码参数一致性。");
            signatures.Add(string.Join("|", properties.Select(p => stream.TryGetProperty(p, out var value) ? value.ToString() : "")));
        }
        if (videoCount != 1 || audioCount != 1) throw new InvalidOperationException("片段必须恰有一条视频流和一条音频流。");
        var format = root.GetProperty("format");
        if (!format.TryGetProperty("duration", out var duration) || !double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0 || !double.IsFinite(seconds)) throw new InvalidOperationException("无法确认片段时长。");
        return new(string.Join("\n", signatures), seconds);
    }

    public static async Task MergeAsync(IReadOnlyList<string> files, string destination)
    {
        if (files.Count < 2) throw new ArgumentException("至少选择两个录制片段。");
        if (File.Exists(destination)) throw new IOException("目标文件已存在，请选择其他文件名。");
        var paths = files.Select(Path.GetFullPath).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length) throw new ArgumentException("不能重复选择同一文件。");
        if (paths.Any(p => !File.Exists(p) || p.Contains('\r') || p.Contains('\n'))) throw new IOException("片段不存在或文件名不可用。");
        await MediaDownload.ValidateMediaToolsAsync();
        ProbeResult? first = null; double duration = 0;
        foreach (var file in paths)
        {
            var probe = await ProbeAsync(file);
            if (first is not null && probe.Signature != first.Signature) throw new InvalidOperationException("片段编码参数不一致，无法安全无损合并。");
            first ??= probe; duration += probe.Duration;
        }
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        var list = Path.Combine(parent, ".bili-concat-" + Guid.NewGuid().ToString("N") + ".txt");
        var temp = Path.Combine(parent, ".bili-merged-" + Guid.NewGuid().ToString("N") + ".mkv");
        try
        {
            var contents = "ffconcat version 1.0\n" + string.Join("\n", paths.Select(p => "file '" + p.Replace('\\', '/').Replace("'", "'\\''") + "'")) + "\n";
            await File.WriteAllTextAsync(list, contents, new UTF8Encoding(false));
            await MediaDownload.RunAsync("ffmpeg", ["-nostdin", "-v", "error", "-f", "concat", "-safe", "0", "-i", list, "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-n", temp]);
            var result = await ProbeAsync(temp);
            if (Math.Abs(result.Duration - duration) > Math.Max(2, duration * 0.01)) throw new InvalidOperationException("合并文件时长与片段总时长差异过大。");
            File.Move(temp, destination, false);
        }
        finally
        {
            try { File.Delete(list); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
