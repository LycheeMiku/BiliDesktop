using System.IO;
using System.Text.Json;

namespace BiliDesktop;

public static class AppSettings
{
    internal static string? FileNameOverride { get; set; }
    private static string FileName => FileNameOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliDesktop", "settings.json");
    public static string LoadUserAgent()
    {
        if (!File.Exists(FileName)) return BiliApi.DefaultUserAgent;
        using var data = JsonDocument.Parse(File.ReadAllText(FileName));
        return data.RootElement.TryGetProperty("userAgent", out var ua) ? ua.GetString() ?? BiliApi.DefaultUserAgent : BiliApi.DefaultUserAgent;
    }
    public static void SaveUserAgent(string ua)
    {
        var options = LoadTransferOptions();
        Save(ua, options, LoadLanguage(), LoadDownloadDirectory(), LoadAllowBackground());
    }
    public static TransferOptions LoadTransferOptions()
    {
        if (!File.Exists(FileName)) return new(8, 16);
        using var data = JsonDocument.Parse(File.ReadAllText(FileName));
        var split = data.RootElement.TryGetProperty("connections", out var s) && s.TryGetInt32(out var sv) ? sv : 8;
        var cache = data.RootElement.TryGetProperty("cacheMiB", out var c) && c.TryGetInt32(out var cv) ? cv : 16;
        var maximum = data.RootElement.TryGetProperty("maxConcurrentTasks", out var m) && m.TryGetInt32(out var mv) ? mv : 4;
        return new(split, cache, maximum);
    }
    public static void SaveTransferOptions(TransferOptions options) => Save(LoadUserAgent(), options, LoadLanguage(), LoadDownloadDirectory(), LoadAllowBackground());
    public static string LoadDownloadDirectory()
    {
        if (File.Exists(FileName))
        {
            using var data = JsonDocument.Parse(File.ReadAllText(FileName));
            if (data.RootElement.TryGetProperty("downloadDirectory", out var path) && path.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(path.GetString())) return path.GetString()!;
        }
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return string.IsNullOrWhiteSpace(videos) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : videos;
    }
    public static void SaveDownloadDirectory(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("下载目录不存在。");
        Save(LoadUserAgent(), LoadTransferOptions(), LoadLanguage(), Path.GetFullPath(path), LoadAllowBackground());
    }
    public static string LoadLanguage()
    {
        if (!File.Exists(FileName)) return "zh-CN";
        using var data = JsonDocument.Parse(File.ReadAllText(FileName));
        var language = data.RootElement.TryGetProperty("language", out var value) ? value.GetString() : null;
        return language is "zh-TW" or "en" ? language : "zh-CN";
    }
    public static void SaveLanguage(string language)
    {
        if (language is not ("zh-CN" or "zh-TW" or "en")) throw new ArgumentException("Unsupported language.");
        Save(LoadUserAgent(), LoadTransferOptions(), language, LoadDownloadDirectory(), LoadAllowBackground());
    }
    public static bool LoadAllowBackground()
    {
        if (!File.Exists(FileName)) return false;
        using var data = JsonDocument.Parse(File.ReadAllText(FileName));
        return data.RootElement.TryGetProperty("allowBackground", out var value) && value.ValueKind == JsonValueKind.True;
    }
    public static void SaveAllowBackground(bool enabled) => Save(LoadUserAgent(), LoadTransferOptions(), LoadLanguage(), LoadDownloadDirectory(), enabled);
    private static void Save(string ua, TransferOptions options, string language, string directory, bool allowBackground)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileName)!);
        var temporary = FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new { userAgent = ua, connections = options.Connections, cacheMiB = options.CacheMiB, maxConcurrentTasks = options.MaxConcurrentTasks, language, downloadDirectory = directory, allowBackground })); File.Move(temporary, FileName, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
