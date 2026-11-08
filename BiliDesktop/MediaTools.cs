using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace BiliDesktop;

internal static class MediaTools
{
    // Fixed release and digest: https://www.gyan.dev/ffmpeg/builds/
    private const string PackageUrl = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip";
    private const string PackageHash = "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba";
    private static readonly SemaphoreSlim InstallLock = new(1, 1);
    private const long MaxPackageBytes = 200L * 1024 * 1024;
    private const long MaxFileBytes = 200L * 1024 * 1024;
    internal static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliDesktop", "tools", "ffmpeg-9.0.2-essentials");
    internal static bool Installed(string directory) => File.Exists(Path.Combine(directory, "ffmpeg.exe")) && File.Exists(Path.Combine(directory, "ffprobe.exe")) && File.Exists(Path.Combine(directory, "LICENSE"));

    internal static Task EnsureAsync(IProgress<string>? progress = null) => DownloadAndInstallAsync(progress, InstallDirectory, PackageUrl, PackageHash, new HttpClientHandler { AllowAutoRedirect = false });

    internal static async Task DownloadAndInstallAsync(IProgress<string>? progress, string target, string packageUrl, string expectedHash, HttpMessageHandler handler)
    {
        using var ownedHandler = handler;
        await InstallLock.WaitAsync();
        try
        {
            if (Installed(target)) return;
            var parent = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(parent);
            var archive = Path.Combine(parent, ".ffmpeg-download-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                progress?.Report("正在下载 FFmpeg（约 110 MB）…");
                using var http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(10) };
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                using var response = await http.GetAsync(packageUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                var expectedLength = response.Content.Headers.ContentLength;
                if (expectedLength is long length && (length <= 0 || length > MaxPackageBytes)) throw new InvalidDataException("FFmpeg 下载包大小无效。");
                await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
                await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920]; long total = 0; long lastReported = 0; int count;
                    while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
                    {
                        total += count;
                        if (total > MaxPackageBytes) throw new InvalidDataException("FFmpeg 下载包大小超出限制。");
                        await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                        if (total - lastReported >= 1024 * 1024)
                        {
                            lastReported = total;
                            var expected = expectedLength.HasValue ? (expectedLength.Value / (1024 * 1024)).ToString() : "?";
                            progress?.Report($"正在下载 FFmpeg：{total / (1024 * 1024)} / {expected} MiB");
                        }
                    }
                }
                progress?.Report("正在校验并准备 FFmpeg…");
                await InstallArchiveAsync(archive, expectedHash, target);
            }
            catch (OperationCanceledException ex) { throw new TimeoutException("下载 FFmpeg 超时，请检查网络后重试。", ex); }
            finally { try { File.Delete(archive); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        finally { InstallLock.Release(); }
    }

    internal static async Task InstallArchiveAsync(string archive, string expectedHash, string target)
    {
        await using (var file = File.OpenRead(archive))
        {
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file));
            if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("FFmpeg 下载包校验失败。");
        }
        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("工具目录无效。");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".ffmpeg-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var (suffix, name) in new[] { ("/bin/ffmpeg.exe", "ffmpeg.exe"), ("/bin/ffprobe.exe", "ffprobe.exe"), ("/LICENSE", "LICENSE") })
            {
                var entries = zip.Entries.Where(e => e.FullName.EndsWith(suffix, StringComparison.Ordinal) && e.FullName.Count(c => c == '/') == (name == "LICENSE" ? 1 : 2)).ToArray();
                if (entries.Length != 1 || entries[0].Length <= 0 || entries[0].Length > MaxFileBytes) throw new InvalidDataException("FFmpeg 下载包内容无效。");
                await using var input = entries[0].Open();
                await using var output = new FileStream(Path.Combine(staging, name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(output);
                if (output.Length != entries[0].Length) throw new InvalidDataException("FFmpeg 下载包内容不完整。");
            }
            try { Directory.Move(staging, target); }
            catch (IOException) when (Installed(target)) { }
            if (!Installed(target)) throw new IOException("FFmpeg 安装目录不完整，请检查后重试。");
        }
        finally { if (Directory.Exists(staging)) { try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }
}
