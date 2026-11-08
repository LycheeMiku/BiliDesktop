using System.Text.Json;
using BiliDesktop;
using BililiveRecorder.Core.Config;
using BililiveRecorder.Core.Recording;
using BililiveRecorder.Core.Api;
using Microsoft.Extensions.DependencyInjection;
using BililiveRecorder.Core;
using Serilog;

{
    var cache = new LiveMetricsCache(); var id = Guid.NewGuid(); var other = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
    cache.Network(id, 2.5, true, now); cache.Ratio(id, 1.23, true, now);
    if (cache.Read(id, true, now) != (2.5, 1.23) || cache.Read(other, true, now) != (null, null) || cache.Read(id, false, now) != (null, null)) throw new Exception("Live metrics values or room isolation failed.");
    if (cache.Read(id, true, now.AddSeconds(7)) != (null, 1.23) || cache.Read(id, true, now.AddSeconds(11)) != (null, null)) throw new Exception("Stale live metrics were displayed.");
    cache.Network(id, double.NaN, true, now); cache.Ratio(id, double.PositiveInfinity, true, now);
    if (cache.Read(id, true, now) != (null, null)) throw new Exception("Invalid live metrics were displayed.");
    cache.Network(id, 0, true, now); cache.Ratio(id, 0, true, now);
    if (cache.Read(id, true, now) != (0d, 0d)) throw new Exception("A valid zero metric was hidden.");
    cache.Clear(id); if (cache.Read(id, true, now) != (null, null)) throw new Exception("Live metrics were not cleared.");
    Console.WriteLine("Live bandwidth and recording-rate freshness, invalid data and room isolation passed.");
}

if (args.Length == 2 && args[0] == "--diagnose-room" && int.TryParse(args[1], out var diagnoseRoom))
{
    var diagnoseRoot = Path.Combine(Path.GetTempPath(), "bili-live-diag-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(diagnoseRoot);
    try
    {
        var credential = CredentialStore.LoadCredential() ?? throw new InvalidOperationException("Saved login unavailable.");
        var authenticated = new BiliApi(); authenticated.SetCookie(credential.Cookie); await authenticated.GetAccountInfoAsync();
        await File.WriteAllTextAsync(Path.Combine(diagnoseRoot, "live-settings.json"), JsonSerializer.Serialize(new LivePreferences(diagnoseRoot, [new LiveRoomSetting(diagnoseRoom, false)])));
        using var manager = new LiveManager(authenticated.RecorderCookie, diagnoseRoot);
        var room = manager.Rooms().Single(); await manager.RefreshAsync(room.Id);
        var services = (IServiceProvider)typeof(LiveManager).GetField("services", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!;
        var client = services.GetRequiredService<IApiClient>();
        var (avc, hevc) = await client.GetCodecItemInStreamUrlAsync(manager.Rooms().Single().RoomId, 10000);
        Console.WriteLine($"Advertised AVC qualities: {string.Join(",", avc?.AcceptQn ?? [])}; HEVC: {string.Join(",", hevc?.AcceptQn ?? [])}.");
        var preferredQn = new[] { 30000, 20000, 10000, 400, 250, 150 }.FirstOrDefault(q => (avc?.AcceptQn.Contains(q) ?? false) || (hevc?.AcceptQn.Contains(q) ?? false));
        if (preferredQn != 0 && preferredQn != 10000) (avc, hevc) = await client.GetCodecItemInStreamUrlAsync(manager.Rooms().Single().RoomId, preferredQn);
        var item = avc ?? hevc ?? throw new Exception("No supported FLV codec.");
        Console.WriteLine($"Requested quality: {preferredQn}; returned quality: {item.CurrentQn}.");
        var candidates = item.UrlInfos?.Take(3).ToArray() ?? [];
        Console.WriteLine($"Available test lines: {candidates.Length}.");
        async Task<string> TestLine(int index, string mode)
        {
            var candidate = candidates[index]; var original = new Uri(candidate.Host + item.BaseUrl + candidate.Extra);
            try
            {
                using var handler = new HttpClientHandler { UseProxy = mode == "proxy", UseCookies = false, AllowAutoRedirect = true };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(9) };
                var target = original;
                if (mode == "ipv4")
                {
                    var ips = await System.Net.Dns.GetHostAddressesAsync(original.DnsSafeHost);
                    var ip = ips.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? throw new Exception("No IPv4");
                    target = new UriBuilder(original) { Host = ip.ToString() }.Uri;
                }
                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                if (mode == "ipv4") request.Headers.Host = original.IsDefaultPort ? original.Host : original.Authority;
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36 Edg/132.0.0.0");
                request.Headers.TryAddWithoutValidation("Origin", "https://live.bilibili.com"); request.Headers.Referrer = new Uri("https://live.bilibili.com/");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(9));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode) return $"line {index + 1}, {mode}: HTTP {(int)response.StatusCode}";
                using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var bytes = new byte[13]; var count = 0;
                while (count < bytes.Length) { var n = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token); if (n == 0) break; count += n; }
                return $"line {index + 1}, {mode}: HTTP {(int)response.StatusCode}, FLV={(count >= 3 && bytes[0] == 70 && bytes[1] == 76 && bytes[2] == 86)}, bytes={count}";
            }
            catch (Exception ex) { return $"line {index + 1}, {mode}: {ex.GetType().Name}"; }
        }
        var probes = Enumerable.Range(0, candidates.Length).SelectMany(i => new[] { "direct", "proxy", "ipv4" }.Select(mode => TestLine(i, mode))).ToArray();
        foreach (var line in await Task.WhenAll(probes)) Console.WriteLine(line);
        if (candidates.Length >= 2)
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36 Edg/132.0.0.0");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://live.bilibili.com"); http.DefaultRequestHeaders.Referrer = new Uri("https://live.bilibili.com/");
            var urls = candidates.Select(x => x.Host + item.BaseUrl + x.Extra).ToArray();
            var attempts = 0; var retried = 0; HttpResponseMessage? response = null;
            try
            {
                var (stream, _) = await LiveStreamConnector.ConnectAsync(urls, async url =>
                {
                    if (Interlocked.Increment(ref attempts) == 1) throw new TimeoutException("fixture");
                    response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead); response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStreamAsync();
                }, TimeSpan.FromSeconds(10), CancellationToken.None, (_, _) => retried++);
                using (stream) { var prefix = new byte[3]; if (await stream.ReadAsync(prefix) != 3 || !prefix.SequenceEqual(new byte[] { 70, 76, 86 })) throw new Exception("Real fallback lost FLV header."); }
                if (attempts < 2 || retried < 1) throw new Exception("Live fallback did not switch lines.");
                Console.WriteLine("Forced first-line failure and real backup-line FLV verification passed.");
            }
            finally { response?.Dispose(); }
        }
    }
    finally { Directory.Delete(diagnoseRoot, true); }
    return;
}

