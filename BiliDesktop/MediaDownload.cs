using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BiliDesktop;

public record TransferOptions(int Connections, int CacheMiB, int MaxConcurrentTasks = 4);
public enum VideoProcessing { Copy, Nvenc, Qsv, Amf, Software }

public static class MediaDownload
{
    private static readonly object SlotLock = new();
    private static int activeCount;
    private static TaskCompletionSource slotsChanged = NewSlotSignal();
    private static TaskCompletionSource NewSlotSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static int ActiveCount { get { lock (SlotLock) return activeCount; } }
    private static TransferOptions current = new(8, 16);
    public static TransferOptions CurrentOptions => Volatile.Read(ref current);
    public static void Configure(TransferOptions options)
    {
        if (options.Connections is < 1 or > 64 || options.CacheMiB is < 0 or > 512 || options.MaxConcurrentTasks < 0) throw new ArgumentException("连接数、缓存大小或最大并发任务数超出范围。");
        lock (SlotLock) { Volatile.Write(ref current, options); PulseSlots(); }
    }
    private static void PulseSlots() { var previous = slotsChanged; slotsChanged = NewSlotSignal(); previous.TrySetResult(); }
    private static async Task AcquireSlotAsync(CancellationToken cancellation)
    {
        while (true)
        {
            Task wait;
            lock (SlotLock)
            {
                cancellation.ThrowIfCancellationRequested();
                if (current.MaxConcurrentTasks == 0 || activeCount < current.MaxConcurrentTasks) { activeCount++; return; }
                wait = slotsChanged.Task;
            }
            await wait.WaitAsync(cancellation);
        }
    }
    private static void ReleaseSlot() { lock (SlotLock) { activeCount--; PulseSlots(); } }
    public static async Task DownloadAsync(IReadOnlyList<string>? video, IReadOnlyList<string>? audio, string? cover, string destination, string userAgent, IProgress<string> progress, CancellationToken cancellation = default, Func<bool>? canStart = null, VideoProcessing processing = VideoProcessing.Copy, TransferControl? control = null)
    {
        progress.Report("等待下载任务…");
        await InSlotAsync(() => DownloadCoreAsync(video, audio, cover, destination, userAgent, progress, processing, control), control?.Token ?? cancellation, canStart, control);
    }

    public static async Task DownloadFreshAsync(BiliApi api, string bvid, VideoPage page, VideoStream? preferredVideo, AudioStream? preferredAudio, bool allowPreview, long sessionVersion, string destination, string userAgent, IProgress<string> progress, VideoProcessing processing = VideoProcessing.Copy, TransferControl? control = null, AudioMetadata? audioMetadata = null)
    {
        if (preferredVideo is null && preferredAudio is null) throw new ArgumentException("请选择下载内容。");
        progress.Report("等待下载任务…");
        await InSlotAsync(async () =>
        {
            if (control is not null) await control.WaitIfPausedAsync();
            if (File.Exists(destination)) throw new IOException("目标文件已存在，请选择其他文件名。");
            progress.Report("正在刷新下载地址…");
            var streams = await api.GetStreamsForPageAsync(bvid, page);
            if (control is not null) await control.WaitIfPausedAsync();
            if (api.SessionVersion != sessionVersion) throw new InvalidOperationException("登录会话已变化，请重新解析视频。");
            if (streams.IsPreview && !allowPreview) throw new InvalidOperationException("当前账号只能获取预览，请重新解析视频。");
            var selectedVideo = preferredVideo is null ? null : streams.Video.FirstOrDefault(v => v.Quality == preferredVideo.Quality && v.Codec == preferredVideo.Codec && v.Bandwidth == preferredVideo.Bandwidth) ?? streams.Video.FirstOrDefault(v => v.Quality == preferredVideo.Quality && v.Codec == preferredVideo.Codec);
            var selectedAudio = preferredAudio is null ? null : streams.Audio.FirstOrDefault(a => a.Quality == preferredAudio.Quality && a.Codec == preferredAudio.Codec && a.Bandwidth == preferredAudio.Bandwidth) ?? streams.Audio.FirstOrDefault(a => a.Quality == preferredAudio.Quality && a.Codec == preferredAudio.Codec);
            if (preferredVideo is not null && selectedVideo is null || preferredAudio is not null && selectedAudio is null) throw new InvalidOperationException("所选音视频流已不可用，请重新解析视频。");
            await DownloadCoreAsync(selectedVideo?.Urls, selectedAudio?.Urls, null, destination, userAgent, progress, processing, control, audioMetadata);
        }, control?.Token ?? CancellationToken.None, canStart: () => api.SessionVersion == sessionVersion, control: control);
    }

