using System.IO;
using System.Net.Http;
using System.Text.Json;
using BililiveRecorder.Core;
using BililiveRecorder.Core.Config.V3;
using BililiveRecorder.Core.Config;
using BililiveRecorder.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Collections.Concurrent;

namespace BiliDesktop;

public record LiveRoomState(Guid Id, int RoomId, string Name, string Title, bool Streaming, bool Recording, bool AutoRecord, double? NetworkMibps = null, double? DurationRatio = null);
public record LiveRoomSetting(int RoomId, bool AutoRecord);
public record LiveRecordingOptions(RecordMode Mode = RecordMode.Standard, string Quality = "avc30000,hevc30000,avc20000,hevc20000,avc10000,hevc10000,avc400,hevc400,avc250,hevc250,avc150,hevc150", CuttingMode Cut = CuttingMode.Disabled, uint CutAmount = 100, bool CutOnTitle = false, bool Danmaku = false, bool SaveCover = false);
public record LivePreferences(string Directory, List<LiveRoomSetting> Rooms, LiveRecordingOptions? Options = null);
public record LiveEvent(string Kind, Guid SessionId, int RoomId, DateTimeOffset Time, string? Path = null);

public sealed class LiveManager : IDisposable
{
    private readonly string settingsPath;
    private readonly string journalPath;
    private readonly object journalLock = new();
    private readonly SemaphoreSlim mergeLock = new(1, 1);
    private readonly TimeSpan mergeDelay;
    private readonly CancellationTokenSource lifetime = new();
    private readonly FileStream ownership;
    private readonly ServiceProvider services;
    private readonly ConfigV3 config;
    private readonly IRecorder recorder;
    private readonly Func<IRoom, Task> refreshRoom;
    private readonly LiveMetricsCache metrics = new();
    private sealed record LiveError(string Message, int? RoomId);
    private LiveError? lastError;
    private int disposed;
    public string? LastError => Volatile.Read(ref lastError)?.Message;
    private void SetError(string message, int? roomId = null) => Volatile.Write(ref lastError, new LiveError(message, roomId));
    private void ClearRoomError(int roomId)
    {
        while (Volatile.Read(ref lastError) is { } error && error.RoomId == roomId)
            if (ReferenceEquals(Interlocked.CompareExchange(ref lastError, null, error), error)) return;
    }
    public string DirectoryPath => config.Global.WorkDirectory ?? "";
    public LiveRecordingOptions Options => new(config.Global.RecordMode, config.Global.RecordingQuality ?? "", config.Global.CuttingMode, config.Global.CuttingNumber, config.Global.CuttingByTitle, config.Global.RecordDanmaku, config.Global.SaveStreamCover);