{
    var attempted = new List<string>();
    var stalled = new HangingLiveStream();
    var (stream, selected) = await LiveStreamConnector.ConnectAsync(["timeout", "bad", "good"], name =>
    {
        attempted.Add(name);
        return name switch { "timeout" => Task.FromResult<Stream>(stalled), "bad" => Task.FromResult<Stream>(new MemoryStream([0x00, 0x01, 0x02])), _ => Task.FromResult<Stream>(new MemoryStream([0x46, 0x4c, 0x56, 0x01, 0x05, 0, 0, 0, 9, 0, 0, 0, 0, 42])) };
    }, TimeSpan.FromMilliseconds(50), CancellationToken.None);
    using (stream)
    using (var result = new MemoryStream())
    {
        await stream.CopyToAsync(result);
        if (selected != "good" || !attempted.SequenceEqual(["timeout", "bad", "good"]) || !stalled.WasDisposed || !result.ToArray().SequenceEqual(new byte[] { 0x46, 0x4c, 0x56, 0x01, 0x05, 0, 0, 0, 9, 0, 0, 0, 0, 42 })) throw new Exception("Live line fallback or FLV prefix replay failed.");
    }
    using var stopped = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
    var calls = 0;
    try { await LiveStreamConnector.ConnectAsync(["one", "two"], _ => { calls++; return Task.FromResult<Stream>(new HangingLiveStream()); }, TimeSpan.FromSeconds(5), stopped.Token); throw new Exception("Stopped live connection continued."); }
    catch (OperationCanceledException) { }
    if (calls != 1) throw new Exception("Canceled live connection attempted another line.");
    Console.WriteLine("Live line timeout, invalid stream fallback, FLV prefix preservation and stop cancellation passed.");
}