    internal static async Task InSlotAsync(Func<Task> work, CancellationToken cancellation = default, Func<bool>? canStart = null, TransferControl? control = null)
    {
        while (true)
        {
            if (control is not null) await control.WaitIfPausedAsync();
            await AcquireSlotAsync(cancellation);
            if (control?.IsPaused != true) break;
            ReleaseSlot();
        }
        var slotHeld = true;
        control?.SetPausedWait(async () =>
        {
            if (slotHeld) { ReleaseSlot(); slotHeld = false; }
            while (true)
            {
                await control.WaitAsync();
                await AcquireSlotAsync(cancellation); slotHeld = true;
                if (canStart is not null && !canStart()) throw new InvalidOperationException("下载规则或登录会话已变化，未启动任务。");
                if (!control.IsPaused) return;
                ReleaseSlot(); slotHeld = false;
            }
        });
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (canStart is not null && !canStart()) throw new InvalidOperationException("下载规则或登录会话已变化，未启动任务。");
            await work();
        }
        finally { control?.SetPausedWait(null); if (slotHeld) ReleaseSlot(); }
    }

    // The caller must hold a slot acquired through InSlotAsync.
    internal static Task DownloadWithinSlotAsync(IReadOnlyList<string>? video, IReadOnlyList<string>? audio, string? cover, string destination, string userAgent, IProgress<string> progress, VideoProcessing processing = VideoProcessing.Copy, TransferControl? control = null, AudioMetadata? audioMetadata = null) =>
        DownloadCoreAsync(video, audio, cover, destination, userAgent, progress, processing, control, audioMetadata);

    private static async Task DownloadCoreAsync(IReadOnlyList<string>? video, IReadOnlyList<string>? audio, string? cover, string destination, string userAgent, IProgress<string> progress, VideoProcessing processing, TransferControl? control, AudioMetadata? audioMetadata = null)
    {
        if (cover is not null && (video is not null || audio is not null) || cover is null && video is null && audio is null) throw new ArgumentException("请选择下载内容。");
        if (audioMetadata is not null && (video is not null || audio is null || cover is not null)) throw new ArgumentException("音频元数据仅适用于仅音频下载。");
        if (video is { Count: 0 } || audio is { Count: 0 }) throw new ArgumentException("媒体地址列表为空。");
        foreach (var url in (video ?? []).Concat(audio ?? []).Concat(cover is null ? [] : [cover]).Concat(audioMetadata?.CoverUrl is string imageUrl ? [imageUrl] : []))
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("媒体地址无效。");
        var ext = Path.GetExtension(destination).ToLowerInvariant();
        if (cover is null && (ext is not (".mp4" or ".m4a" or ".mkv") || ext == ".m4a" && video is not null)) throw new ArgumentException("不支持的输出格式。");
        if (File.Exists(destination)) throw new IOException("目标文件已存在，请选择其他文件名。");
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        var work = Path.Combine(parent, ".bili-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var completed = false; var canceled = false;
        try
        {
            control?.PrepareTransfer();
            if (control is not null) await control.WaitIfPausedAsync();
            if (cover is null) { progress.Report("正在检查媒体工具…"); await ValidateMediaToolsAsync(progress); }
            if (video is not null && processing != VideoProcessing.Copy) { progress.Report("正在检测所选编码器…"); await ValidateEncoderAsync(processing); }
            var options = CurrentOptions;
            using var aria = await AriaSession.StartAsync(options.CacheMiB);
            if (cover is not null)
            {
                await aria.DownloadAsync([cover], work, "cover", progress, "封面", userAgent, options.Connections, control);
                if (control is not null) await control.WaitIfPausedAsync();
                var coverPath = Path.Combine(work, "cover");
                if (new FileInfo(coverPath).Length == 0) throw new InvalidOperationException("封面文件为空。");
                File.Move(coverPath, destination, false);
                completed = true;
                return;
            }
            if (video is not null) await aria.DownloadAsync(video, work, "video.m4s", progress, "视频", userAgent, options.Connections, control);
            if (audio is not null) await aria.DownloadAsync(audio, work, "audio.m4s", progress, "音频", userAgent, options.Connections, control);
            string? coverFile = null;
            if (audioMetadata?.CoverUrl is string image)
            {
                await aria.DownloadAsync([image], work, "metadata-cover", progress, "封面", userAgent, options.Connections, control);
                coverFile = Path.Combine(work, "metadata-cover");
                if (new FileInfo(coverFile).Length == 0) throw new InvalidOperationException("封面文件为空。");
            }
            if (control is not null) await control.BeginProcessingAsync();
            progress.Report("正在处理媒体文件…");
            var output = Path.Combine(work, "output" + ext);
            await ProcessMediaAsync(video is null ? null : Path.Combine(work, "video.m4s"), audio is null ? null : Path.Combine(work, "audio.m4s"), output, processing, control?.Token ?? CancellationToken.None, audioMetadata, coverFile);
            control?.Token.ThrowIfCancellationRequested();
            File.Move(output, destination, false);
            completed = true;
        }
        catch (OperationCanceledException) { canceled = true; throw; }
        catch (Exception ex) { throw new InvalidOperationException($"{ex.Message}；临时文件保留于 {work}", ex); }
        finally { if (completed || canceled) { try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }

    private static string EncoderName(VideoProcessing processing) => processing switch
    {
        VideoProcessing.Nvenc => "h264_nvenc", VideoProcessing.Qsv => "h264_qsv", VideoProcessing.Amf => "h264_amf", VideoProcessing.Software => "libx264", _ => throw new ArgumentException("不支持的编码器。")
    };
    internal static Task ValidateMediaToolsAsync(IProgress<string>? progress = null) =>
        ValidateMediaToolsAsync(() => MediaTools.EnsureAsync(progress), executable => RunAsync(executable, ["-version"], TimeSpan.FromSeconds(10)));

    internal static async Task ValidateMediaToolsAsync(Func<Task> install, Func<string, Task<string>> execute)
    {
        try
        {
            await execute("ffmpeg");
            await execute("ffprobe");
        }
        catch (FileNotFoundException)
        {
            await install();
            await execute("ffmpeg");
            await execute("ffprobe");
        }
    }
    internal static async Task ValidateEncoderAsync(VideoProcessing processing)
    {
        if (processing == VideoProcessing.Copy) return;
        var encoder = EncoderName(processing);
        try { await RunAsync("ffmpeg", ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=size=192x108:rate=1", "-frames:v", "1", "-c:v", encoder, "-f", "null", "-"], TimeSpan.FromSeconds(15)); }
        catch (Exception ex) { throw new InvalidOperationException($"编码器 {encoder} 不可用：{ex.Message}", ex); }
    }
    internal static async Task ProcessMediaAsync(string? video, string? audio, string output, VideoProcessing processing = VideoProcessing.Copy, CancellationToken cancellation = default, AudioMetadata? audioMetadata = null, string? coverFile = null)
    {
        if (video is null && audio is null) throw new ArgumentException("没有媒体输入。");
        if ((audioMetadata is not null || coverFile is not null) && (video is not null || audio is null)) throw new ArgumentException("音频元数据仅适用于仅音频下载。");
        if (audioMetadata?.CoverUrl is not null && coverFile is null) throw new ArgumentException("无法获取视频封面，不能嵌入音频。");
        var matroska = Path.GetExtension(output).Equals(".mkv", StringComparison.OrdinalIgnoreCase);
        string? normalizedCover = null;
        try
        {
            if (coverFile is not null)
            {
                normalizedCover = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, ".cover-" + Guid.NewGuid().ToString("N") + ".jpg");
                await RunAsync("ffmpeg", ["-nostdin", "-v", "error", "-i", coverFile, "-frames:v", "1", "-c:v", "mjpeg", "-q:v", "3", "-n", normalizedCover], cancellation: cancellation);
            }
            var args = new List<string> { "-nostdin", "-v", "error" };
            if (video is not null) args.AddRange(["-i", video]);
            if (audio is not null) args.AddRange(["-i", audio]);
            if (normalizedCover is not null && !matroska) args.AddRange(["-i", normalizedCover]);
            if (video is not null) args.AddRange(["-map", "0:v:0"]);
            if (audio is not null) args.AddRange(["-map", video is null ? "0:a:0" : "1:a:0"]);
            if (video is not null) args.AddRange(["-c:v", processing == VideoProcessing.Copy ? "copy" : EncoderName(processing)]);
            if (video is not null && processing == VideoProcessing.Software) args.AddRange(["-crf", "20"]);
            if (audio is not null) args.AddRange(["-c:a", "copy"]);
            if (normalizedCover is not null)
            {
                if (matroska) args.AddRange(["-attach", normalizedCover, "-metadata:s:t", "mimetype=image/jpeg", "-metadata:s:t", "filename=cover.jpg"]);
                else args.AddRange(["-map", "1:v:0", "-c:v:0", "copy", "-disposition:v:0", "attached_pic"]);
            }
            if (audioMetadata?.Artist is string artist) args.AddRange(["-metadata", "artist=" + artist]);
            if (audioMetadata?.Title is string title) args.AddRange(["-metadata", "title=" + title]);
            if (audioMetadata?.Album is string album) args.AddRange(["-metadata", "album=" + album]);
            if (audioMetadata?.TrackNumber is > 0 and var track) args.AddRange(["-metadata", "track=" + track.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            args.AddRange(["-n", output]);
            await RunAsync("ffmpeg", args.ToArray(), cancellation: cancellation);
            var probe = await RunAsync("ffprobe", ["-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", output], cancellation: cancellation);
            var types = probe.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (video is not null && !types.Contains("video") || audio is not null && !types.Contains("audio")) throw new InvalidOperationException("输出文件缺少所选的音频或视频流。");
            if (normalizedCover is not null && !types.Contains("video") && !types.Contains("attachment")) throw new InvalidOperationException("输出文件缺少所选封面。");
        }
        finally { if (normalizedCover is not null) { try { File.Delete(normalizedCover); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }

    internal static async Task<string> RunAsync(string executable, string[] args, TimeSpan? timeout = null, CancellationToken cancellation = default)
    {
        var resolved = ResolveExecutable(executable, AppContext.BaseDirectory, MediaTools.InstallDirectory);
        var info = new ProcessStartInfo(resolved) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        Process started;
        try { started = Process.Start(info) ?? throw new InvalidOperationException($"无法启动 {executable}"); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2 && executable is ("ffmpeg" or "ffprobe"))
        { throw new FileNotFoundException($"未找到 {executable}。请将 ffmpeg.exe 和 ffprobe.exe 放入程序目录的 vendor/ffmpeg 文件夹，或安装到系统 PATH。", ex); }
        using var process = started;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (timeout.HasValue) timer.CancelAfter(timeout.Value);
        try { await process.WaitForExitAsync(timer.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            _ = await output; _ = await error;
            cancellation.ThrowIfCancellationRequested();
            throw new TimeoutException($"{executable} 执行超时。");
        }
        var stderr = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} 失败：{stderr.Trim()}");
        return await output;
    }

    internal static string ResolveExecutable(string executable, string baseDirectory, string? installedDirectory = null)
    {
        if (executable is not ("ffmpeg" or "ffprobe")) return executable;
        var bundled = Path.Combine(baseDirectory, "vendor", "ffmpeg", executable + ".exe");
        if (File.Exists(bundled)) return bundled;
        if (installedDirectory is not null && MediaTools.Installed(installedDirectory)) return Path.Combine(installedDirectory, executable + ".exe");
        return executable;
    }

    internal sealed class AriaSession : IDisposable
    {
        private readonly Process process;
        private readonly HttpClient http;
        private readonly string secret;
        private readonly string config;
        private readonly EventHandler onExit;
        private AriaSession(Process process, int port, string secret, string config)
        {
            this.process = process; this.secret = secret; this.config = config;
            http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/jsonrpc"), Timeout = TimeSpan.FromSeconds(5) };
            onExit = (_, _) => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } };
            AppDomain.CurrentDomain.ProcessExit += onExit;
        }
        public static async Task<AriaSession> StartAsync(int cacheMiB = 16)
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "vendor", "aria2", "aria2c.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("未找到 aria2c.exe。", executable);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var config = Path.Combine(Path.GetTempPath(), "bili-aria-" + Guid.NewGuid().ToString("N") + ".conf");
            if (cacheMiB is < 0 or > 512) throw new ArgumentOutOfRangeException(nameof(cacheMiB));
            await File.WriteAllTextAsync(config, $"enable-rpc=true\nrpc-listen-all=false\nrpc-listen-port={port}\nrpc-secret={secret}\ndisk-cache={cacheMiB}M\n", new UTF8Encoding(false));
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("--conf-path=" + config);
            Process process;
            try { process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 aria2"); }
            catch { try { File.Delete(config); } catch (IOException) { } throw; }
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            var session = new AriaSession(process, port, secret, config);
            try
            {
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    if (process.HasExited) throw new InvalidOperationException("aria2 启动失败。");
                    try { await session.CallAsync("aria2.getVersion", []); File.Delete(config); return session; }
                    catch (HttpRequestException) { await Task.Delay(200); }
                }
                throw new TimeoutException("aria2 启动超时。");
            }
            catch { session.Dispose(); throw; }
        }
        internal async Task<JsonElement> CallAsync(string method, object[] args)
        {
            var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "1", method, @params = new object[] { "token:" + secret }.Concat(args).ToArray() });
            using var response = await http.PostAsync("", new StringContent(body, Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (json.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException("aria2：" + error.GetProperty("message").GetString());
            return json.RootElement.GetProperty("result").Clone();
        }
        public async Task DownloadAsync(string url, string directory, string name, IProgress<string> progress, string label, string userAgent = BiliApi.DefaultUserAgent)
            => await DownloadAsync([url], directory, name, progress, label, userAgent);

        internal static Dictionary<string, object> MakeOptions(string directory, string name, string userAgent, int connections)
        {
            if (connections is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(connections));
            return new Dictionary<string, object> { ["dir"] = directory, ["out"] = name, ["split"] = connections.ToString(), ["max-connection-per-server"] = Math.Min(connections, 16).ToString(), ["min-split-size"] = "1M", ["referer"] = "https://www.bilibili.com/", ["user-agent"] = userAgent, ["max-tries"] = "3", ["allow-overwrite"] = "false" };
        }
        internal Task<JsonElement> GlobalOptionsAsync() => CallAsync("aria2.getGlobalOption", []);
        public async Task DownloadAsync(IReadOnlyList<string> urls, string directory, string name, IProgress<string> progress, string label, string userAgent = BiliApi.DefaultUserAgent, int connections = 8, TransferControl? control = null)
        {
            if (control is not null) await control.WaitIfPausedAsync();
            var options = MakeOptions(directory, name, userAgent, connections);
            var gid = (await CallAsync("aria2.addUri", [urls, options])).GetString()!;
            while (true)
            {
                if (process.HasExited) throw new InvalidOperationException("aria2 意外退出。");
                var status = await CallAsync("aria2.tellStatus", [gid]);
                var state = status.GetProperty("status").GetString();
                if (state == "complete") return;
                if (state is "error" or "removed") throw new InvalidOperationException(status.TryGetProperty("errorMessage", out var msg) ? msg.GetString() : "下载失败");
                if (control?.IsPaused == true)
                {
                    if (state != "paused")
                    {
                        try { await CallAsync("aria2.pause", [gid]); }
                        catch (InvalidOperationException) { var latest = await CallAsync("aria2.tellStatus", [gid]); if (latest.GetProperty("status").GetString() == "complete") return; throw; }
                    }
                    await control.WaitAfterPauseAsync();
                    await CallAsync("aria2.unpause", [gid]);
                    continue;
                }
                control?.Token.ThrowIfCancellationRequested();
                var current = long.Parse(status.GetProperty("completedLength").GetString()!);
                var total = long.Parse(status.GetProperty("totalLength").GetString()!);
                var speed = status.TryGetProperty("downloadSpeed", out var speedValue) && long.TryParse(speedValue.GetString(), out var parsedSpeed) ? parsedSpeed : 0;
                var activeConnections = status.TryGetProperty("connections", out var connectionValue) && int.TryParse(connectionValue.GetString(), out var parsedConnections) ? parsedConnections : 0;
                control?.Progress?.Report(new TransferProgress(label, current, total, speed, activeConnections));
                progress.Report($"下载{label}：{current / 1048576} / {total / 1048576} MiB");
                await Task.Delay(700, control?.Token ?? CancellationToken.None);
            }
        }
        public void Dispose()
        {
            AppDomain.CurrentDomain.ProcessExit -= onExit;
            http.Dispose();
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            process.Dispose();
            try { File.Delete(config); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