    public LiveManager(string cookie, string? stateDirectory = null, TimeSpan? mergeDelay = null, Func<IRoom, Task>? refreshRoom = null)
    {
        this.refreshRoom = refreshRoom ?? (room => room.RefreshRoomInfoOrThrowAsync());
        this.mergeDelay = mergeDelay ?? TimeSpan.FromSeconds(30);
        var root = stateDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliDesktop");
        Directory.CreateDirectory(root);
        settingsPath = Path.Combine(root, "live-settings.json"); journalPath = Path.Combine(root, "live-events.jsonl");
        ownership = new FileStream(Path.Combine(root, "live.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            LivePreferences settings;
            if (File.Exists(settingsPath))
                settings = JsonSerializer.Deserialize<LivePreferences>(File.ReadAllText(settingsPath)) ?? throw new InvalidDataException("直播设置为空。");
            else
            {
                var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                if (string.IsNullOrWhiteSpace(videos)) videos = root;
                settings = new LivePreferences(Path.Combine(videos, "BiliDesktop Live"), []);
            }
            if (string.IsNullOrWhiteSpace(settings.Directory) || settings.Rooms is null || settings.Rooms.Any(r => r.RoomId <= 0) || settings.Rooms.Select(r => r.RoomId).Distinct().Count() != settings.Rooms.Count) throw new InvalidDataException("直播设置无效，未自动覆盖。");
            Directory.CreateDirectory(settings.Directory);
            config = new ConfigV3 { DisableConfigSave = true };
            config.Global.WorkDirectory = Path.GetFullPath(settings.Directory);
            config.Global.Cookie = cookie;
            config.Global.TimingApiTimeout = 20000;
            config.Global.TimingStreamConnect = 10000;
            config.Global.TimingWatchdogTimeout = 20000;
            config.Global.NetworkTransportAllowedAddressFamily = AllowedAddressFamily.System;
            ApplyOptions(settings.Options ?? new LiveRecordingOptions());
            var logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(new RecorderErrorSink((room, message) => SetError(message, room))).CreateLogger();
            services = new ServiceCollection().AddSingleton<ILogger>(logger).AddFlv().AddRecorderConfig(config).AddRecorder().BuildServiceProvider();
            recorder = services.GetRequiredService<IRecorder>();
            recorder.RecordSessionStarted += (_, e) => { metrics.Clear(e.Room.ObjectId); Append(new("started", e.Event.SessionId, e.Event.RoomId, DateTimeOffset.UtcNow)); };
            recorder.RecordFileOpening += (_, e) => Append(new("file-open", e.Event.SessionId, e.Event.RoomId, e.Event.FileOpenTime, e.Event.FullPath));
            recorder.RecordFileClosed += (_, e) =>
            {
                if (e.Event.FileSize == 0)
                {
                    Append(new("empty-file", e.Event.SessionId, e.Event.RoomId, e.Event.FileCloseTime, e.Event.FullPath));
                    SetError("录制文件为空，未收到有效数据。", e.Event.RoomId);
                }
                else { ClearRoomError(e.Event.RoomId); Append(new("file", e.Event.SessionId, e.Event.RoomId, e.Event.FileCloseTime, e.Event.FullPath)); }
            };
            recorder.IOStats += (_, e) =>
            {
                metrics.Network(e.Room.ObjectId, e.Event.NetworkMbps, e.Event.Duration > TimeSpan.Zero, DateTimeOffset.UtcNow);
                if (e.Event.DiskBytesWritten > 0) ClearRoomError(e.Room.RoomConfig.RoomId);
            };
            recorder.RecordingStats += (_, e) => metrics.Ratio(e.Room.ObjectId, e.Event.DurationRatio, e.Event.PassedTime > 0, DateTimeOffset.UtcNow);
            recorder.RecordSessionEnded += (_, e) =>
            {
                var session = e.Event.SessionId; var room = e.Event.RoomId;
                metrics.Clear(e.Room.ObjectId);
                Append(new("ended", session, room, DateTimeOffset.UtcNow));
                _ = MergeWhenIdleAsync(session, room, e.Room);
            };
            RecoverInterruptedSessions();
            foreach (var room in settings.Rooms) recorder.AddRoom(room.RoomId, room.AutoRecord);
            foreach (var room in recorder.Rooms) _ = ResumePendingAsync(room);
        }
        catch { ownership.Dispose(); throw; }
    }
    private void RecoverInterruptedSessions()
    {
        var events = ReadEvents();
        foreach (var started in events.Where(e => e.Kind == "started").GroupBy(e => (e.RoomId, e.SessionId)).Select(g => g.OrderBy(x => x.Time).First()))
        {
            if (started.SessionId == Guid.Empty || events.Any(e => e.RoomId == started.RoomId && e.SessionId == started.SessionId && e.Kind == "ended")) continue;
            var sessionEvents = events.Where(e => e.RoomId == started.RoomId && e.SessionId == started.SessionId).ToArray();
            var knownFiles = sessionEvents.Where(e => e.Kind == "file" && !string.IsNullOrWhiteSpace(e.Path)).ToList();
            var closedPaths = knownFiles.Select(e => e.Path!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var opened in sessionEvents.Where(e => e.Kind == "file-open" && !string.IsNullOrWhiteSpace(e.Path)))
            {
                try
                {
                    var full = Path.GetFullPath(opened.Path!);
                    var relative = Path.GetRelativePath(DirectoryPath, full);
                    if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !closedPaths.Add(full)) continue;
                    var info = new FileInfo(full);
                    if (!info.Exists || info.Length == 0) continue;
                    var time = new DateTimeOffset(info.LastWriteTimeUtc);
                    if (time < opened.Time) time = opened.Time;
                    if (time > DateTimeOffset.UtcNow) time = DateTimeOffset.UtcNow;
                    var recovered = new LiveEvent("file", started.SessionId, started.RoomId, time, full);
                    Append(recovered); knownFiles.Add(recovered);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { SetError("恢复录制记录失败：" + ex.Message); }
            }
            if (knownFiles.Count == 0) continue;
            var end = knownFiles.Max(x => x.Time);
            if (end < started.Time) end = started.Time;
            Append(new("ended", started.SessionId, started.RoomId, end));
        }
    }
    private async Task ResumePendingAsync(IRoom room)
    {
        try
        {
            await Task.Delay(mergeDelay, lifetime.Token);
            var roomId = room.RoomConfig.RoomId;
            var events = ReadEvents();
            if (events.Any(e => e.Kind == "ended") && !events.Any(e => e.RoomId == roomId && e.Kind == "ended"))
            {
                try { await room.RefreshRoomInfoAsync(); roomId = room.RoomConfig.RoomId; }
                catch (Exception ex) { SetError("恢复录制时无法解析直播间号码：" + ex.Message); }
            }
            var ended = events.Where(e => e.RoomId == roomId && e.Kind == "ended").GroupBy(e => e.SessionId)
                .Select(g => g.OrderByDescending(e => e.Time).First()).OrderByDescending(e => e.Time).ToArray();
            var covered = new HashSet<Guid>();
            foreach (var session in ended)
            {
                if (covered.Contains(session.SessionId)) continue;
                covered.UnionWith(LiveSessionGrouping.RelatedSessions(events, roomId, session.SessionId, TimeSpan.FromMinutes(2)));
                if (events.Any(e => e.RoomId == roomId && e.SessionId == session.SessionId && e.Kind is "merge-complete" or "merge-exists")) continue;
                await MergeWhenIdleAsync(session.SessionId, roomId, room, waitForSettle: false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetError("恢复录制整理失败：" + ex.Message); }
    }
    private async Task MergeWhenIdleAsync(Guid session, int room, IRoom roomState, bool waitForSettle = true)
    {
        try { if (waitForSettle) await Task.Delay(mergeDelay, lifetime.Token); else lifetime.Token.ThrowIfCancellationRequested(); }
        catch (OperationCanceledException) { return; }
        if (roomState.Recording || roomState.Streaming && roomState.RoomConfig.AutoRecord && roomState.AutoRecordForThisSession) return;
        try { await mergeLock.WaitAsync(lifetime.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            var files = LiveSessionGrouping.RelatedFiles(ReadEvents(), room, session, TimeSpan.FromMinutes(2));
            if (files.Count < 2) return;
            var output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(files[0]))!, $"merged-{session:N}.mkv");
            if (File.Exists(output)) { Append(new("merge-exists", session, room, DateTimeOffset.UtcNow, output)); return; }
            Append(new("merge-start", session, room, DateTimeOffset.UtcNow, output));
            await LiveMerge.MergeAsync(files, output);
            Append(new("merge-complete", session, room, DateTimeOffset.UtcNow, output));
        }
        catch (Exception ex)
        {
            SetError("片段未合并：" + ex.Message);
            Append(new("merge-failed", session, room, DateTimeOffset.UtcNow, ex.Message));
        }
        finally { mergeLock.Release(); }
    }
    private void Append(LiveEvent item)
    {
        try { lock (journalLock) File.AppendAllText(journalPath, JsonSerializer.Serialize(item) + Environment.NewLine); }
        catch (Exception ex) { SetError("录制事件保存失败：" + ex.Message); }
    }
    public IReadOnlyList<LiveEvent> ReadEvents()
    {
        lock (journalLock)
        {
            if (!File.Exists(journalPath)) return [];
            var result = new List<LiveEvent>();
            foreach (var line in File.ReadLines(journalPath))
            {
                try { var item = JsonSerializer.Deserialize<LiveEvent>(line); if (item is not null) result.Add(item); }
                catch (JsonException) { SetError("录制事件记录中存在损坏行；原始记录已保留。"); }
            }
            return result;
        }
    }
    public IReadOnlyList<LiveRoomState> Rooms()
    {
        var now = DateTimeOffset.UtcNow;
        return recorder.Rooms.Select(r =>
        {
            var (network, ratio) = metrics.Read(r.ObjectId, r.Recording, now);
            return new LiveRoomState(r.ObjectId, r.RoomConfig.RoomId, r.Name, r.Title, r.Streaming, r.Recording, r.RoomConfig.AutoRecord, network, ratio);
        }).ToList();
    }
    public void UpdateCookie(string cookie) => config.Global.Cookie = cookie;
    private IRoom Find(Guid id) => recorder.Rooms.FirstOrDefault(r => r.ObjectId == id) ?? throw new InvalidOperationException("直播间不存在。");
    public Guid AddRoom(string input) => AddRoom(input, true);
    private Guid AddRoom(string input, bool autoRecord)
    {
        var match = RoomIdFromUrl.Regex.Match(input.Trim());
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var id) || id <= 0) throw new ArgumentException("请输入有效的直播间号码或链接。");
        if (recorder.Rooms.Any(r => r.RoomConfig.RoomId == id || r.ShortId == id)) throw new InvalidOperationException("直播间已存在。");
        var room = recorder.AddRoom(id, autoRecord);
        try { Save(); } catch { recorder.RemoveRoom(room); throw; }
        return room.ObjectId;
    }
    public async Task<Guid> AddRoomAsync(string input)
    {
        input = await ResolveRoomInputAsync(input);
        var id = AddRoom(input, false);
        var room = Find(id);
        try { await RefreshAsync(room.ObjectId); }
        catch (Exception ex) { throw new InvalidOperationException("直播间已添加，自动录制已暂停；刷新失败：" + ex.Message); }
        if (recorder.Rooms.Any(r => r.ObjectId != room.ObjectId && r.RoomConfig.RoomId == room.RoomConfig.RoomId))
        {
            recorder.RemoveRoom(room); Save();
            throw new InvalidOperationException("直播间已存在。");
        }
        WriteSettings(new(DirectoryPath, recorder.Rooms.Select(r => new LiveRoomSetting(r.RoomConfig.RoomId, r.ObjectId == id || r.RoomConfig.AutoRecord)).ToList(), Options));
        room.RoomConfig.AutoRecord = true;
        return room.ObjectId;
    }
    internal static async Task<string> ResolveRoomInputAsync(string input, HttpMessageHandler? handler = null)
    {
        input = input.Trim();
        if (input.StartsWith("b23.tv/", StringComparison.OrdinalIgnoreCase)) input = "https://" + input;
        if (Uri.TryCreate(input, UriKind.Absolute, out var link) && link.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase))
        {
            using var client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
            for (var hops = 0; hops < 5; hops++)
            {
                if (link.Scheme != Uri.UriSchemeHttps || !link.IsDefaultPort || link.UserInfo.Length != 0 || !link.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("直播短链接无效。");
                using var response = await client.GetAsync(link, HttpCompletionOption.ResponseHeadersRead);
                if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is null) throw new InvalidOperationException("无法解析直播短链接。");
                link = new Uri(link, response.Headers.Location);
                if (!link.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase)) break;
            }
            if (link.Scheme != Uri.UriSchemeHttps || !link.IsDefaultPort || link.UserInfo.Length != 0 || !link.Host.Equals("live.bilibili.com", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("直播短链接未指向支持的直播间。");
            input = link.ToString();
        }
        return input;
    }
    public void RemoveRoom(Guid id)
    {
        var room = Find(id);
        if (room.Recording) throw new InvalidOperationException("请先停止该直播间的录制。");
        var old = recorder.Rooms.Select(r => new LiveRoomSetting(r.RoomConfig.RoomId, r.RoomConfig.AutoRecord)).ToList();
        WriteSettings(new(DirectoryPath, old.Where(r => r.RoomId != room.RoomConfig.RoomId).ToList(), Options));
        recorder.RemoveRoom(room);
    }
    public void ToggleAuto(Guid id)
    {
        var room = Find(id); var previous = room.RoomConfig.AutoRecord;
        room.RoomConfig.AutoRecord = !previous;
        try { Save(); } catch { room.RoomConfig.AutoRecord = previous; throw; }
    }
    public void Start(Guid id) => Find(id).StartRecord();
    public void Stop(Guid id) { var room = Find(id); metrics.Clear(room.ObjectId); room.StopRecord(); }
    internal void SplitForTest(Guid id) => Find(id).SplitOutput();
    public async Task RefreshAsync(Guid id)
    {
        var room = Find(id);
        ClearRoomError(room.RoomConfig.RoomId);
        try { await refreshRoom(room); }
        catch (Exception ex) { throw new InvalidOperationException("获取直播间信息失败：" + RecorderErrorSink.DescribeError(ex)); }
        if (string.IsNullOrWhiteSpace(room.Name)) throw new InvalidOperationException(LastError ?? "无法获取直播间信息，请检查房间号、网络或登录状态。");
    }
    public async Task StartAsync(Guid id)
    {
        await RefreshAsync(id);
        var room = Find(id);
        if (!room.Streaming) throw new InvalidOperationException("直播间当前未开播。");
        room.StartRecord();
    }
    public void SetOptions(LiveRecordingOptions options)
    {
        if (recorder.Rooms.Any(r => r.Recording)) throw new InvalidOperationException("请先停止所有录制。");
        if (!Enum.IsDefined(options.Mode) || !Enum.IsDefined(options.Cut) || options.CutAmount == 0 || string.IsNullOrWhiteSpace(options.Quality) || options.Quality.Split(',').Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x.Trim(), @"^(?:(?:avc|hevc))?\d{1,6}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))) throw new ArgumentException("录制设置无效，请检查画质优先级和分段数值。");
        if (options.Mode == RecordMode.RawData && (options.Cut != CuttingMode.Disabled || options.CutOnTitle)) throw new ArgumentException("原始数据模式不支持分段，请先关闭分段。");
        var old = Options;
        ApplyOptions(options);
        try { Save(); } catch { ApplyOptions(old); throw; }
    }
    private void ApplyOptions(LiveRecordingOptions options)
    {
        config.Global.RecordMode = options.Mode; config.Global.RecordingQuality = options.Quality;
        config.Global.CuttingMode = options.Cut; config.Global.CuttingNumber = options.CutAmount;
        config.Global.CuttingByTitle = options.CutOnTitle; config.Global.RecordDanmaku = options.Danmaku; config.Global.SaveStreamCover = options.SaveCover;
    }
    public void SetDirectory(string path)
    {
        if (recorder.Rooms.Any(r => r.Recording)) throw new InvalidOperationException("请先停止所有录制。");
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("录制目录不存在。");
        var old = DirectoryPath; config.Global.WorkDirectory = Path.GetFullPath(path);
        try { Save(); } catch { config.Global.WorkDirectory = old; throw; }
    }
    private void Save() => WriteSettings(new(DirectoryPath, recorder.Rooms.Select(r => new LiveRoomSetting(r.RoomConfig.RoomId, r.RoomConfig.AutoRecord)).ToList(), Options));
    private void WriteSettings(LivePreferences settings)
    {
        var temp = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(settings)); File.Move(temp, settingsPath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void StopAll() { foreach (var room in recorder.Rooms.Where(r => r.Recording)) room.StopRecord(); }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        try { recorder.Dispose(); services.Dispose(); }
        finally { ownership.Dispose(); }
    }
}

internal sealed class LiveMetricsCache
{
    private sealed record Snapshot(double? Network, DateTimeOffset NetworkTime, double? Ratio, DateTimeOffset RatioTime);
    private readonly ConcurrentDictionary<Guid, Snapshot> values = new();
    public void Network(Guid id, double value, bool valid, DateTimeOffset time)
    {
        var measured = valid && double.IsFinite(value) && value >= 0 ? value : (double?)null;
        values.AddOrUpdate(id, _ => new(measured, time, null, default), (_, old) => old with { Network = measured, NetworkTime = time });
    }
    public void Ratio(Guid id, double value, bool valid, DateTimeOffset time)
    {
        var measured = valid && double.IsFinite(value) && value >= 0 && value <= double.MaxValue / 100 ? value : (double?)null;
        values.AddOrUpdate(id, _ => new(null, default, measured, time), (_, old) => old with { Ratio = measured, RatioTime = time });
    }
    public (double? Network, double? Ratio) Read(Guid id, bool recording, DateTimeOffset now)
    {
        if (!recording || !values.TryGetValue(id, out var snapshot)) return (null, null);
        var networkAge = now - snapshot.NetworkTime; var ratioAge = now - snapshot.RatioTime;
        return (networkAge >= TimeSpan.Zero && networkAge <= TimeSpan.FromSeconds(6) ? snapshot.Network : null,
                ratioAge >= TimeSpan.Zero && ratioAge <= TimeSpan.FromSeconds(10) ? snapshot.Ratio : null);
    }
    public void Clear(Guid id) => values.TryRemove(id, out _);
}

internal sealed class RecorderErrorSink(Action<int?, string> report) : ILogEventSink
{
    internal static string DescribeError(Exception? error)
    {
        var apiCode = System.Text.RegularExpressions.Regex.Match(error?.Message ?? "", @"^BiliBili API Code: (-?\d+)");
        return error is TimeoutException ? "直播线路连接超时或未返回数据" : error is TaskCanceledException ? "请求超时或连接被中断" : error is HttpRequestException http && http.StatusCode.HasValue ? $"HTTP {(int)http.StatusCode.Value}" : error?.GetType().Name == "Http412Exception" ? "HTTP 412" : apiCode.Success ? "API " + apiCode.Groups[1].Value : error?.GetType().Name ?? "UnknownError";
    }
    public void Emit(LogEvent logEvent)
    {
        var text = logEvent.MessageTemplate.Text;
        var roomId = logEvent.Properties.TryGetValue(LoggingContext.RoomId, out var value) && value is ScalarValue { Value: int room } ? room : (int?)null;
        if (text.Contains("直播线路连接失败，正在尝试备用线路", StringComparison.Ordinal))
        {
            var phase = logEvent.Properties.TryGetValue("Phase", out var phaseValue) && phaseValue is ScalarValue { Value: string s } ? s : "";
            report(roomId, phase == "data" ? "直播线路未返回有效数据，正在尝试备用线路。" : "直播线路连接失败，正在尝试备用线路。"); return;
        }
        if (text.Contains("没有符合设置要求的画质", StringComparison.Ordinal)) { report(roomId, "没有匹配的直播画质，请调整画质优先级。"); return; }
        if (logEvent.Level < LogEventLevel.Warning || !(text.Contains("刷新房间信息时出错", StringComparison.Ordinal) || text.Contains("启动录制出错", StringComparison.Ordinal) || text.Contains("尝试开始录制时出错", StringComparison.Ordinal))) return;
        var message = DescribeError(logEvent.Exception);
        report(roomId, (text.Contains("刷新", StringComparison.Ordinal) ? "获取直播间信息失败：" : "录制启动失败：") + message);
    }
}