if (args.Length == 2 && args[0] is ("--probe-room" or "--probe-room-raw") && int.TryParse(args[1], out var requestedRoom))
{
    var probeRoot = Path.Combine(Path.GetTempPath(), "bili-room-probe-" + Guid.NewGuid().ToString("N"));
    var probeDirectory = Path.Combine(probeRoot, "recordings"); Directory.CreateDirectory(probeDirectory);
    try
    {
        var credential = CredentialStore.LoadCredential() ?? throw new InvalidOperationException("Saved login unavailable.");
        var api = new BiliApi(); api.SetCookie(credential.Cookie); await api.GetAccountInfoAsync();
        var probeOptions = args[0] == "--probe-room-raw" ? new LiveRecordingOptions(Mode: RecordMode.RawData) : null;
        await File.WriteAllTextAsync(Path.Combine(probeRoot, "live-settings.json"), JsonSerializer.Serialize(new LivePreferences(probeDirectory, [new LiveRoomSetting(requestedRoom, false)], probeOptions)));
        using var manager = new LiveManager(api.RecorderCookie, probeRoot, TimeSpan.FromSeconds(1));
        var room = manager.Rooms().Single(); await manager.RefreshAsync(room.Id);
        Console.WriteLine($"Room status: live={manager.Rooms().Single().Streaming}.");
        if (!manager.Rooms().Single().Streaming) return;
        await manager.StartAsync(room.Id);
        string? lastProbeError = null;
        for (var i = 0; i < 160; i++)
        {
            if (manager.LastError is string message && message != lastProbeError) { lastProbeError = message; Console.WriteLine("Recorder status: " + message); }
            if (manager.ReadEvents().Any(e => e.Kind == "file-open")) break;
            await Task.Delay(250);
        }
        for (var i = 0; i < 60; i++)
        {
            var snapshot = manager.Rooms().Single();
            if (snapshot.NetworkMibps.HasValue && (probeOptions?.Mode == RecordMode.RawData || snapshot.DurationRatio.HasValue)) break;
            await Task.Delay(250);
        }
        var liveMetrics = manager.Rooms().Single();
        if (!liveMetrics.NetworkMibps.HasValue || (probeOptions?.Mode != RecordMode.RawData && !liveMetrics.DurationRatio.HasValue) || (probeOptions?.Mode == RecordMode.RawData && liveMetrics.DurationRatio.HasValue)) throw new Exception("Real live metrics missing or raw recording rate was invented.");
        manager.Stop(room.Id);
        for (var i = 0; i < 60 && manager.Rooms().Single().Recording; i++) await Task.Delay(200);
        var stoppedRoom = manager.Rooms().Single();
        if (stoppedRoom.Recording || stoppedRoom.NetworkMibps.HasValue || stoppedRoom.DurationRatio.HasValue) throw new Exception("Stopped recording retained live metrics.");
        var files = manager.ReadEvents().Where(e => e.Kind == "file" && e.Path is not null && File.Exists(e.Path)).Select(e => e.Path!).ToArray();
        if (files.Length == 0 || files.All(file => new FileInfo(file).Length == 0)) throw new Exception("No nonempty recording: " + (manager.LastError ?? "no recorder error"));
        var media = await MediaDownload.RunAsync("ffprobe", ["-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", files[0]], TimeSpan.FromSeconds(15));
        if (!media.Contains("video", StringComparison.Ordinal)) throw new Exception("Recorded file has no readable video stream.");
        Console.WriteLine($"Recorder probe: files={files.Length}, nonempty={files.Count(file => new FileInfo(file).Length > 0)}, video=readable, bandwidth=available, rate={(liveMetrics.DurationRatio.HasValue ? "available" : "unavailable (raw mode)")}, last-error={manager.LastError ?? "none"}.");
    }
    finally { Directory.Delete(probeRoot, true); }
    return;
}

{
    var resolved = await LiveManager.ResolveRoomInputAsync("b23.tv/start", new LiveShortLinkFixture());
    if (resolved != "https://live.bilibili.com/6?from=share") throw new Exception("Live short link resolution failed");
    foreach (var bad in new[] { "https://b23.tv/evil", "https://b23.tv/insecure", "https://b23.tv/loop" })
    {
        try { await LiveManager.ResolveRoomInputAsync(bad, new LiveShortLinkFixture()); throw new Exception("Unsafe live short link accepted"); }
        catch (ArgumentException) { }
    }
    Console.WriteLine("Live short link redirects, domain restrictions and credential isolation passed.");
}

if (args.Length == 2 && args[0] == "--resume-crash-root")
{
    using var restarted = new LiveManager("", Path.GetFullPath(args[1]), TimeSpan.FromSeconds(1));
    for (var i = 0; i < 150 && !restarted.ReadEvents().Any(x => x.Kind is "merge-complete" or "merge-failed"); i++) await Task.Delay(200);
    var result = restarted.ReadEvents().LastOrDefault(x => x.Kind is "merge-complete" or "merge-failed");
    if (result?.Kind != "merge-complete" || result.Path is null || !File.Exists(result.Path)) throw new Exception("Retry recovery failed: " + result?.Path + "; lastError=" + restarted.LastError);
    Console.WriteLine("Retained real interrupted recording recovered successfully.");
    return;
}

if (args.Length == 2 && args[0] == "--crash-child")
{
    var childRoot = Path.GetFullPath(args[1]);
    var childRecordings = Path.Combine(childRoot, "recordings"); Directory.CreateDirectory(childRecordings);
    await File.WriteAllTextAsync(Path.Combine(childRoot, "live-settings.json"), JsonSerializer.Serialize(new LivePreferences(childRecordings, [new LiveRoomSetting(6, false)])));
    using var childManager = new LiveManager("", childRoot, TimeSpan.FromSeconds(1));
    var childRoom = childManager.Rooms().Single();
    await childManager.RefreshAsync(childRoom.Id);
    if (!childManager.Rooms().Single().Streaming) throw new Exception("Live sample is not currently streaming.");
    childManager.Start(childRoom.Id);
    for (var i = 0; i < 150 && !childManager.ReadEvents().Any(x => x.Kind == "file-open"); i++) await Task.Delay(200);
    if (!childManager.ReadEvents().Any(x => x.Kind == "file-open")) throw new Exception("First recording file did not open.");
    await Task.Delay(TimeSpan.FromSeconds(6)); childManager.SplitForTest(childRoom.Id);
    for (var i = 0; i < 150 && (childManager.ReadEvents().Count(x => x.Kind == "file-open") < 2 || !childManager.ReadEvents().Any(x => x.Kind == "file")); i++) await Task.Delay(200);
    if (childManager.ReadEvents().Count(x => x.Kind == "file-open") < 2 || !childManager.ReadEvents().Any(x => x.Kind == "file")) throw new Exception("Live split did not create two files.");
    await Task.Delay(TimeSpan.FromSeconds(6));
    await File.WriteAllTextAsync(Path.Combine(childRoot, "ready"), "ready");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

var root = Path.Combine(Path.GetTempPath(), "bili-live-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var preserveRoot = false;
try
{
    var recordings = Path.Combine(root, "recordings"); Directory.CreateDirectory(recordings);
    var settings = Path.Combine(root, "live-settings.json");
    await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new LivePreferences(recordings, [])));
    using (var failing = new LiveManager("", root, refreshRoom: _ => Task.FromException(new HttpRequestException("fixture-secret", null, System.Net.HttpStatusCode.ServiceUnavailable))))
    {
        try { await failing.AddRoomAsync("2147483647"); throw new Exception("Failed room refresh was accepted"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("自动录制已暂停", StringComparison.Ordinal) && ex.Message.Contains("HTTP 503", StringComparison.Ordinal) && !ex.Message.Contains("fixture-secret", StringComparison.Ordinal)) { }
        var pending = failing.Rooms().Single();
        if (pending.AutoRecord || pending.Recording) throw new Exception("Failed room lookup started recording");
    }
    using (var failingRestart = new LiveManager("", root))
    {
        var pending = failingRestart.Rooms().Single();
        if (pending.AutoRecord) throw new Exception("Failed room lookup did not persist paused state");
        failingRestart.RemoveRoom(pending.Id);
    }
    Console.WriteLine("Failed room lookup remains visible with automatic recording paused after restart.");
    using (var manager = new LiveManager("SESSDATA=fixture-secret", root))
    {
        try { using var second = new LiveManager("", root); throw new Exception("Live lock did not prevent another recorder"); } catch (IOException) { }
        var recorderConfig = (BililiveRecorder.Core.Config.V3.ConfigV3)typeof(LiveManager).GetField("config", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!;
        if (recorderConfig.Global.NetworkTransportAllowedAddressFamily != AllowedAddressFamily.System || recorderConfig.Global.TimingStreamConnect != 10000 || recorderConfig.Global.TimingApiTimeout != 20000) throw new Exception("Reliable live network defaults not configured");
        var setError = typeof(LiveManager).GetMethod("SetError", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var clearError = typeof(LiveManager).GetMethod("ClearRoomError", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        setError.Invoke(manager, ["room-error", 10]); clearError.Invoke(manager, [11]);
        if (manager.LastError != "room-error") throw new Exception("Successful recording cleared another room's error");
        clearError.Invoke(manager, [10]); if (manager.LastError is not null) throw new Exception("Successful recording did not clear its own stale error");
        setError.Invoke(manager, ["journal-error", null]); clearError.Invoke(manager, [10]);
        if (manager.LastError != "journal-error") throw new Exception("Successful recording cleared an unrelated journal error");
        var reported = new List<(int? Room, string Message)>();
        using (var logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(new RecorderErrorSink((roomId, message) => reported.Add((roomId, message)))).CreateLogger())
            logger.ForContext(LoggingContext.RoomId, 10).Warning(new TaskCanceledException("fixture-secret"), "启动录制出错");
        if (reported.Count != 1 || reported[0].Room != 10 || !reported[0].Message.Contains("超时", StringComparison.Ordinal) || reported[0].Message.Contains("fixture-secret", StringComparison.Ordinal)) throw new Exception("Recorder timeout was not safely reported.");
        manager.AddRoom("2147483647");
        var room = manager.Rooms().Single();
        var configured = new LiveRecordingOptions(RecordMode.Standard, "hevc400,avc400,avc150", CuttingMode.ByTime, 15, true, true, true);
        manager.SetOptions(configured);
        if (manager.Options != configured) throw new Exception("Recording options did not apply");
        try { manager.SetOptions(configured with { Mode = RecordMode.RawData }); throw new Exception("Raw mode accepted incompatible split settings"); } catch (ArgumentException) { }
        try { manager.SetOptions(configured with { Quality = "bad;value" }); throw new Exception("Invalid quality accepted"); } catch (ArgumentException) { }
        manager.ToggleAuto(room.Id);
        if (manager.Rooms().Single().AutoRecord) throw new Exception("Auto-record toggle failed");
        var saved = await File.ReadAllTextAsync(settings);
        if (saved.Contains("fixture-secret") || !saved.Contains("2147483647")) throw new Exception("Live settings missing room or contain cookie");
        await File.AppendAllTextAsync(Path.Combine(root, "live-events.jsonl"), JsonSerializer.Serialize(new LiveEvent("file", Guid.NewGuid(), 123, DateTimeOffset.UtcNow, "fixture.flv")) + "\nnot-json\n");
        if (manager.ReadEvents().Count != 1 || manager.LastError is null) throw new Exception("Live journal recovery failed");
    }
    using (var manager = new LiveManager("", root))
    {
        var room = manager.Rooms().Single();
        if (room.RoomId != 2147483647 || room.AutoRecord) throw new Exception("Live rooms did not persist");
        if (manager.Options.Quality != "hevc400,avc400,avc150" || manager.Options.Cut != CuttingMode.ByTime || manager.Options.CutAmount != 15 || !manager.Options.Danmaku) throw new Exception("Recording options did not persist");
        manager.RemoveRoom(room.Id);
        if (manager.Rooms().Count != 0) throw new Exception("Room removal failed");
    }
    Console.WriteLine("Recorder core initialization, room persistence, lock, credential exclusion, and journal recovery passed.");

    async Task Generate(string path, string size) => await MediaDownload.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", $"color=c=black:s={size}:r=10", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "1", "-c:v", "libx264", "-c:a", "aac", "-f", "flv", path]);
    var one = Path.Combine(recordings, "part'1.flv");
    var two = Path.Combine(recordings, "part2.flv");
    var different = Path.Combine(recordings, "different.flv");
    await Generate(one, "64x64"); await Generate(two, "64x64"); await Generate(different, "96x64");
    var merged = Path.Combine(recordings, "merged.mkv");
    await LiveMerge.MergeAsync([one, two], merged);
    if (!File.Exists(merged) || !File.Exists(one) || !File.Exists(two)) throw new Exception("Merge output or originals missing");
    try { await LiveMerge.MergeAsync([one, different], Path.Combine(recordings, "bad.mkv")); throw new Exception("Incompatible streams were accepted"); } catch (InvalidOperationException) { }
    try { await LiveMerge.MergeAsync([one, two], merged); throw new Exception("Existing output was overwritten"); } catch (IOException) { }
    Console.WriteLine("Compatible live segment merge, quoted filename, incompatible rejection, and original preservation passed.");

    var firstSession = Guid.NewGuid(); var secondSession = Guid.NewGuid(); var oldSession = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
    var journal = new List<LiveEvent> {
        new("started", oldSession, 6, now.AddHours(-1)), new("file", oldSession, 6, now.AddMinutes(-50), "old.flv"), new("ended", oldSession, 6, now.AddMinutes(-50)),
        new("started", firstSession, 6, now), new("file", firstSession, 6, now.AddSeconds(10), "one.flv"), new("ended", firstSession, 6, now.AddSeconds(10)),
        new("started", secondSession, 6, now.AddSeconds(12)), new("file", secondSession, 6, now.AddSeconds(20), "two.flv"), new("ended", secondSession, 6, now.AddSeconds(20)) };
    var grouped = LiveSessionGrouping.RelatedFiles(journal, 6, secondSession, TimeSpan.FromMinutes(2));
    if (grouped.Count != 2 || grouped[0] != "one.flv" || grouped[1] != "two.flv") throw new Exception("Reconnect session grouping failed");
    Console.WriteLine("Reconnect grouping preserves order and excludes old sessions.");

    var recoveryRoot = Path.Combine(root, "recovery"); Directory.CreateDirectory(recoveryRoot);
    await File.WriteAllTextAsync(Path.Combine(recoveryRoot, "live-settings.json"), JsonSerializer.Serialize(new LivePreferences(recordings, [new LiveRoomSetting(2147483647, false)])));
    var recoverySession = Guid.NewGuid();
    var recoveryEvents = new[] { new LiveEvent("started", recoverySession, 2147483647, now), new LiveEvent("file", recoverySession, 2147483647, now.AddSeconds(1), one), new LiveEvent("file", recoverySession, 2147483647, now.AddSeconds(2), two), new LiveEvent("ended", recoverySession, 2147483647, now.AddSeconds(3)) };
    await File.WriteAllLinesAsync(Path.Combine(recoveryRoot, "live-events.jsonl"), recoveryEvents.Select(item => JsonSerializer.Serialize(item)));
    using (var recovered = new LiveManager("", recoveryRoot, TimeSpan.FromMilliseconds(100)))
    {
        for (var i = 0; i < 100 && !recovered.ReadEvents().Any(x => x.Kind is "merge-complete" or "merge-failed"); i++) await Task.Delay(100);
        var result = recovered.ReadEvents().LastOrDefault(x => x.Kind is "merge-complete" or "merge-failed");
        if (result?.Kind != "merge-complete") throw new Exception("Pending merge recovery failed: " + result?.Path);
    }
    Console.WriteLine("Pending ended-session merge recovered after restart.");

    var multiRoot = Path.Combine(root, "multi-recovery"); Directory.CreateDirectory(multiRoot);
    await File.WriteAllTextAsync(Path.Combine(multiRoot, "live-settings.json"), JsonSerializer.Serialize(new LivePreferences(recordings, [new LiveRoomSetting(2147483647, false)])));
    var earlier = Guid.NewGuid(); var middle = Guid.NewGuid(); var latest = Guid.NewGuid();
    var multiEvents = new[] {
        new LiveEvent("started", earlier, 2147483647, now.AddHours(-2)), new LiveEvent("file", earlier, 2147483647, now.AddHours(-2).AddSeconds(1), one), new LiveEvent("file", earlier, 2147483647, now.AddHours(-2).AddSeconds(2), two), new LiveEvent("ended", earlier, 2147483647, now.AddHours(-2).AddSeconds(3)),
        new LiveEvent("started", middle, 2147483647, now.AddMinutes(-1)), new LiveEvent("file", middle, 2147483647, now.AddSeconds(-55), one), new LiveEvent("ended", middle, 2147483647, now.AddSeconds(-50)),
        new LiveEvent("started", latest, 2147483647, now.AddSeconds(-48)), new LiveEvent("file", latest, 2147483647, now.AddSeconds(-40), two), new LiveEvent("ended", latest, 2147483647, now.AddSeconds(-39))
    };
    await File.WriteAllLinesAsync(Path.Combine(multiRoot, "live-events.jsonl"), multiEvents.Select(item => JsonSerializer.Serialize(item)));
    using (var recovered = new LiveManager("", multiRoot, TimeSpan.FromMilliseconds(100)))
    {
        for (var i = 0; i < 150 && recovered.ReadEvents().Count(x => x.Kind is "merge-complete" or "merge-failed") < 2; i++) await Task.Delay(100);
        var events = recovered.ReadEvents();
        var complete = events.Where(x => x.Kind == "merge-complete").Select(x => x.SessionId).ToHashSet();
        if (!complete.SetEquals([earlier, latest]) || events.Any(x => x.Kind == "merge-failed") || events.Any(x => x.Kind == "merge-complete" && x.SessionId == middle)) throw new Exception("Multiple pending session groups were not recovered correctly");
    }
    using (var recoveredAgain = new LiveManager("", multiRoot, TimeSpan.FromMilliseconds(100)))
    {
        await Task.Delay(500);
        if (recoveredAgain.ReadEvents().Count(x => x.Kind == "merge-complete") != 2) throw new Exception("Repeated startup duplicated recovered groups");
    }
    Console.WriteLine("Multiple disjoint groups recovered without duplicate adjacent-session merges.");

    var crashRoot = Path.Combine(root, "crash-recovery"); Directory.CreateDirectory(crashRoot);
    await File.WriteAllTextAsync(Path.Combine(crashRoot, "live-settings.json"), JsonSerializer.Serialize(new LivePreferences(recordings, [new LiveRoomSetting(2147483647, false)])));
    var interruptedSession = Guid.NewGuid();
    var outside = Path.Combine(root, "outside.flv"); await Generate(outside, "64x64");
    var crashEvents = new[] {
        new LiveEvent("started", interruptedSession, 2147483647, now.AddSeconds(-10)),
        new LiveEvent("file-open", interruptedSession, 2147483647, now.AddSeconds(-9), one),
        new LiveEvent("file", interruptedSession, 2147483647, now.AddSeconds(-8), one),
        new LiveEvent("file-open", interruptedSession, 2147483647, now.AddSeconds(-7), two),
        new LiveEvent("file-open", interruptedSession, 2147483647, now.AddSeconds(-6), outside)
    };
    await File.WriteAllLinesAsync(Path.Combine(crashRoot, "live-events.jsonl"), crashEvents.Select(item => JsonSerializer.Serialize(item)));
    using (var recovered = new LiveManager("", crashRoot, TimeSpan.FromMilliseconds(100)))
    {
        for (var i = 0; i < 100 && !recovered.ReadEvents().Any(x => x.Kind is "merge-complete" or "merge-failed"); i++) await Task.Delay(100);
        var events = recovered.ReadEvents();
        if (events.Count(x => x.Kind == "file" && x.SessionId == interruptedSession) != 2 || events.Any(x => x.Kind == "file" && x.Path == outside) || events.Count(x => x.Kind == "ended" && x.SessionId == interruptedSession) != 1) throw new Exception("Interrupted-session journal recovery failed");
        if (events.LastOrDefault(x => x.Kind is "merge-complete" or "merge-failed")?.Kind != "merge-complete") throw new Exception("Interrupted-session merge recovery failed");
    }
    using (var recoveredAgain = new LiveManager("", crashRoot, TimeSpan.FromMilliseconds(100)))
    {
        await Task.Delay(500);
        var events = recoveredAgain.ReadEvents();
        if (events.Count(x => x.Kind == "file" && x.SessionId == interruptedSession) != 2 || events.Count(x => x.Kind == "ended" && x.SessionId == interruptedSession) != 1) throw new Exception("Interrupted-session recovery was not idempotent");
    }
    Console.WriteLine("Interrupted open-file recovery, directory boundary, merge and restart idempotency passed.");

    if (args.Contains("--live-crash"))
    {
        preserveRoot = true;
        var childRoot = Path.Combine(root, "live-crash-child"); Directory.CreateDirectory(childRoot);
        var childInfo = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        childInfo.ArgumentList.Add("--crash-child"); childInfo.ArgumentList.Add(childRoot);
        using var child = System.Diagnostics.Process.Start(childInfo) ?? throw new Exception("Could not start isolated recorder process.");
        try
        {
            for (var i = 0; i < 450 && !File.Exists(Path.Combine(childRoot, "ready")) && !child.HasExited; i++) await Task.Delay(200);
            if (!File.Exists(Path.Combine(childRoot, "ready"))) throw new Exception(child.HasExited ? "Isolated recorder exited early: " + await child.StandardError.ReadToEndAsync() : "Isolated recorder did not become ready.");
            child.Kill(true); await child.WaitForExitAsync();
            using var restarted = new LiveManager("", childRoot, TimeSpan.FromSeconds(1));
            for (var i = 0; i < 150 && !restarted.ReadEvents().Any(x => x.Kind is "merge-complete" or "merge-failed"); i++) await Task.Delay(200);
            var events = restarted.ReadEvents();
            var merge = events.LastOrDefault(x => x.Kind is "merge-complete" or "merge-failed");
            if (events.Count(x => x.Kind == "file") < 2 || !events.Any(x => x.Kind == "ended") || merge?.Kind != "merge-complete" || merge.Path is null || !File.Exists(merge.Path)) throw new Exception("Live crash recovery failed: " + merge?.Path + "; events=" + string.Join(", ", events.Select(x => $"{x.Kind}:{x.RoomId}")) + "; lastError=" + restarted.LastError);
            if (events.Where(x => x.Kind == "file" && x.Path is not null).Any(x => !File.Exists(x.Path))) throw new Exception("Live crash recovery lost an original file.");
            Console.WriteLine("Real live recording process termination, restart recovery and merge passed.");
            preserveRoot = false;
        }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
    }

    if (args.Contains("--live") || args.Contains("--live-split") || args.Contains("--live-reconnect") || args.Contains("--live-auth-raw"))
    {
        var cookie = "";
        if (args.Contains("--live-auth-raw"))
        {
            var credential = CredentialStore.LoadCredential() ?? throw new Exception("Saved credential not found");
            var signedInApi = new BiliApi(); signedInApi.SetCookie(credential.Cookie); await signedInApi.GetAccountInfoAsync(); cookie = signedInApi.RecorderCookie;
            if (!cookie.Contains("buvid3=", StringComparison.OrdinalIgnoreCase)) throw new Exception("Recorder cookie lacks device identifier");
        }
        var options = args.Contains("--live-auth-raw") ? new LiveRecordingOptions(Mode: RecordMode.RawData) : null;
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new LivePreferences(recordings, [], options)));
        var failRefresh = false;
        using var manager = new LiveManager(cookie, root, TimeSpan.FromSeconds(1), room => failRefresh ? Task.FromException(new HttpRequestException("fixture-secret", null, System.Net.HttpStatusCode.ServiceUnavailable)) : room.RefreshRoomInfoOrThrowAsync());
        var added = await manager.AddRoomAsync("https://live.bilibili.com/6?test=1");
        var room = manager.Rooms().Single();
        if (room.Id != added || string.IsNullOrWhiteSpace(room.Name) || string.IsNullOrWhiteSpace(room.Title) || !room.Streaming) throw new Exception("Live URL did not resolve a currently streaming room with name and title.");
        failRefresh = true;
        try { await manager.StartAsync(room.Id); throw new Exception("Stale live status was accepted after refresh failure"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("HTTP 503", StringComparison.Ordinal) && !ex.Message.Contains("fixture-secret", StringComparison.Ordinal)) { }
        failRefresh = false;
        Console.WriteLine("Stale live state rejected after refresh failure; error text sanitized.");
        try { manager.AddRoom("6"); throw new Exception("Duplicate numeric room ID accepted"); } catch (InvalidOperationException) { }
        await manager.StartAsync(room.Id);
        var started = false;
        for (var i = 0; i < 100; i++)
        {
            if (manager.ReadEvents().Any(x => x.Kind == "started")) { started = true; break; }
            await Task.Delay(300);
        }
        if (!started) throw new Exception("Live recording did not start within 30 seconds.");
        if (args.Contains("--live-reconnect"))
        {
            await Task.Delay(TimeSpan.FromSeconds(6)); manager.Stop(room.Id);
            for (var i = 0; i < 100 && manager.Rooms().Single().Recording; i++) await Task.Delay(200);
            manager.Start(room.Id);
            for (var i = 0; i < 100 && manager.ReadEvents().Count(x => x.Kind == "started") < 2; i++) await Task.Delay(300);
            if (manager.ReadEvents().Count(x => x.Kind == "started") < 2) throw new Exception("Second live session did not start.");
            await Task.Delay(TimeSpan.FromSeconds(6));
        }
        else if (args.Contains("--live-split"))
        {
            await Task.Delay(TimeSpan.FromSeconds(6)); manager.SplitForTest(room.Id); await Task.Delay(TimeSpan.FromSeconds(8));
        }
        else await Task.Delay(TimeSpan.FromSeconds(10));
        manager.Stop(room.Id);
        for (var i = 0; i < 100 && manager.Rooms().Single().Recording; i++) await Task.Delay(200);
        var closed = manager.ReadEvents().LastOrDefault(x => x.Kind == "file");
        if (closed?.Path is not string file || !File.Exists(file) || new FileInfo(file).Length == 0) throw new Exception("Recorded file event missing.");
        var streams = await MediaDownload.RunAsync("ffprobe", ["-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", file]);
        if (!streams.Contains("video")) throw new Exception("Live output has no video stream.");
        Console.WriteLine($"Live room 6 recording passed: {new FileInfo(file).Length} bytes; ffprobe found video.");
        if (args.Contains("--live-split") || args.Contains("--live-reconnect"))
        {
            for (var i = 0; i < 150 && !manager.ReadEvents().Any(x => x.Kind is "merge-complete" or "merge-failed"); i++) await Task.Delay(200);
            var merge = manager.ReadEvents().LastOrDefault(x => x.Kind is "merge-complete" or "merge-failed");
            if (merge?.Kind != "merge-complete" || merge.Path is null || !File.Exists(merge.Path)) throw new Exception("Live automatic merge did not complete: " + merge?.Path);
            Console.WriteLine("Live segments were automatically merged and originals preserved.");
        }
    }
}
finally { if (preserveRoot) Console.WriteLine("Live test artifacts retained at " + root); else Directory.Delete(root, true); }

sealed class LiveShortLinkFixture : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host != "b23.tv" || request.Headers.Contains("Cookie")) throw new Exception("Live short link leaked credentials or followed an untrusted redirect");
        var location = request.RequestUri.AbsolutePath switch
        {
            "/start" => "/next",
            "/next" => "https://live.bilibili.com/6?from=share",
            "/evil" => "https://example.com/6",
            "/insecure" => "http://live.bilibili.com/6",
            "/loop" => "/loop",
            _ => throw new Exception("Unexpected short link")
        };
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return Task.FromResult(response);
    }
}

sealed class HangingLiveStream : Stream
{
    public bool WasDisposed { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
}
