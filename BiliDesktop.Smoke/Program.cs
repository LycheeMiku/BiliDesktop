using BiliDesktop;

var releaseJson = """[{"tag_name":"beta1","draft":false,"published_at":"2026-01-01T00:00:00Z","body":"old"},{"tag_name":"beta2","draft":false,"prerelease":true,"published_at":"2026-02-01T00:00:00Z","body":"new notes","html_url":"https://evil.example/"},{"tag_name":"beta10","draft":true,"published_at":"2026-03-01T00:00:00Z"}]""";
var newRelease = UpdateChecker.ParseReleases(releaseJson, "beta1");
if (newRelease?.Version != "beta2" || newRelease.Notes != "new notes" || newRelease.GithubUrl.AbsoluteUri != "https://github.com/LycheeMiku/BiliDesktop/releases/tag/beta2") throw new Exception("Update release parsing, prerelease selection or safe link failed");
if (UpdateChecker.ParseReleases("[]", "beta1") is not null || UpdateChecker.ParseReleases(releaseJson, "beta2") is not null) throw new Exception("Empty or current release was treated as an update");
var numericReleases = """[{"tag_name":"v1.2.0-rc.9","draft":false,"published_at":"2026-01-01T00:00:00Z"},{"tag_name":"v1.2.0","draft":false,"published_at":"2026-01-02T00:00:00Z"},{"tag_name":"v1.1.9","draft":false,"published_at":"2026-01-03T00:00:00Z"}]""";
if (UpdateChecker.ParseReleases(numericReleases, "v1.2.0-rc.8")?.Version != "v1.2.0" || UpdateChecker.ParseReleases(numericReleases, "v1.2.0") is not null || UpdateChecker.ParseReleases("""[{"tag_name":"beta10","draft":false,"published_at":"2026-01-01T00:00:00Z"}]""", "beta2")?.Version != "beta10") throw new Exception("Release version ordering failed");
if (UpdateChecker.ParseReleases("""[{"tag_name":"v1.2.1+build.7","draft":false,"published_at":"2026-01-01T00:00:00Z"}]""", "v1.2.0")?.Version != "v1.2.1+build.7") throw new Exception("Release build metadata was not accepted");
if (!UpdateChecker.IsMainlandTimeZone("China Standard Time") || !UpdateChecker.IsMainlandTimeZone("Asia/Urumqi") || UpdateChecker.IsMainlandTimeZone("Asia/Hong_Kong") || UpdateChecker.Sources("Asia/Shanghai").Count != 5 || UpdateChecker.Sources("Europe/Berlin").Count != 1) throw new Exception("Mainland update source selection failed");
var fallbackHandler = new UpdateFixtureHandler((request, _) =>
{
    if (request.RequestUri!.Host == "api.github.com") return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway));
    if (request.RequestUri.Host == "gh.dpik.top") return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("invalid") });
    if (request.RequestUri.Host == "gh-proxy.com") { var redirect = new HttpResponseMessage(System.Net.HttpStatusCode.Redirect); redirect.Headers.Location = new Uri("https://evil.example/releases"); return Task.FromResult(redirect); }
    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(releaseJson.Replace("beta2", "beta4")) });
});
using (var client = new HttpClient(fallbackHandler))
{
    var checkedRelease = await new UpdateChecker(client, TimeSpan.FromSeconds(1)).CheckAsync("Asia/Shanghai");
    if (checkedRelease?.Version != "beta4" || !fallbackHandler.Hosts.SequenceEqual(["api.github.com", "gh.dpik.top", "gh-proxy.com", "hk.gh-proxy.com"]) || fallbackHandler.SentCookie) throw new Exception("Update mirror fallback, ordering or credential isolation failed");
}
var failureHandler = new UpdateFixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)));
using (var client = new HttpClient(failureHandler))
{
    try { await new UpdateChecker(client, TimeSpan.FromSeconds(1)).CheckAsync("Asia/Shanghai"); throw new Exception("All update source failures were ignored"); } catch (InvalidOperationException ex) when (ex.Message.Contains("更新检查失败")) { }
    if (failureHandler.Hosts.Count != 5) throw new Exception("Not all requested mirrors were attempted");
}
var redirectHandler = new UpdateFixtureHandler((request, _) =>
{
    if (request.RequestUri!.Host == "hk.gh-proxy.com") { var response = new HttpResponseMessage(System.Net.HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://hk.gh-proxy.org/https:/api.github.com/repos/LycheeMiku/BiliDesktop/releases?per_page=100"); return Task.FromResult(response); }
    if (request.RequestUri.Host == "hk.gh-proxy.org") return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("[]") });
    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
});
using (var client = new HttpClient(redirectHandler))
{
    if (await new UpdateChecker(client, TimeSpan.FromSeconds(1)).CheckAsync("China Standard Time") is not null || !redirectHandler.Hosts.SequenceEqual(["api.github.com", "gh.dpik.top", "gh-proxy.com", "hk.gh-proxy.com", "hk.gh-proxy.org"])) throw new Exception("Allowed mirror redirect failed");
}
var cancellationHandler = new UpdateFixtureHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); throw new Exception("Unexpected completion"); });
using (var client = new HttpClient(cancellationHandler))
using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(40)))
{
    try { await new UpdateChecker(client, TimeSpan.FromSeconds(1)).CheckAsync("Asia/Shanghai", cancel.Token); throw new Exception("Update cancellation was ignored"); } catch (OperationCanceledException) { }
    if (cancellationHandler.Hosts.Count != 1) throw new Exception("Canceled update check continued to mirrors");
}
var timeoutHandler = new UpdateFixtureHandler(async (request, token) =>
{
    if (request.RequestUri!.Host == "api.github.com") { await Task.Delay(Timeout.Infinite, token); throw new Exception("Unexpected completion"); }
    return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("[]") };
});
using (var client = new HttpClient(timeoutHandler))
{
    if (await new UpdateChecker(client, TimeSpan.FromMilliseconds(80)).CheckAsync("Asia/Shanghai") is not null || !timeoutHandler.Hosts.SequenceEqual(["api.github.com", "gh.dpik.top"])) throw new Exception("Timed-out direct connection did not try the next source");
}
var directFailure = new UpdateFixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)));
using (var client = new HttpClient(directFailure))
{
    try { await new UpdateChecker(client, TimeSpan.FromSeconds(1)).CheckAsync("Europe/Berlin"); throw new Exception("Update failure was ignored"); } catch (InvalidOperationException ex) when (ex.Message.Contains("更新检查失败")) { }
    if (directFailure.Hosts.Count != 1) throw new Exception("Mirrors were used outside mainland timezone");
}
try { UpdateChecker.ParseReleases("{}", "beta1"); throw new Exception("Invalid release response was accepted"); } catch (System.IO.InvalidDataException) { }
Console.WriteLine("Release versions, empty lists, mirror fallback, failure and credential-free checks passed.");

if (BiliApi.ExtractBvid("https://www.bilibili.com/video/BV1xx411c7mD?p=2") != "BV1xx411c7mD") throw new Exception("BV parsing failed");
if (!BiliApi.TryParseCollectionLink("https://space.bilibili.com/123/channel/collectiondetail?sid=456", out var ownerId, out var seasonId) || ownerId != 123 || seasonId != 456) throw new Exception("Collection link parsing failed");
if (BiliApi.TryParseCollectionLink("https://space.bilibili.com.evil.test/123/channel/collectiondetail?sid=456", out _, out _)) throw new Exception("Untrusted collection domain accepted");
if (!BiliApi.TryParseUploader("https://space.bilibili.com/123/video", out var uploader) || uploader != 123 || !BiliApi.TryParseUploader("123", out _)) throw new Exception("Uploader input parsing failed");
if (BiliApi.TryParseUploader("https://space.bilibili.com.evil.test/123", out _)) throw new Exception("Untrusted uploader domain accepted");
if (!BiliApi.TryParseAvid("AV123", out var avid) || avid != 123 || !BiliApi.TryParseAvid("https://www.bilibili.com/video/av123?p=2", out avid) || avid != 123) throw new Exception("AV parsing failed");
if (BiliApi.TryParseAvid("https://www.bilibili.com.evil.test/video/av123", out _) || BiliApi.TryParseAvid("av0", out _) || BiliApi.TryParseAvid("av123extra", out _)) throw new Exception("Invalid AV accepted");
if (BiliApi.RequestedPage("https://www.bilibili.com/video/BV1xx411c7mD?p=2", 3) != 1 || BiliApi.RequestedPage("https://www.bilibili.com/video/BV1xx411c7mD?p=99", 3) != 0 || BiliApi.RequestedPage("https://www.bilibili.com/video/BV1xx411c7mD?p=1&p=2", 3) != 0 || BiliApi.RequestedPage("https://bilibili.com.evil.test/video/BV1xx411c7mD?p=2", 3) != 0) throw new Exception("Page selection failed");
if (!BiliApi.TryParsePgc("https://www.bilibili.com/bangumi/play/ep123?from=search", out var pgcKind, out var pgcId) || pgcKind != "ep" || pgcId != 123 || !BiliApi.TryParsePgc("ss456", out _, out _)) throw new Exception("PGC link parsing failed");
if (BiliApi.TryParsePgc("https://www.bilibili.com.evil.test/bangumi/play/ep123", out _, out _)) throw new Exception("Untrusted PGC domain accepted");
if (!BiliApi.TryParseCourse("https://www.bilibili.com/cheese/play/ep790?x=1", out var courseKind, out var courseId) || courseKind != "ep" || courseId != 790 || !BiliApi.TryParseCourse("https://www.bilibili.com/cheese/play/ss61", out _, out _)) throw new Exception("Course URL parsing failed");
if (BiliApi.TryParseCourse("https://www.bilibili.com.evil.test/cheese/play/ep790", out _, out _) || BiliApi.TryParseCourse("ep790", out _, out _)) throw new Exception("Invalid course URL accepted");
try { BiliApi.ExtractBvid("invalid"); throw new Exception("Invalid input accepted"); } catch (ArgumentException) { }
Console.WriteLine("Input parsing passed.");
using (var sizeClient = new HttpClient(new SizeProbeFixture()))
{
    var length = await MediaSizeProbe.GetSizeAsync("https://example.test/video", "BiliDesktop-Fixture/1.0", client: sizeClient);
    if (length != 12345678) throw new Exception("Selected stream size did not use the total Content-Range");
    if (await MediaSizeProbe.GetSizeAsync(["https://example.test/fail", "https://example.test/video"], "BiliDesktop-Fixture/1.0", client: sizeClient) != 12345678) throw new Exception("Size probe did not try the backup URL");
    if (await MediaSizeProbe.GetSizeAsync("http://example.test/video", "fixture", client: sizeClient) is not null) throw new Exception("Insecure size URL accepted");
}
using (var canceledSizeClient = new HttpClient(new TimeoutHandler()))
    if (await MediaSizeProbe.GetSizeAsync("https://example.test/video", "fixture", client: canceledSizeClient) is not null) throw new Exception("Canceled stream size request was not handled");
var namingFixture = new VideoInfo("BV1xx411c7mD", "A: title", "author", "", [new(1, "P1 · first"), new(2, "P2 · second")], null, null);
if (!DownloadNaming.NeedsFolder(namingFixture) || !DownloadNaming.Folder(Path.GetTempPath(), namingFixture).Contains("A_ title") || !DownloadNaming.BaseName(namingFixture, namingFixture.Pages[1], 2, new VideoStream(80, "avc1", 100, "https://example.test/v", []), null).Contains("002-P2 · second-1080P-avc1")) throw new Exception("Download naming or grouped folder failed");
if (DownloadNaming.SafeComponent("CON") != "_CON" || DownloadNaming.SafeComponent("..").Contains("..")) throw new Exception("Unsafe download name accepted");
Console.WriteLine("Selected stream size, credential-free probe, naming and grouped folders passed.");
var startupPath = Path.Combine(Path.GetTempPath(), "BiliDesktop Test", "BiliDesktop.exe");
var startupMemory = new MemoryStartupStore();
if (StartupRegistration.GetState(startupMemory, startupPath) != StartupState.Disabled) throw new Exception("Startup should start disabled");
StartupRegistration.SetEnabled(startupMemory, startupPath, true);
if (StartupRegistration.GetState(startupMemory, startupPath) != StartupState.Enabled || startupMemory.Value is not string startupCommand || !startupCommand.StartsWith('"') || !startupCommand.EndsWith("\" --background", StringComparison.Ordinal)) throw new Exception("Startup command or registration failed");
StartupRegistration.SetEnabled(startupMemory, startupPath, true);
if (startupMemory.Writes != 1) throw new Exception("Startup enable was not idempotent");
StartupRegistration.SetEnabled(startupMemory, startupPath, false);
if (StartupRegistration.GetState(startupMemory, startupPath) != StartupState.Disabled || startupMemory.Deletes != 1) throw new Exception("Startup disable failed");
var olderStartupPath = Path.Combine(Path.GetTempPath(), "BiliDesktop Older", "BiliDesktop.exe");
startupMemory.Value = StartupRegistration.CommandFor(olderStartupPath);
if (StartupRegistration.GetState(startupMemory, startupPath) != StartupState.OtherVersion) throw new Exception("Older application startup was not recognized");
StartupRegistration.SetEnabled(startupMemory, startupPath, true);
if (StartupRegistration.GetState(startupMemory, startupPath) != StartupState.Enabled || !Equals(startupMemory.Value, StartupRegistration.CommandFor(startupPath))) throw new Exception("Older application startup could not be switched to the current version");
StartupRegistration.SetEnabled(startupMemory, startupPath, false);
startupMemory.Value = "foreign command";
foreach (var enabled in new[] { false, true })
{
    try { StartupRegistration.SetEnabled(startupMemory, startupPath, enabled); throw new Exception("Foreign startup entry was modified"); }
    catch (InvalidOperationException) { }
}
if (!Equals(startupMemory.Value, "foreign command")) throw new Exception("Foreign startup entry changed");
startupMemory.Value = "\"C:\\other\\other.exe\" --background";
if (StartupRegistration.GetState(startupMemory, startupPath) != StartupState.Other) throw new Exception("Foreign executable mistaken for another version");
try { StartupRegistration.CommandFor("relative.exe"); throw new Exception("Relative startup path accepted"); } catch (ArgumentException) { }
try { StartupRegistration.CommandFor(startupPath + "\r\n"); throw new Exception("Unsafe startup path accepted"); } catch (ArgumentException) { }
if (OperatingSystem.IsWindows())
{
    var testKey = @"Software\BiliDesktop-StartupSmoke-" + Guid.NewGuid().ToString("N");
    try
    {
        var registryStore = new WindowsStartupStore(testKey, "test");
        StartupRegistration.SetEnabled(registryStore, startupPath, true);
        if (StartupRegistration.GetState(registryStore, startupPath) != StartupState.Enabled) throw new Exception("Registry startup roundtrip failed");
        StartupRegistration.SetEnabled(registryStore, startupPath, false);
        if (StartupRegistration.GetState(registryStore, startupPath) != StartupState.Disabled) throw new Exception("Registry startup removal failed");
        registryStore.Write(StartupRegistration.CommandFor(olderStartupPath));
        if (StartupRegistration.GetState(registryStore, startupPath) != StartupState.OtherVersion) throw new Exception("Older startup registry value not recognized");
        StartupRegistration.SetEnabled(registryStore, startupPath, true);
        if (StartupRegistration.GetState(registryStore, startupPath) != StartupState.Enabled) throw new Exception("Older startup registry value not switched");
    }
    finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(testKey, false); }
}
Console.WriteLine("Opt-in startup registration, path quoting, foreign-entry protection and isolated registry roundtrip passed.");
var shortApi = new BiliApi(new ShortLinkHandler());
shortApi.SetCookie("SESSDATA=private-fixture");
if (await shortApi.ResolveVideoLinkAsync("https://b23.tv/start") != "https://www.bilibili.com/video/BV1xx411c7mD?p=2") throw new Exception("Short link resolution failed");
if (await shortApi.ResolveVideoLinkAsync("https://www.bilibili.com/video/BV1xx411c7mD") != "https://www.bilibili.com/video/BV1xx411c7mD") throw new Exception("Ordinary URL changed");
foreach (var path in new[] { "bad", "loop", "insecure" })
{
    try { await shortApi.ResolveVideoLinkAsync("https://b23.tv/" + path); throw new Exception("Unsafe short link accepted: " + path); }
    catch (InvalidOperationException) { }
}
Console.WriteLine("Short links, redirect restrictions, loop detection and cookie isolation passed.");
var executableFixture = Path.Combine(Path.GetTempPath(), "bili-executable-" + Guid.NewGuid().ToString("N"));
var bundledDirectory = Path.Combine(executableFixture, "vendor", "ffmpeg");
Directory.CreateDirectory(bundledDirectory);
var bundledFfmpeg = Path.Combine(bundledDirectory, "ffmpeg.exe");
try
{
    if (MediaDownload.ResolveExecutable("ffmpeg", executableFixture) != "ffmpeg") throw new Exception("PATH fallback failed");
    File.WriteAllText(bundledFfmpeg, "fixture");
    if (MediaDownload.ResolveExecutable("ffmpeg", executableFixture) != bundledFfmpeg) throw new Exception("Bundled FFmpeg not preferred");
    if (MediaDownload.ResolveExecutable("ffprobe", executableFixture) != "ffprobe") throw new Exception("Missing bundled ffprobe fallback failed");
    if (MediaDownload.ResolveExecutable("other", executableFixture) != "other") throw new Exception("Unexpected executable substitution");
}
finally { File.Delete(bundledFfmpeg); Directory.Delete(bundledDirectory); Directory.Delete(Path.Combine(executableFixture, "vendor")); Directory.Delete(executableFixture); }
Console.WriteLine("Bundled FFmpeg discovery and PATH fallback passed.");
var toolsInstalled = false; var checkedTools = new List<string>(); var installCount = 0;
await MediaDownload.ValidateMediaToolsAsync(() => { installCount++; toolsInstalled = true; return Task.CompletedTask; }, name =>
{
    checkedTools.Add(name);
    if (!toolsInstalled && name == "ffprobe") throw new FileNotFoundException();
    return Task.FromResult("fixture");
});
if (installCount != 1 || !checkedTools.SequenceEqual(new[] { "ffmpeg", "ffprobe", "ffmpeg", "ffprobe" })) throw new Exception("Missing media tools did not trigger installation and recheck");
try
{
    await MediaDownload.ValidateMediaToolsAsync(() => { throw new Exception("Installer should not run"); }, _ => throw new InvalidOperationException("Broken binary"));
    throw new Exception("Broken tool unexpectedly accepted");
}
catch (InvalidOperationException ex) when (ex.Message == "Broken binary") { }
Console.WriteLine("Missing media tools trigger installation and recheck; broken tools fail safely.");
var signed = BiliApi.SignWbi(new Dictionary<string, string> { ["foo"] = "114", ["bar"] = "514", ["zab"] = "1919810" }, "ea1db124af3c7062474693fa704f4ff8", 1702204169);
if (signed != "bar=514&foo=114&wts=1702204169&zab=1919810&w_rid=8f6f2b5b3d485fe1886cec6a0be8c5d4") throw new Exception("WBI known vector failed: " + signed);
var fake = new BiliApi(new FixtureHandler());
fake.SetUserAgent("BiliDesktop-Fixture/1.0");
try { fake.SetUserAgent("bad\r\nInjected: value"); throw new Exception("Invalid user agent accepted"); } catch (ArgumentException) { }
var fixtureVideo = await fake.GetVideoAsync("BV1xx411c7mD");
var fixtureAv = await fake.GetVideoAsync("av123");
if (fixtureAv.Bvid != "BV1xx411c7mD") throw new Exception("AV response did not resolve BV");
if (fixtureVideo.Title != "test" || fixtureVideo.Pages.Single().Cid != 42 || fixtureVideo.CollectionId != 456 || fixtureVideo.OwnerId != 123) throw new Exception("Video parsing failed");
var collection = await fake.GetCollectionAsync(123, 456);
if (collection.Count != 31 || collection[30].Bvid != "BV0000000031" || collection[30].CollectionNumber != 31 || collection[0].CollectionTitle != "示例合集") throw new Exception("Collection pagination or metadata failed");
var collectionVideoInfo = await fake.GetVideoAsync("BV0000000001");
if (collectionVideoInfo.CollectionTitle != "示例合集" || collectionVideoInfo.CollectionItemTitle != "单集名称" || collectionVideoInfo.CollectionNumber != 2) throw new Exception("Collection title, item title or sequence was not parsed");
Console.WriteLine("Multi-page collection enumeration passed.");
var uploaderVideos = await fake.GetUploaderVideosAsync(123);
if (uploaderVideos.Count != 31 || uploaderVideos[30].Bvid != "BV0000000031") throw new Exception("Uploader pagination failed");
try { await fake.GetUploaderVideosAsync(123, cancellation: new CancellationToken(true)); throw new Exception("Uploader cancellation failed"); } catch (OperationCanceledException) { }
Console.WriteLine("Uploader pagination, input validation and cancellation passed.");
var favoriteApi = new BiliApi(new FixtureHandler()); favoriteApi.SetUserAgent("BiliDesktop-Fixture/1.0"); favoriteApi.SetCookie("SESSDATA=fixture");
var folders = await favoriteApi.GetFavoriteFoldersAsync();
if (folders.Count != 1 || folders[0].Id != 456) throw new Exception("Favorite folders parsing failed");
var followedFolders = await favoriteApi.GetCollectedFavoriteFoldersAsync();
if (followedFolders.Count != 20 || followedFolders[0].Id != 1001 || followedFolders[^1].Id != 1021) throw new Exception("Followed favorite folder pagination or invalid state filtering failed");
foreach (var mode in new[] { "short", "duplicate" })
{
    var incompleteApi = new BiliApi(new FixtureHandler(mode)); incompleteApi.SetUserAgent("BiliDesktop-Fixture/1.0"); incompleteApi.SetCookie("SESSDATA=fixture");
    try { await incompleteApi.GetCollectedFavoriteFoldersAsync(); throw new Exception("Incomplete or duplicate followed folders accepted"); }
    catch (InvalidOperationException) { }
}
var favorites = await favoriteApi.GetFavoriteContentsAsync(folders[0].Id);
if (favorites.Videos.Count != 19 || favorites.Invalid != 1 || favorites.Unsupported != 1) throw new Exception("Favorite content pagination or classification failed");
try { await favoriteApi.GetFavoriteContentsAsync(456, cancellation: new CancellationToken(true)); throw new Exception("Favorite cancellation failed"); } catch (OperationCanceledException) { }
Console.WriteLine("Favorite account, folders, content pagination, invalid types and cancellation passed.");
var fixtureStreams = await fake.GetStreamsAsync(fixtureVideo.Bvid, 42);
var pgc = await fake.GetPgcAsync("ss456");
if (pgc.Pages.Count != 2 || pgc.Pages[1].EpisodeId != 124 || pgc.Pages[1].Bvid != "BV0000000002") throw new Exception("PGC episode parsing failed");
var pgcStreams = await fake.GetStreamsAsync(pgc.Pages[0].Bvid!, pgc.Pages[0].Cid, pgc.Pages[0].EpisodeId);
if (pgcStreams.Video.Count != 1 || pgcStreams.Video[0].Urls.Count != 2 || pgcStreams.Video[0].Url != "https://example.com/v") throw new Exception("PGC playback or snake_case URLs parsing failed");
if (!(await fake.GetStreamsAsync(pgc.Pages[1].Bvid!, pgc.Pages[1].Cid, pgc.Pages[1].EpisodeId)).IsPreview) throw new Exception("PGC preview was not identified");
var audioOnlyStreams = await fake.GetStreamsAsync("BV0000000001", 3, 125);
if (audioOnlyStreams.Video.Count != 0 || audioOnlyStreams.Audio.Count != 1 || audioOnlyStreams.Audio[0].Urls.Count != 2) throw new Exception("Audio-only DASH or backup_url parsing failed");
if (StreamLabels.Video(80) != "1080P" || StreamLabels.Video(999) != "Q999" || StreamLabels.Audio(30251) != "Hi-Res" || StreamLabels.Audio(30250) != "Dolby Atmos") throw new Exception("Stream quality labels failed");
Console.WriteLine("PGC links, episodes, extra sections and playback parsing passed.");
var course = await fake.GetCourseAsync("https://www.bilibili.com/cheese/play/ss61");
if (course.Bvid != "course-ss61" || course.Pages.Count != 4 || course.Pages[0].CourseAid != 11 || course.Pages[1].Available || course.Pages[2].EpisodeId != 793 || course.Pages[3].Available) throw new Exception("Course pagination or access parsing failed");
var courseStreams = await fake.GetStreamsForPageAsync(course.Bvid, course.Pages[0]);
if (courseStreams.Video.Count != 1 || courseStreams.Audio.Count != 1 || courseStreams.IsPreview) throw new Exception("Course playback parsing failed");
try { await fake.GetStreamsForPageAsync(course.Bvid, course.Pages[1]); throw new Exception("Locked course episode accepted"); }
catch (InvalidOperationException ex) when (ex.Message.Contains("无权观看", StringComparison.Ordinal)) { }
try { await fake.GetStreamsAsync(course.Bvid, 21, 791, 11); throw new Exception("Unsupported segmented course accepted"); }
catch (InvalidOperationException ex) when (ex.Message.Contains("分段流格式", StringComparison.Ordinal)) { }
if (!BatchPolicy.FileName(course.Bvid, 1, "lesson", false).StartsWith("course-ss61_P001_")) throw new Exception("Course batch filename failed");
Console.WriteLine("Course links, episode pagination, permissions, playback and naming passed.");
if (fixtureStreams.Video.Count != 2 || fixtureStreams.Video[0].Quality != 80 || fixtureStreams.Video[0].Urls.Count != 2) throw new Exception("Stream selection or backup URL failed");
if (fixtureStreams.Audio.Count != 3 || fixtureStreams.Audio[0].Quality != 30251 || fixtureStreams.Audio[1].Quality != 30250 || fixtureStreams.Audio[2].Quality != 30280 || fixtureStreams.Audio[0].Urls.Count != 2) throw new Exception("FLAC/Dolby audio selection failed");
var named = BatchPolicy.FileName("BV1xx411c7mD", 2, "../bad\\part: title", false);
if (named.Contains('/') || named.Contains('\\') || named.Contains(':') || !named.StartsWith("BV1xx411c7mD_P002_")) throw new Exception("Unsafe batch filename");
if (!BatchPolicy.FileName("BV1xx411c7mD", 2, "part", true).EndsWith(".m4a") || !BatchPolicy.FileName("BV1xx411c7mD", 2, "part", true, true).EndsWith(".mkv") || !BatchPolicy.FileName("BV1xx411c7mD", 2, "part", false, true).EndsWith(".mkv")) throw new Exception("Batch format filename failed");
var flacChoice = new AudioStream(30251, "flac", 1000, "https://example.com/a", []);
var aacChoice = new AudioStream(30280, "aac", 200, "https://example.com/a", []);
if (!BatchPolicy.ChooseMatroska(BatchFormat.Automatic, flacChoice) || BatchPolicy.ChooseMatroska(BatchFormat.Automatic, aacChoice) || BatchPolicy.ChooseMatroska(BatchFormat.Automatic, null) || BatchPolicy.ChooseMatroska(BatchFormat.Mp4, flacChoice) || !BatchPolicy.ChooseMatroska(BatchFormat.Matroska, aacChoice)) throw new Exception("Automatic or explicit container selection failed");
var preferred = new VideoStream(64, "unavailable", 150, "https://example.com", []);
if (BatchPolicy.ChooseVideo(fixtureStreams.Video, preferred)?.Quality != 32) throw new Exception("Batch quality fallback failed");
if (BatchPolicy.ChooseVideo(fixtureStreams.Video, null)?.Quality != 80) throw new Exception("Batch highest quality failed");
Console.WriteLine("Batch filename and quality preference passed.");
using (var cancel = new CancellationTokenSource())
{
    var active = 0; var peak = 0; var started = 0; var skipped = 0;
    var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var run = BatchScheduler.RunAsync(Enumerable.Range(0, 5), 2, async _ =>
    {
        var now = Interlocked.Increment(ref active);
        if (now > peak) peak = now;
        if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
        await release.Task;
        Interlocked.Decrement(ref active);
    }, _ => Interlocked.Increment(ref skipped), cancel.Token);
    await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancel.Cancel(); release.TrySetResult(); await run;
    if (peak != 2 || started != 2 || skipped != 3) throw new Exception($"Batch concurrency or cancellation failed: peak={peak}, started={started}, skipped={skipped}");
}
Console.WriteLine("Batch concurrency and stopping queued work passed.");
var ariaOptions = MediaDownload.AriaSession.MakeOptions(".", "test", "fixture", 64);
if ((string)ariaOptions["split"] != "64" || (string)ariaOptions["max-connection-per-server"] != "16") throw new Exception("aria2 connection limits failed");
try { MediaDownload.Configure(new(65, 16)); throw new Exception("Invalid connection count accepted"); } catch (ArgumentException) { }
{
    var running = 0; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var tasks = Enumerable.Range(0, 4).Select(_ => MediaDownload.InSlotAsync(async () => { if (Interlocked.Increment(ref running) == 4) entered.TrySetResult(); await release.Task; })).ToArray();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    if (MediaDownload.ActiveCount != 4) throw new Exception("Active download count did not reflect occupied slots");
    var unexpectedlyRan = false;
    var waiting = MediaDownload.InSlotAsync(() => { unexpectedlyRan = true; return Task.CompletedTask; }, canStart: () => false);
    release.TrySetResult(); await Task.WhenAll(tasks);
    try { await waiting; throw new Exception("Changed rule was allowed to start"); } catch (InvalidOperationException) { }
    if (unexpectedlyRan || MediaDownload.ActiveCount != 0) throw new Exception("Global slot guard or active count release failed");
}
Console.WriteLine("64-way configuration, per-server cap and global queue guard passed.");
{
    MediaDownload.Configure(new(8, 16, 2));
    var started = 0; var firstTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thirdEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var tasks = Enumerable.Range(0, 5).Select(_ => MediaDownload.InSlotAsync(async () =>
    {
        var count = Interlocked.Increment(ref started);
        if (count == 2) firstTwo.TrySetResult();
        if (count == 5) thirdEntered.TrySetResult();
        await release.Task;
    })).ToArray();
    try
    {
        await firstTwo.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (started != 2 || MediaDownload.ActiveCount != 2) throw new Exception("Tasks exceeded configured concurrency");
        using var cancellation = new CancellationTokenSource();
        var cancelled = MediaDownload.InSlotAsync(() => throw new Exception("Cancelled waiter started"), cancellation.Token);
        cancellation.Cancel();
        try { await cancelled; throw new Exception("Queued cancellation ignored"); } catch (OperationCanceledException) { }
        MediaDownload.Configure(new(8, 16, 1));
        if (MediaDownload.ActiveCount != 2 || thirdEntered.Task.IsCompleted) throw new Exception("Lowering limit interrupted or admitted tasks");
        MediaDownload.Configure(new(8, 16, 0));
        await thirdEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (MediaDownload.ActiveCount != 5) throw new Exception("Unlimited did not release the waiting tasks");
    }
    finally { release.TrySetResult(); await Task.WhenAll(tasks); MediaDownload.Configure(new(8, 16)); }
    if (MediaDownload.ActiveCount != 0) throw new Exception("Dynamic concurrency leaked slots");
}
Console.WriteLine("Configurable download concurrency, live limit changes, unlimited and queued cancellation passed.");
{
    var controls = Enumerable.Range(0, 4).Select(_ => new TransferControl()).ToArray();
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var pauseNow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var entered = 0;
    var workers = controls.Select(control => MediaDownload.InSlotAsync(async () =>
    {
        if (Interlocked.Increment(ref entered) == 4) ready.TrySetResult();
        await pauseNow.Task; await control.WaitIfPausedAsync();
    }, control.Token, control: control)).ToArray();
    try
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var control in controls) if (!control.Pause()) throw new Exception("Could not pause occupied slot");
        pauseNow.TrySetResult();
        await MediaDownload.InSlotAsync(() => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
        if (workers.Any(task => task.IsCompleted)) throw new Exception("Paused slot worker completed unexpectedly");
        foreach (var control in controls) control.Resume();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5));
    }
    finally { pauseNow.TrySetResult(); foreach (var control in controls) control.Resume(); await Task.WhenAll(workers); foreach (var control in controls) control.Dispose(); }
}
Console.WriteLine("Paused downloads release and reacquire global task slots.");
{
    var slotsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseSlots = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var occupied = 0;
    var blockers = Enumerable.Range(0, 4).Select(_ => MediaDownload.InSlotAsync(async () => { if (Interlocked.Increment(ref occupied) == 4) slotsEntered.TrySetResult(); await releaseSlots.Task; })).ToArray();
    Task? queued = null; Task? queuedSingle = null; Task? queuedChangedSession = null; Task? queuedCollision = null;
    var collisionPath = "";
    try
    {
        await slotsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var handler = new FixtureHandler(previewVideo: true);
        var apiQueued = new BiliApi(handler); apiQueued.SetUserAgent("BiliDesktop-Fixture/1.0");
        var job = new BatchJob { Number = 1, Bvid = "BV1xx411c7mD", Label = "fixture", Page = new VideoPage(42, Guid.NewGuid().ToString("N")) };
        queued = new BatchDownloadEngine(apiQueued, null, null).RunJobAsync(job, Path.GetTempPath(), 0, BatchFormat.Automatic, apiQueued.UserAgent);
        var singleHandler = new FixtureHandler(previewVideo: true);
        var singleApi = new BiliApi(singleHandler); singleApi.SetUserAgent("BiliDesktop-Fixture/1.0");
        var singlePath = Path.Combine(Path.GetTempPath(), "bili-single-" + Guid.NewGuid().ToString("N") + ".mp4");
        queuedSingle = MediaDownload.DownloadFreshAsync(singleApi, "BV1xx411c7mD", new VideoPage(42, "fixture"), new VideoStream(80, "avc", 200, "https://example.com/a", []), null, false, singleApi.SessionVersion, singlePath, singleApi.UserAgent, new CaptureProgress());
        var changedHandler = new FixtureHandler(previewVideo: true);
        var changedApi = new BiliApi(changedHandler); changedApi.SetUserAgent("BiliDesktop-Fixture/1.0");
        var changedJob = new BatchJob { Number = 1, Bvid = "BV1xx411c7mD", Label = "fixture", Page = new VideoPage(42, Guid.NewGuid().ToString("N")) };
        queuedChangedSession = new BatchDownloadEngine(changedApi, null, null).RunJobAsync(changedJob, Path.GetTempPath(), 0, BatchFormat.Automatic, changedApi.UserAgent);
        changedApi.SetCookie("SESSDATA=fixture");
        var collisionHandler = new FixtureHandler(previewVideo: true);
        var collisionApi = new BiliApi(collisionHandler); collisionApi.SetUserAgent("BiliDesktop-Fixture/1.0");
        var collisionLabel = Guid.NewGuid().ToString("N");
        var collisionJob = new BatchJob { Number = 1, Bvid = "BV1xx411c7mD", Label = "fixture", Page = new VideoPage(42, collisionLabel) };
        collisionPath = Path.Combine(Path.GetTempPath(), BatchPolicy.FileName(collisionJob.Bvid, 1, collisionLabel, false));
        queuedCollision = new BatchDownloadEngine(collisionApi, null, null).RunJobAsync(collisionJob, Path.GetTempPath(), 0, BatchFormat.Automatic, collisionApi.UserAgent);
        await File.WriteAllTextAsync(collisionPath, "other task output");
        await Task.Delay(200);
        if (handler.StreamRequests != 0 || singleHandler.StreamRequests != 0 || changedHandler.StreamRequests != 0 || job.Status != "等待下载任务…") throw new Exception("Batch or single download fetched streams while waiting for a slot");
        releaseSlots.TrySetResult(); await queued;
        if (handler.StreamRequests != 1 || !job.Status.Contains("预览", StringComparison.Ordinal)) throw new Exception("Batch did not request fresh streams or reject preview after acquiring a slot");
        try { await queuedSingle; throw new Exception("Unexpected preview accepted by single download"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("预览", StringComparison.Ordinal)) { }
        queuedSingle = null;
        if (singleHandler.StreamRequests != 1 || File.Exists(singlePath)) throw new Exception("Single download did not refresh or safely reject preview");
        await queuedChangedSession;
        if (changedHandler.StreamRequests != 0 || !changedJob.Status.Contains("会话已变化", StringComparison.Ordinal)) throw new Exception("Queued batch did not reject changed login session");
        await queuedCollision;
        if (collisionHandler.StreamRequests != 0 || collisionJob.Status != "文件已存在，跳过" || await File.ReadAllTextAsync(collisionPath) != "other task output") throw new Exception("Output appearing in queue was not skipped safely");
    }
    finally { releaseSlots.TrySetResult(); await Task.WhenAll(blockers); if (queued is not null) await queued; if (queuedChangedSession is not null) await queuedChangedSession; if (queuedCollision is not null) await queuedCollision; if (queuedSingle is not null) { try { await queuedSingle; } catch (InvalidOperationException) { } } if (collisionPath.Length > 0) File.Delete(collisionPath); }
}
Console.WriteLine("Queued single and batch stream lookups wait for a download slot and reject unexpected previews.");
{
    var handler = new FixtureHandler(); var sessionApi = new BiliApi(handler); sessionApi.SetUserAgent("BiliDesktop-Fixture/1.0");
    var oldSession = sessionApi.SessionVersion; sessionApi.SetCookie("SESSDATA=fixture");
    var selected = new VideoStream(80, "avc", 200, "https://example.com/b", []);
    var path = Path.Combine(Path.GetTempPath(), "bili-single-" + Guid.NewGuid().ToString("N") + ".mp4");
    try { await MediaDownload.DownloadFreshAsync(sessionApi, "BV1xx411c7mD", new VideoPage(42, "fixture"), selected, null, false, oldSession, path, sessionApi.UserAgent, new CaptureProgress()); throw new Exception("Changed login session accepted"); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("会话已变化", StringComparison.Ordinal)) { }
    if (handler.StreamRequests != 0 || File.Exists(path)) throw new Exception("Changed session fetched streams or wrote output");
    var missing = new VideoStream(999, "unknown", 1, "https://example.com/missing", []);
    try { await MediaDownload.DownloadFreshAsync(sessionApi, "BV1xx411c7mD", new VideoPage(42, "fixture"), missing, null, false, sessionApi.SessionVersion, path, sessionApi.UserAgent, new CaptureProgress()); throw new Exception("Unavailable selected stream accepted"); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("所选音视频流已不可用", StringComparison.Ordinal)) { }
    if (handler.StreamRequests != 1 || File.Exists(path)) throw new Exception("Unavailable selected stream handling failed");
    BiliApi? changedDuringRequest = null;
    var switchingHandler = new FixtureHandler(onStream: () => changedDuringRequest!.SetCookie("SESSDATA=changed"));
    changedDuringRequest = new BiliApi(switchingHandler); changedDuringRequest.SetUserAgent("BiliDesktop-Fixture/1.0");
    var capturedSession = changedDuringRequest.SessionVersion;
    try { await MediaDownload.DownloadFreshAsync(changedDuringRequest, "BV1xx411c7mD", new VideoPage(42, "fixture"), selected, null, false, capturedSession, path, changedDuringRequest.UserAgent, new CaptureProgress()); throw new Exception("Session change during stream request accepted"); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("会话已变化", StringComparison.Ordinal)) { }
    if (switchingHandler.StreamRequests != 1 || File.Exists(path)) throw new Exception("Session switch during refresh was not stopped");
}
Console.WriteLine("Single-download session change and unavailable quality safeguards passed.");
foreach (var marker in new[] { "true", "false", "missing" })
{
    var paidApi = new BiliApi(new FixtureHandler(paidPreview: marker)); paidApi.SetUserAgent("BiliDesktop-Fixture/1.0");
    var paidVideo = await paidApi.GetVideoAsync("BV1xx411c7mD");
    if (!paidVideo.Pages[0].CheckPaidPreview) throw new Exception("Paid video marker was not propagated to its page");
    if (marker == "missing")
    {
        try { await paidApi.GetStreamsForPageAsync(paidVideo.Bvid, paidVideo.Pages[0]); throw new Exception("Unknown paid preview status accepted"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("无法确认付费视频", StringComparison.Ordinal)) { }
    }
    else
    {
        var paidStreams = await paidApi.GetStreamsForPageAsync(paidVideo.Bvid, paidVideo.Pages[0]);
        if (paidStreams.IsPreview != (marker == "true")) throw new Exception("Paid video preview status was not propagated");
        var gatewayStreams = await new BiliBackupGateway(paidApi).StreamsAsync(paidVideo.Bvid, paidVideo.Pages[0]);
        if (gatewayStreams.IsPreview != paidStreams.IsPreview) throw new Exception("Automated download gateway lost paid preview status");
        if (marker == "true")
        {
            var job = new BatchJob { Number = 1, Bvid = paidVideo.Bvid, Label = Guid.NewGuid().ToString("N"), Page = paidVideo.Pages[0] };
            await new BatchDownloadEngine(paidApi, null, null).RunJobAsync(job, Path.GetTempPath(), 0, BatchFormat.Automatic, paidApi.UserAgent);
            if (!job.Status.Contains("预览", StringComparison.Ordinal)) throw new Exception("Paid preview accepted for batch download");
        }
    }
}
Console.WriteLine("Paid UGC preview detection, missing marker and batch rejection passed with fixtures.");
Console.WriteLine("Fixture video and signed stream requests passed.");
var qr = await fake.CreateQrLoginAsync();
var qrResult = await fake.PollQrLoginAsync(qr.Key, CancellationToken.None);
if (qr.Key != "test-key" || qrResult.Code != 0 || qrResult.Cookie != "SESSDATA=test-session") throw new Exception("QR login response failed");
if (qrResult.Expires is not DateTimeOffset expires || expires < DateTimeOffset.UtcNow.AddMinutes(58) || expires > DateTimeOffset.UtcNow.AddMinutes(61)) throw new Exception("QR cookie expiration parsing failed");
Console.WriteLine("Fixture QR login response passed.");
var search = await fake.SearchVideosAsync("测试 keyword", 1, "pubdate");
if (search.PageCount != 2 || search.Items.Count != 1 || search.Items[0].Title != "测试 & video" || search.Items[0].Bvid != "BV1xx411c7mD") throw new Exception("Search result parsing failed");
try { await fake.SearchVideosAsync(" ", 1, "pubdate"); throw new Exception("Empty query accepted"); } catch (ArgumentException) { }
Console.WriteLine("WBI vector, signed search, fingerprint, and pagination passed.");
var temporary = Path.Combine(Path.GetTempPath(), "bili-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var packageFixture = Path.Combine(temporary, "package.zip");
    using (var zip = System.IO.Compression.ZipFile.Open(packageFixture, System.IO.Compression.ZipArchiveMode.Create))
    {
        foreach (var name in new[] { "fixture/bin/ffmpeg.exe", "fixture/bin/ffprobe.exe", "fixture/LICENSE" })
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("fixture");
        }
        var padding = new byte[1200000]; System.Security.Cryptography.RandomNumberGenerator.Fill(padding);
        using var paddingStream = zip.CreateEntry("fixture/padding.bin", System.IO.Compression.CompressionLevel.NoCompression).Open(); paddingStream.Write(padding);
    }
    string packageHash;
    await using (var packageStream = File.OpenRead(packageFixture)) packageHash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(packageStream));
    var installFixture = Path.Combine(temporary, "installed-tools");
    try { await MediaTools.InstallArchiveAsync(packageFixture, new string('0', 64), installFixture); throw new Exception("Bad package hash accepted"); }
    catch (InvalidDataException) { }
    if (Directory.Exists(installFixture)) throw new Exception("Bad hash left an installation");
    await Task.WhenAll(MediaTools.InstallArchiveAsync(packageFixture, packageHash, installFixture), MediaTools.InstallArchiveAsync(packageFixture, packageHash, installFixture));
    if (!MediaTools.Installed(installFixture) || MediaDownload.ResolveExecutable("ffmpeg", temporary, installFixture) != Path.Combine(installFixture, "ffmpeg.exe")) throw new Exception("Atomic tool installation or discovery failed");
    var incompletePackage = Path.Combine(temporary, "incomplete.zip");
    using (var zip = System.IO.Compression.ZipFile.Open(incompletePackage, System.IO.Compression.ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("fixture/bin/ffmpeg.exe").Open()); writer.Write("fixture"); }
    string incompleteHash;
    await using (var packageStream = File.OpenRead(incompletePackage)) incompleteHash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(packageStream));
    var incompleteTarget = Path.Combine(temporary, "incomplete-tools");
    try { await MediaTools.InstallArchiveAsync(incompletePackage, incompleteHash, incompleteTarget); throw new Exception("Incomplete package accepted"); }
    catch (InvalidDataException) { }
    if (Directory.Exists(incompleteTarget) || Directory.EnumerateDirectories(temporary, ".ffmpeg-stage-*").Any()) throw new Exception("Incomplete package left installation files");
    var packageBytes = await File.ReadAllBytesAsync(packageFixture);
    var networkTarget = Path.Combine(temporary, "downloaded-tools");
    var packageHandler = new PackageHandler(packageBytes);
    var packageProgress = new CaptureProgress();
    await MediaTools.DownloadAndInstallAsync(packageProgress, networkTarget, "https://example.test/package.zip", packageHash, packageHandler);
    if (!MediaTools.Installed(networkTarget) || packageHandler.Requests != 1 || !packageProgress.Messages.Any(x => x.StartsWith("正在下载 FFmpeg：1 / 1 MiB", StringComparison.Ordinal))) throw new Exception("Package download, progress or installation failed");
    var unnecessaryRequest = new PackageHandler(packageBytes);
    await MediaTools.DownloadAndInstallAsync(null, networkTarget, "https://example.test/package.zip", packageHash, unnecessaryRequest);
    if (unnecessaryRequest.Requests != 0) throw new Exception("Installed package was downloaded again");
    var badTarget = Path.Combine(temporary, "bad-download");
    try { await MediaTools.DownloadAndInstallAsync(null, badTarget, "https://example.test/package.zip", new string('0', 64), new PackageHandler(packageBytes)); throw new Exception("Bad downloaded package accepted"); }
    catch (InvalidDataException) { }
    if (Directory.Exists(badTarget) || Directory.EnumerateFiles(temporary, ".ffmpeg-download-*").Any()) throw new Exception("Bad download left installation files");
    try { await MediaTools.DownloadAndInstallAsync(null, badTarget, "https://example.test/package.zip", packageHash, new TimeoutHandler()); throw new Exception("Timed-out package accepted"); }
    catch (TimeoutException) { }
    using (var firstCancel = new CancellationTokenSource())
    using (var waitingCancel = new CancellationTokenSource())
    {
        var blocked = new BlockingPackageHandler();
        var firstInstall = MediaTools.DownloadAndInstallAsync(null, Path.Combine(temporary, "blocked-tools"), "https://example.test/package.zip", packageHash, blocked, firstCancel.Token);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waitingInstall = MediaTools.DownloadAndInstallAsync(null, Path.Combine(temporary, "waiting-tools"), "https://example.test/package.zip", packageHash, new PackageHandler(packageBytes), waitingCancel.Token);
        waitingCancel.Cancel();
        try { await waitingInstall.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Waiting for FFmpeg installation ignored cancellation"); } catch (OperationCanceledException) { }
        firstCancel.Cancel();
        try { await firstInstall.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("FFmpeg download ignored cancellation"); } catch (OperationCanceledException) { }
    }
    if (Directory.Exists(badTarget) || Directory.EnumerateFiles(temporary, ".ffmpeg-download-*").Any()) throw new Exception("Timeout left installation files");
    Console.WriteLine("FFmpeg package verification, atomic installation and concurrent installs passed.");
    File.Delete(packageFixture); File.Delete(incompletePackage); Directory.Delete(installFixture, true); Directory.Delete(networkTarget, true);
    if (args.Contains("--tools-package"))
    {
        var realPackage = Path.GetFullPath(Path.Combine(".tools", "ffmpeg-9.0.2-essentials_build.zip"));
        var realTarget = Path.Combine(temporary, "real-ffmpeg");
        await MediaTools.InstallArchiveAsync(realPackage, "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba", realTarget);
        var version = await MediaDownload.RunAsync(Path.Combine(realTarget, "ffmpeg.exe"), ["-version"], TimeSpan.FromSeconds(15));
        var probeVersion = await MediaDownload.RunAsync(Path.Combine(realTarget, "ffprobe.exe"), ["-version"], TimeSpan.FromSeconds(15));
        if (!version.Contains("ffmpeg version 9.0.2", StringComparison.Ordinal) || !probeVersion.Contains("ffprobe version 9.0.2", StringComparison.Ordinal)) throw new Exception("Installed FFmpeg tools failed version check");
        Console.WriteLine("Officially linked FFmpeg 9.0.2 package extracted and both binaries started successfully.");
        Directory.Delete(realTarget, true);
    }
    if (args.Contains("--tools-network"))
    {
        var realTarget = Path.Combine(temporary, "network-ffmpeg");
        await MediaTools.DownloadAndInstallAsync(new Progress<string>(Console.WriteLine), realTarget, "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip", "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba", new HttpClientHandler { AllowAutoRedirect = false });
        var version = await MediaDownload.RunAsync(Path.Combine(realTarget, "ffmpeg.exe"), ["-version"], TimeSpan.FromSeconds(15));
        var probeVersion = await MediaDownload.RunAsync(Path.Combine(realTarget, "ffprobe.exe"), ["-version"], TimeSpan.FromSeconds(15));
        if (!version.Contains("ffmpeg version 9.0.2", StringComparison.Ordinal) || !probeVersion.Contains("ffprobe version 9.0.2", StringComparison.Ordinal)) throw new Exception("Downloaded FFmpeg tools failed version check");
        Console.WriteLine("Network download, verified installation and both FFmpeg binaries passed.");
        Directory.Delete(realTarget, true);
    }
    try
    {
        await MediaDownload.DownloadAsync(["https://example.com/video"], null, null, Path.Combine(temporary, "invalid.avi"), BiliApi.DefaultUserAgent, new Progress<string>());
        throw new Exception("Invalid media output format accepted");
    }
    catch (ArgumentException ex) when (ex.Message.Contains("不支持的输出格式")) { }
    try
    {
        await MediaDownload.DownloadAsync(["https://example.com/video"], null, null, Path.Combine(temporary, "video.m4a"), BiliApi.DefaultUserAgent, new Progress<string>());
        throw new Exception("Video-only M4A format accepted");
    }
    catch (ArgumentException ex) when (ex.Message.Contains("不支持的输出格式")) { }
    if (Directory.EnumerateFileSystemEntries(temporary).Any()) throw new Exception("Invalid output created temporary files or started work");
    Console.WriteLine("Invalid media format rejected before starting work.");
    var mp4Collision = Path.Combine(temporary, "existing.mp4"); var mkvCollision = Path.Combine(temporary, "existing.mkv");
    await File.WriteAllTextAsync(mkvCollision, "fixture");
    if (!BatchPolicy.OutputAlreadyExists(BatchFormat.Automatic, mp4Collision, mkvCollision) || BatchPolicy.OutputAlreadyExists(BatchFormat.Mp4, mp4Collision, mkvCollision) || !BatchPolicy.OutputAlreadyExists(BatchFormat.Matroska, mp4Collision, mkvCollision)) throw new Exception("Batch existing-output collision policy failed");
    File.Delete(mkvCollision);
    if (OperatingSystem.IsWindows())
    {
        var queueDirectory = Path.Combine(temporary, "queued-rules"); Directory.CreateDirectory(queueDirectory);
        var queueStore = new BackupStore(Path.Combine(temporary, "queued-rules.db"));
        queueStore.Add(123, new FavoriteFolder(888, "fixture", 1), queueDirectory, 5);
        queueStore.AddKeyword("cat", DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds(), queueDirectory, 5);
        var backupGateway = new BackupFixtureGateway(); var keywordGatewayQueued = new BackupFixtureGateway();
        var slotsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlots = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var occupied = 0;
        var blockers = Enumerable.Range(0, 4).Select(_ => MediaDownload.InSlotAsync(async () => { if (Interlocked.Increment(ref occupied) == 4) slotsEntered.TrySetResult(); await releaseSlots.Task; })).ToArray();
        Task? queuedBackup = null; Task? queuedKeyword = null;
        try
        {
            await slotsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queuedBackup = new BackupEngine(queueStore, backupGateway).RunDueAsync();
            queuedKeyword = new KeywordEngine(queueStore, keywordGatewayQueued).RunDueAsync();
            for (var i = 0; i < 100 && (!queueStore.List().Single().Status.StartsWith("正在备份", StringComparison.Ordinal) || !queueStore.ListKeywords().Single().Status.StartsWith("正在下载", StringComparison.Ordinal)); i++) await Task.Delay(50);
            if (!queueStore.List().Single().Status.StartsWith("正在备份", StringComparison.Ordinal) || !queueStore.ListKeywords().Single().Status.StartsWith("正在下载", StringComparison.Ordinal)) throw new Exception("Rules did not reach the download queue");
            if (backupGateway.StreamCalls != 0 || keywordGatewayQueued.StreamCalls != 0) throw new Exception("Queued rules requested streams before acquiring a slot");
            queueStore.SetEnabled(queueStore.List().Single().Id, false);
            queueStore.SetKeywordEnabled(queueStore.ListKeywords().Single().Id, false);
            releaseSlots.TrySetResult();
            await Task.WhenAll(queuedBackup, queuedKeyword);
            if (backupGateway.StreamCalls != 0 || keywordGatewayQueued.StreamCalls != 0 || Directory.GetFiles(queueDirectory).Length != 0) throw new Exception("Paused queued rules fetched or downloaded media");
        }
        finally { releaseSlots.TrySetResult(); await Task.WhenAll(blockers); if (queuedBackup is not null && queuedKeyword is not null) await Task.WhenAll(queuedBackup, queuedKeyword); }
        Console.WriteLine("Queued backup and keyword rules defer stream lookup and honor pauses passed.");
        var backupDir = Path.Combine(temporary, "backup"); Directory.CreateDirectory(backupDir);
        var dbPath = Path.Combine(temporary, "backups.db");
        var store = new BackupStore(dbPath);
        var bulkFolders = new[] { new FavoriteFolder(7001, "one", 1), new FavoriteFolder(7002, "two", 2), new FavoriteFolder(7001, "duplicate", 1) };
        if (store.AddMany(123, bulkFolders, backupDir, 15) != 2 || store.AddMany(123, bulkFolders, backupDir, 15) != 0 || store.List().Count != 2) throw new Exception("Bulk backup rule deduplication failed");
        try { store.AddMany(123, [new FavoriteFolder(7003, "valid", 1), new FavoriteFolder(0, "invalid", 1)], backupDir, 15); throw new Exception("Invalid bulk rules accepted"); }
        catch (ArgumentException) { }
        if (store.List().Count != 2) throw new Exception("Invalid bulk rules partially added");
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER bulk_failure BEFORE INSERT ON backup_rules WHEN NEW.folder_id = 7005 BEGIN SELECT RAISE(ABORT, 'fixture'); END"; cmd.ExecuteNonQuery();
            try { store.AddMany(123, [new FavoriteFolder(7004, "first", 1), new FavoriteFolder(7005, "second", 1)], backupDir, 15); throw new Exception("Simulated database failure accepted"); }
            catch (Microsoft.Data.Sqlite.SqliteException) { }
            if (store.List().Any(r => r.FolderId == 7004)) throw new Exception("Bulk rule transaction did not roll back");
            cmd.CommandText = "DROP TRIGGER bulk_failure"; cmd.ExecuteNonQuery();
        }
        foreach (var bulkRule in store.List()) store.Remove(bulkRule.Id);
        Console.WriteLine("Bulk backup rules, duplicate handling and validation passed.");
        var legacyDir = Path.Combine(temporary, "legacy-backup"); Directory.CreateDirectory(legacyDir);
        var legacyFile = Path.Combine(legacyDir, "previous.mp4"); File.WriteAllText(legacyFile, "old");
        var replacementFile = Path.Combine(legacyDir, "replacement.mp4"); File.WriteAllText(replacementFile, "new");
        store.Add(123, new FavoriteFolder(9991, "legacy", 1), legacyDir, 5);
        var legacyRule = store.List().Single();
        store.MarkCompleted(legacyRule.Id, "BV1xx411c7mD", 11, legacyFile);
        store.MarkCompleted(legacyRule.Id, "BV1xx411c7mD", 11, replacementFile);
        File.Delete(legacyFile);
        if (!store.IsCompleted(legacyRule.Id, "BV1xx411c7mD", 11)) throw new Exception("Completion update did not replace the saved path");
        var legacyGateway = new BackupFixtureGateway();
        await new BackupEngine(store, legacyGateway).RunDueAsync();
        if (legacyGateway.Successes != 1 || !File.Exists(replacementFile)) throw new Exception("Existing legacy completion was redownloaded or moved");
        store.Remove(legacyRule.Id);
        var manualDir = Path.Combine(temporary, "manual-favorites"); Directory.CreateDirectory(manualDir);
        store.Add(123, new FavoriteFolder(9994, "manual", 1), manualDir, 5);
        var manualRule = store.List().Single();
        var manualGateway = new BackupFixtureGateway();
        var manualVideo = await manualGateway.VideoAsync("BV1xx411c7mD");
        var manualOutput = DownloadNaming.Folder(BackupEngine.FolderDirectory(manualRule), manualVideo);
        Directory.CreateDirectory(manualOutput);
        var manualName = DownloadNaming.BaseName(manualVideo, manualVideo.Pages[0], 1, new VideoStream(80, "avc", 200, "https://example.com/v", []), null) + ".mp4";
        var manualFile = Path.Combine(manualOutput, manualName); File.WriteAllText(manualFile, "fixture");
        store.RecordFavoriteFile(123, 9994, manualVideo.Bvid, manualVideo.Pages[0].Cid, manualFile);
        var corruptFile = Path.Combine(BackupEngine.FolderDirectory(manualRule), BatchPolicy.FileName(manualVideo.Bvid, 2, manualVideo.Pages[1].Label, false));
        File.WriteAllText(corruptFile, "corrupt");
        var manualEngine = new BackupEngine(store, manualGateway);
        await manualEngine.RunDueAsync();
        var repaired = store.FavoriteFile(123, 9994, manualVideo.Bvid, manualVideo.Pages[1].Cid);
        if (manualGateway.Successes != 1 || repaired is null || repaired == corruptFile || !File.Exists(repaired) || File.ReadAllText(corruptFile) != "corrupt" || !store.IsCompleted(manualRule.Id, manualVideo.Bvid, manualVideo.Pages[0].Cid)) throw new Exception("Manual favorite file reconciliation or safe incomplete-file recovery failed");
        store.RunNow(manualRule.Id); await manualEngine.RunDueAsync();
        if (manualGateway.Successes != 1) throw new Exception("Reconciled favorites were downloaded again");
        store.Remove(manualRule.Id);
        var untrackedDir = Path.Combine(temporary, "untracked-favorites"); Directory.CreateDirectory(untrackedDir);
        store.Add(123, new FavoriteFolder(9995, "untracked", 1), untrackedDir, 5);
        var untrackedRule = store.List().Single();
        var oldManual = DownloadNaming.Folder(untrackedDir, manualVideo); Directory.CreateDirectory(oldManual);
        File.WriteAllText(Path.Combine(oldManual, manualName), "fixture");
        var untrackedGateway = new BackupFixtureGateway { IncludeDuration = true }; await new BackupEngine(store, untrackedGateway).RunDueAsync();
        if (untrackedGateway.Successes != 1 || store.FavoriteFile(123, 9995, manualVideo.Bvid, manualVideo.Pages[0].Cid) is null) throw new Exception("Previous manual favorite download was not recognized");
        store.Remove(untrackedRule.Id);
        Console.WriteLine("Manual and prior favorite downloads reconcile with backups; damaged originals are preserved.");
        var concurrencyDir = Path.Combine(temporary, "backup-concurrency"); Directory.CreateDirectory(concurrencyDir);
        try
        {
            MediaDownload.Configure(new(17, 16, 2));
            store.Add(123, new FavoriteFolder(9992, "parallel", 1), concurrencyDir, 5);
            var parallel = new ConcurrentBackupFixtureGateway();
            await new BackupEngine(store, parallel).RunDueAsync();
            if (parallel.Maximum != 2 || parallel.Successes != 2) throw new Exception("Backup did not use the configured global concurrency");
            store.Remove(store.List().Single().Id);
            MediaDownload.Configure(new(3, 16, 1));
            store.Add(123, new FavoriteFolder(9993, "serial", 1), concurrencyDir, 5);
            var serial = new ConcurrentBackupFixtureGateway();
            await new BackupEngine(store, serial).RunDueAsync();
            if (serial.Maximum != 1 || serial.Successes != 2) throw new Exception("Backup exceeded the configured global concurrency");
            store.Remove(store.List().Single().Id);
            var backupAriaOptions = MediaDownload.AriaSession.MakeOptions(concurrencyDir, "test", BiliApi.DefaultUserAgent, MediaDownload.CurrentOptions.Connections);
            if ((string)backupAriaOptions["split"] != "3") throw new Exception("Configured connection count was not applied to aria2");
        }
        finally { MediaDownload.Configure(new(4, 32)); }
        Console.WriteLine("Legacy backup paths, updated completion records and configured backup concurrency passed.");
        store.Add(123, new FavoriteFolder(456, "fixture", 1), backupDir, 5);
        var rule = store.List().Single();
        var gateway = new BackupFixtureGateway { FailSecondOnce = true };
        var engine = new BackupEngine(store, gateway);
        await engine.RunDueAsync();
        if (gateway.Successes != 1 || !store.List().Single().Status.Contains("失败 1")) throw new Exception("Backup partial failure not recorded");
        store.RunNow(rule.Id); await engine.RunDueAsync();
        if (gateway.Successes != 2 || !store.List().Single().Status.Contains("失败 0")) throw new Exception("Backup retry or deduplication failed");
        var reopened = new BackupStore(dbPath);
        gateway.FirstLabel = "P1 · renamed";
        reopened.RunNow(rule.Id); await new BackupEngine(reopened, gateway).RunDueAsync();
        if (gateway.Successes != 2) throw new Exception("Backup records did not survive reopen or title change");
        gateway.FirstLabel = "P1 · one";
        reopened.Add(123, new FavoriteFolder(458, "collision", 1), backupDir, 5);
        var initialOutput = BackupEngine.FolderDirectory(rule);
        var collisionOutput = BackupEngine.FolderDirectory(reopened.List().Single(r => r.FolderId == 458));
        if (initialOutput == collisionOutput || Directory.GetFiles(initialOutput).Length != 2) throw new Exception("Favorite folders do not have separate directories");
        Directory.CreateDirectory(collisionOutput);
        foreach (var file in Directory.GetFiles(initialOutput)) File.Copy(file, Path.Combine(collisionOutput, Path.GetFileName(file)));
        await new BackupEngine(reopened, gateway).RunDueAsync();
        var collisionRule = reopened.List().Single(r => r.FolderId == 458);
        if (gateway.Successes != 2 || !collisionRule.Status.Contains("已有完整 2")) throw new Exception("Untracked existing files were not safely reported");
        reopened.Remove(collisionRule.Id);
        var missing = Path.Combine(initialOutput, BatchPolicy.FileName("BV1xx411c7mD", 1, "P1 · one", false));
        File.Delete(missing); reopened.RunNow(rule.Id); await new BackupEngine(reopened, gateway).RunDueAsync();
        if (gateway.Successes != 3 || !File.Exists(missing)) throw new Exception("Missing backup was not restored");
        gateway.AccountId = 999; reopened.RunNow(rule.Id); await new BackupEngine(reopened, gateway).RunDueAsync();
        if (gateway.Successes != 3 || !reopened.List().Single().Status.Contains("账号不一致")) throw new Exception("Backup account mismatch not blocked");
        gateway.AccountId = 123;
        reopened.SetEnabled(rule.Id, false); reopened.RunNow(rule.Id); await new BackupEngine(reopened, gateway).RunDueAsync();
        if (gateway.Successes != 3 || reopened.List().Single().Enabled) throw new Exception("Paused backup ran");
        var guardedDir = Path.Combine(temporary, "guarded"); Directory.CreateDirectory(guardedDir);
        reopened.Add(123, new FavoriteFolder(457, "guarded", 1), guardedDir, 5);
        gateway.ChangeSessionDuringStreams = true;
        await new BackupEngine(reopened, gateway).RunDueAsync();
        if (gateway.Successes != 3 || Directory.GetFiles(guardedDir).Length != 0 || !reopened.List().Single(r => r.FolderId == 457).Status.Contains("会话已变化")) throw new Exception("Backup session change not blocked");
        reopened.Remove(reopened.List().Single(r => r.FolderId == 457).Id);
        reopened.Remove(rule.Id);
        if (reopened.List().Count != 0 || !File.Exists(missing)) throw new Exception("Rule deletion removed output or failed");
        Console.WriteLine("Backup persistence, retry, deduplication, missing file recovery, account/session guards, pause and deletion passed.");

        var keywordDir = Path.Combine(temporary, "keywords"); Directory.CreateDirectory(keywordDir);
        reopened.AddKeyword("cat", DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds(), keywordDir, 5);
        var keywordRule = reopened.ListKeywords().Single();
        var keywordGateway = new BackupFixtureGateway { FailSecondOnce = true };
        var keywordEngine = new KeywordEngine(reopened, keywordGateway);
        await keywordEngine.RunDueAsync();
        if (keywordGateway.Successes != 1 || !reopened.ListKeywords().Single().Status.Contains("失败 1") || keywordGateway.SearchCalls != 2) throw new Exception("Keyword matching, pagination or failure tracking failed");
        reopened.RunKeywordNow(keywordRule.Id); await keywordEngine.RunDueAsync();
        if (keywordGateway.Successes != 2 || !reopened.ListKeywords().Single().Status.Contains("匹配 1")) throw new Exception("Keyword retry or filtering failed");
        var restart = new BackupStore(dbPath); restart.RunKeywordNow(keywordRule.Id); await new KeywordEngine(restart, keywordGateway).RunDueAsync();
        if (keywordGateway.Successes != 2) throw new Exception("Keyword deduplication did not survive restart");
        restart.SetKeywordEnabled(keywordRule.Id, false); restart.RunKeywordNow(keywordRule.Id); await new KeywordEngine(restart, keywordGateway).RunDueAsync();
        if (keywordGateway.Successes != 2) throw new Exception("Paused keyword rule ran");
        restart.RemoveKeyword(keywordRule.Id);
        if (restart.ListKeywords().Count != 0 || Directory.GetFiles(keywordDir, "*", SearchOption.AllDirectories).Length != 2) throw new Exception("Keyword rule deletion removed output or failed");
        Console.WriteLine("Keyword matching, pagination, retry, persistent deduplication and pause passed.");

        var combinedDir = Path.Combine(temporary, "combined-keywords"); Directory.CreateDirectory(combinedDir);
        var combinedStore = new BackupStore(Path.Combine(temporary, "combined.db"));
        var since = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
        combinedStore.AddKeyword(["cat", "video"], since, combinedDir, 30, KeywordMatchMode.All, 1500);
        combinedStore.AddKeyword(["cat", "absent"], since, combinedDir, 43200, KeywordMatchMode.Any, 250);
        var rules = combinedStore.ListKeywords();
        if (rules[0].EffectiveIntervalSeconds != 30 || rules[1].EffectiveIntervalSeconds != 43200 || rules[0].MatchMode != KeywordMatchMode.All || rules[0].QueryDelayMs != 1500 || !rules[0].Keywords.SequenceEqual(["cat", "video"])) throw new Exception("Combined rule persistence failed");
        var sample = new SearchVideo("BV1xx411c7mD", "CAT", "owner", "1:00", since + 1, "VIDEO", "tag");
        if (!KeywordPolicy.Matches(sample, rules[0]) || !KeywordPolicy.Matches(sample, rules[1]) || KeywordPolicy.Matches(sample with { Description = "" }, rules[0])) throw new Exception("Any/all keyword matching failed");
        var waits = new List<TimeSpan>();
        var combinedGateway = new BackupFixtureGateway();
        var combinedEngine = new KeywordEngine(combinedStore, combinedGateway, (delay, _) => { waits.Add(delay); return Task.CompletedTask; });
        await combinedEngine.RunDueAsync();
        var outputs = Directory.GetFiles(combinedDir, "*.mp4", SearchOption.AllDirectories);
        if (combinedGateway.SearchCalls != 8 || combinedGateway.Successes != 4 || outputs.Length != 4 || outputs.Any(p => !Path.GetFileName(p).StartsWith("fixture", StringComparison.Ordinal) || Path.GetFileName(p).Contains("BV1xx411c7mD", StringComparison.Ordinal)) || outputs.Select(Path.GetDirectoryName).Distinct().Count() != 2) throw new Exception("Multi-keyword union, deduplication, naming or per-rule folder failed");
        if (!waits.Any(w => w.TotalMilliseconds > 1400) || !waits.Any(w => w.TotalMilliseconds is > 150 and < 300)) throw new Exception("Per-rule search delay was not applied");
        var checkedRules = combinedStore.ListKeywords();
        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (checkedRules[0].NextRun < nowSeconds + 27 || checkedRules[0].NextRun > nowSeconds + 30 || checkedRules[1].NextRun < nowSeconds + 43199) throw new Exception("Second-resolution keyword scheduling failed");
        foreach (var r in checkedRules) combinedStore.RunKeywordNow(r.Id);
        await combinedEngine.RunDueAsync();
        if (combinedGateway.Successes != 4) throw new Exception("Combined rules did not deduplicate on retry");

        var oldPath = Path.Combine(temporary, "old-keywords.db"); var oldFile = Path.Combine(temporary, "old-result.mp4"); File.WriteAllText(oldFile, "existing");
        using (var oldDb = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = oldPath, Pooling = false }.ToString()))
        {
            oldDb.Open(); using var command = oldDb.CreateCommand();
            command.CommandText = "CREATE TABLE keyword_rules(id INTEGER PRIMARY KEY, keyword TEXT NOT NULL, since_unix INTEGER NOT NULL, directory TEXT NOT NULL, interval_minutes INTEGER NOT NULL, enabled INTEGER NOT NULL DEFAULT 1, next_run INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT '等待检查', UNIQUE(keyword,since_unix,directory)); CREATE TABLE keyword_completed(rule_id INTEGER NOT NULL REFERENCES keyword_rules(id) ON DELETE CASCADE, bvid TEXT NOT NULL, cid INTEGER NOT NULL, path TEXT NOT NULL, PRIMARY KEY(rule_id,bvid,cid)); INSERT INTO keyword_rules VALUES(7,'legacy',1,$dir,15,1,0,'等待检查'); INSERT INTO keyword_completed VALUES(7,'BV1xx411c7mD',11,$path);";
            command.Parameters.AddWithValue("$dir", combinedDir); command.Parameters.AddWithValue("$path", oldFile); command.ExecuteNonQuery();
        }
        var migrated = new BackupStore(oldPath); var oldRule = migrated.ListKeywords().Single();
        if (!oldRule.Keywords.SequenceEqual(["legacy"]) || oldRule.EffectiveIntervalSeconds != 900 || oldRule.MatchMode != KeywordMatchMode.Any || !migrated.KeywordCompleted(7, "BV1xx411c7mD", 11)) throw new Exception("Legacy rule or completion migration failed");
        var blockedStore = new BackupStore(Path.Combine(temporary, "blocked-keywords.db"));
        blockedStore.AddKeyword(["cat"], since, combinedDir, 30, KeywordMatchMode.Any, 250);
        await new KeywordEngine(blockedStore, new BackupFixtureGateway { SearchError = "请求被平台拦截（HTTP 412）" }).RunDueAsync();
        var blocked = blockedStore.ListKeywords().Single();
        if (!blocked.Status.StartsWith("检查失败：", StringComparison.Ordinal) || blocked.NextRun < DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 299) throw new Exception("Platform rate-limit backoff failed");
        Console.WriteLine("Keyword migration, any/all matching, rule folders, naming, query pacing and seconds scheduling passed.");

        var flacBackupDir = Path.Combine(temporary, "flac-backup"); Directory.CreateDirectory(flacBackupDir);
        var flacGateway = new BackupFixtureGateway { UseFlac = true };
        restart.Add(123, new FavoriteFolder(459, "FLAC", 1), flacBackupDir, 5);
        await new BackupEngine(restart, flacGateway).RunDueAsync();
        var flacBackupRule = restart.List().Single(r => r.FolderId == 459);
        if (flacGateway.Successes != 2 || Directory.GetFiles(flacBackupDir, "*.mkv", SearchOption.AllDirectories).Length != 2) throw new Exception("FLAC backup did not use MKV");
        restart.Add(123, new FavoriteFolder(460, "existing MKV", 1), flacBackupDir, 5);
        var existingFlacDir = BackupEngine.FolderDirectory(restart.List().Single(r => r.FolderId == 460));
        Directory.CreateDirectory(existingFlacDir);
        foreach (var file in Directory.GetFiles(BackupEngine.FolderDirectory(flacBackupRule), "*.mkv")) File.Copy(file, Path.Combine(existingFlacDir, Path.GetFileName(file)));
        await new BackupEngine(restart, new BackupFixtureGateway()).RunDueAsync();
        if (!restart.List().Single(r => r.FolderId == 460).Status.Contains("已有完整 2")) throw new Exception("Existing MKV was not detected");
        restart.Remove(flacBackupRule.Id); restart.Remove(restart.List().Single(r => r.FolderId == 460).Id);

        var flacKeywordDir = Path.Combine(temporary, "flac-keyword"); Directory.CreateDirectory(flacKeywordDir);
        restart.AddKeyword("cat", DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds(), flacKeywordDir, 5);
        await new KeywordEngine(restart, new BackupFixtureGateway { UseFlac = true }).RunDueAsync();
        if (Directory.GetFiles(flacKeywordDir, "*.mkv", SearchOption.AllDirectories).Length != 2) throw new Exception("FLAC keyword download did not use MKV");
        restart.RemoveKeyword(restart.ListKeywords().Single().Id);
        Console.WriteLine("Automatic FLAC container selection and cross-format collision detection passed.");
    }
    if (OperatingSystem.IsWindows())
    {
        var credentialPath = Path.Combine(temporary, "credential.dat");
        const string credential = "SESSDATA=fixture-secret";
        var credentialExpiry = DateTimeOffset.FromUnixTimeSeconds(2000000000);
        CredentialStore.Save(credential, credentialPath, credentialExpiry);
        if (System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(credentialPath)).Contains(credential)) throw new Exception("Credential stored in plaintext");
        if (CredentialStore.Load(credentialPath) != credential) throw new Exception("Credential roundtrip failed");
        if (CredentialStore.LoadCredential(credentialPath)?.Expires != credentialExpiry) throw new Exception("Credential expiry roundtrip failed");
        var legacyBytes = System.Text.Encoding.UTF8.GetBytes(credential);
        try { await File.WriteAllBytesAsync(credentialPath, System.Security.Cryptography.ProtectedData.Protect(legacyBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser)); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(legacyBytes); }
        var legacyCredential = CredentialStore.LoadCredential(credentialPath);
        if (legacyCredential?.Cookie != credential || legacyCredential.Expires is not null) throw new Exception("Legacy credential migration failed");
        CredentialStore.Delete(credentialPath);
        if (File.Exists(credentialPath)) throw new Exception("Credential deletion failed");
        Console.WriteLine("DPAPI credential save, restore, and delete passed.");
    }
    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    var server = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var connection = client.GetStream();
        var request = new byte[8192];
        var received = await connection.ReadAsync(request.AsMemory());
        if (!System.Text.Encoding.ASCII.GetString(request, 0, received).Contains("User-Agent: BiliDesktop-Fixture/1.0", StringComparison.OrdinalIgnoreCase)) throw new Exception("aria2 did not send configured user agent");
        var body = System.Text.Encoding.ASCII.GetBytes("aria2 fixture");
        var header = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await connection.WriteAsync(header); await connection.WriteAsync(body);
    });
    using var aria = await MediaDownload.AriaSession.StartAsync(64);
    var globalOptions = await aria.GlobalOptionsAsync();
    if (globalOptions.GetProperty("disk-cache").GetString() != "67108864") throw new Exception("aria2 memory cache configuration failed");
    var pausedOptions = MediaDownload.AriaSession.MakeOptions(temporary, "paused.bin", "BiliDesktop-Fixture/1.0", 64);
    pausedOptions["pause"] = "true";
    var pausedId = (await aria.CallAsync("aria2.addUri", [new[] { $"http://127.0.0.1:{port}/paused" }, pausedOptions])).GetString()!;
    var activeOptions = await aria.CallAsync("aria2.getOption", [pausedId]);
    if (activeOptions.GetProperty("split").GetString() != "64" || activeOptions.GetProperty("max-connection-per-server").GetString() != "16") throw new Exception("aria2 did not accept desired connection settings");
    await aria.CallAsync("aria2.remove", [pausedId]);
    await aria.DownloadAsync($"http://127.0.0.1:{port}/fixture", temporary, "fixture.bin", new Progress<string>(), "test", "BiliDesktop-Fixture/1.0");
    await server;
    if (await File.ReadAllTextAsync(Path.Combine(temporary, "fixture.bin")) != "aria2 fixture") throw new Exception("aria2 output mismatch");
    Console.WriteLine("aria2 RPC and local download passed.");
    using (var slowServer = new SlowMediaServer())
    using (var controlledAria = await MediaDownload.AriaSession.StartAsync())
    {
        var firstProgress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var control = new TransferControl { Progress = new InlineProgress<TransferProgress>(p => { if (p.Total > 0 && p.Connections > 0) firstProgress.TrySetResult(); }) };
        var transfer = controlledAria.DownloadAsync([slowServer.Url], temporary, "controlled.bin", new CaptureProgress(), "video", "fixture", 1, control);
        await firstProgress.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (!control.Pause()) throw new Exception("Active transfer could not be paused");
        var paused = false;
        for (var n = 0; n < 40; n++)
        {
            var waiting = await controlledAria.CallAsync("aria2.tellWaiting", [0, 10]);
            if (waiting.EnumerateArray().Any(x => x.GetProperty("status").GetString() == "paused")) { paused = true; break; }
            await Task.Delay(100);
        }
        if (!paused || transfer.IsCompleted) throw new Exception("aria2 did not enter paused state");
        control.Resume(); await transfer.WaitAsync(TimeSpan.FromSeconds(25));
        if (new FileInfo(Path.Combine(temporary, "controlled.bin")).Length != SlowMediaServer.Length) throw new Exception("Resumed aria2 transfer has incorrect length");
    }
    Console.WriteLine("Real aria2 transfer pause, RPC state, resume and output length passed.");
    using (var slowServer = new SlowMediaServer())
    using (var controlledAria = await MediaDownload.AriaSession.StartAsync())
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var control = new TransferControl { Progress = new InlineProgress<TransferProgress>(p => { if (p.Total > 0) first.TrySetResult(); }) };
        var transfer = controlledAria.DownloadAsync([slowServer.Url], temporary, "cancelled.bin", new CaptureProgress(), "video", "fixture", 1, control);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(15)); control.Cancel();
        try { await transfer.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("aria2 transfer did not cancel"); } catch (OperationCanceledException) { }
    }
    Console.WriteLine("Real aria2 cancellation passed.");
    await MediaDownload.ValidateMediaToolsAsync();
    using (var processCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
    {
        try { await MediaDownload.RunAsync("ffmpeg", ["-nostdin", "-v", "error", "-re", "-f", "lavfi", "-i", "color=size=64x64:rate=10", "-t", "30", "-f", "null", "-"], cancellation: processCancellation.Token); throw new Exception("FFmpeg processing ignored cancellation"); }
        catch (OperationCanceledException) { }
    }
    Console.WriteLine("FFmpeg processing cancellation passed.");
    var videoPath = Path.Combine(temporary, "video.mp4");
    var audioPath = Path.Combine(temporary, "audio.m4a");
    var outputPath = Path.Combine(temporary, "combined.mp4");
    await MediaDownload.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "color=size=192x108:rate=10", "-t", "1", "-c:v", "mpeg4", "-an", videoPath]);
    await MediaDownload.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440", "-t", "1", "-c:a", "aac", "-vn", audioPath]);
    await MediaDownload.ProcessMediaAsync(videoPath, audioPath, outputPath);
    var truncatedPath = Path.Combine(temporary, "truncated.mp4");
    var fullBytes = File.ReadAllBytes(outputPath); File.WriteAllBytes(truncatedPath, fullBytes[..(fullBytes.Length / 2)]);
    if (!await MediaDownload.ValidateCompleteVideoAsync(outputPath, 1, CancellationToken.None) || await MediaDownload.ValidateCompleteVideoAsync(outputPath, 30, CancellationToken.None) || await MediaDownload.ValidateCompleteVideoAsync(videoPath, null, CancellationToken.None) || await MediaDownload.ValidateCompleteVideoAsync(truncatedPath, null, CancellationToken.None)) throw new Exception("Existing media integrity verification failed");
    Console.WriteLine("Existing media verification detects missing streams, truncation and insufficient duration.");
    await MediaDownload.ProcessMediaAsync(videoPath, null, Path.Combine(temporary, "video-only.mp4"));
    await MediaDownload.ProcessMediaAsync(null, audioPath, Path.Combine(temporary, "audio-only.m4a"));
    var flacPath = Path.Combine(temporary, "audio.flac");
    var mkvPath = Path.Combine(temporary, BatchPolicy.FileName("BV1xx411c7mD", 1, "audio", true, BatchPolicy.ChooseMatroska(BatchFormat.Automatic, flacChoice)));
    await MediaDownload.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440", "-t", "1", "-c:a", "flac", "-vn", flacPath]);
    await MediaDownload.ProcessMediaAsync(null, flacPath, mkvPath);
    var mkvCodec = await MediaDownload.RunAsync("ffprobe", ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", mkvPath]);
    if (mkvCodec.Trim() != "flac") throw new Exception("FLAC in Matroska output failed");
    var metadataVideo = new VideoInfo("BV1xx411c7mD", "视频标题 / Song", "UP 主 Artist", "//i0.hdslb.com/cover.jpg", [new(11, "P1 · 第一部分"), new(22, "P2 · 第二部分")], 1, null);
    var metadataOptions = new AudioMetadataOptions(true, true, true, true);
    var tags = metadataOptions.ForVideo(metadataVideo, metadataVideo.Pages[1]) ?? throw new Exception("Audio metadata options were lost");
    var trackOnly = new AudioMetadataOptions().ForVideo(metadataVideo, metadataVideo.Pages[0]);
    if (tags.CoverUrl != "https://i0.hdslb.com/cover.jpg" || tags.Artist != metadataVideo.Owner || tags.Title != "第二部分" || tags.Album != metadataVideo.Title || tags.TrackNumber != 2 || trackOnly?.TrackNumber != 1 || trackOnly.Title is not null || trackOnly.Album is not null || trackOnly.Artist is not null || trackOnly.CoverUrl is not null || DownloadNaming.AudioBaseName(metadataVideo, metadataVideo.Pages[1]) != "第二部分") throw new Exception("Audio metadata mapping, filenames or disabled options failed");
    var standalone = metadataVideo with { Pages = [metadataVideo.Pages[0]] };
    if (new AudioMetadataOptions(Album: true).ForVideo(standalone, standalone.Pages[0])?.Album != standalone.Title || DownloadNaming.AudioBaseName(standalone, standalone.Pages[0]) != DownloadNaming.SafeComponent(standalone.Title)) throw new Exception("Standalone audio naming or album failed");
    var collectionSample = new VideoInfo("BV1Kwpd6fEq7", "视频原始标题", "UP 主", "", [new(333, "P1 · 视频原始标题")], 1, 2, "纯蓝幻乐济南场", 7, "老街北");
    var collectionTags = new AudioMetadataOptions(Title: true, Album: true).ForVideo(collectionSample, collectionSample.Pages[0]);
    if (DownloadNaming.AudioBaseName(collectionSample, collectionSample.Pages[0]) != "老街北" || collectionTags?.Title != "老街北" || collectionTags.Album != "纯蓝幻乐济南场" || collectionTags.TrackNumber != 7) throw new Exception("Collection song name, album or position failed");
    try { metadataOptions.ForVideo(metadataVideo with { CoverUrl = "" }, metadataVideo.Pages[0]); throw new Exception("Missing requested cover was accepted"); } catch (InvalidOperationException) { }
    try { new AudioMetadataOptions(Artist: true).ForVideo(metadataVideo with { OwnerId = null, Owner = "番剧" }, metadataVideo.Pages[0]); throw new Exception("Unknown creator was written as artist"); } catch (InvalidOperationException) { }
    var imagePath = Path.Combine(temporary, "fixture-cover.jpg");
    await MediaDownload.RunAsync("ffmpeg", ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=red:size=64x64", "-frames:v", "1", "-update", "1", imagePath]);
    foreach (var (sourceAudio, extension) in new[] { (audioPath, ".m4a"), (flacPath, ".mkv") })
    {
        var tagged = Path.Combine(temporary, "tagged" + extension);
        await MediaDownload.ProcessMediaAsync(null, sourceAudio, tagged, audioMetadata: tags, coverFile: imagePath);
        var json = await MediaDownload.RunAsync("ffprobe", ["-v", "error", "-show_entries", "format_tags:stream=codec_type,codec_name:stream_disposition=attached_pic:stream_tags=filename,mimetype", "-of", "json", tagged]);
        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        var formatTags = parsed.RootElement.GetProperty("format").GetProperty("tags").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString(), StringComparer.OrdinalIgnoreCase);
        if (formatTags.GetValueOrDefault("artist") != tags.Artist || formatTags.GetValueOrDefault("title") != tags.Title || formatTags.GetValueOrDefault("album") != tags.Album || formatTags.GetValueOrDefault("track")?.Split('/')[0] != "2") throw new Exception("Audio metadata or track number was not written correctly to " + extension);
        var probeStreams = parsed.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        if (!probeStreams.Any(x => x.GetProperty("codec_type").GetString() == "audio" && x.GetProperty("codec_name").GetString() == (extension == ".m4a" ? "aac" : "flac"))) throw new Exception("Audio was unexpectedly transcoded");
        var hasCover = probeStreams.Any(x => x.GetProperty("codec_type").GetString() == "video" && x.GetProperty("codec_name").GetString() == "mjpeg" && x.TryGetProperty("disposition", out var disposition) && disposition.GetProperty("attached_pic").GetInt32() == 1 && (extension == ".m4a" || x.TryGetProperty("tags", out var attachmentTags) && attachmentTags.GetProperty("mimetype").GetString() == "image/jpeg"));
        if (!hasCover) throw new Exception("Embedded cover missing from " + extension);
    }
    if (await MediaDownload.ValidateCompleteVideoAsync(Path.Combine(temporary, "tagged.mkv"), 1, CancellationToken.None)) throw new Exception("Audio with cover art was mistaken for complete video");
    var titleOnly = Path.Combine(temporary, "title-only.m4a");
    await MediaDownload.ProcessMediaAsync(null, audioPath, titleOnly, audioMetadata: new AudioMetadataOptions(Title: true).ForVideo(metadataVideo, metadataVideo.Pages[1]));
    var titleOnlyProbe = await MediaDownload.RunAsync("ffprobe", ["-v", "error", "-show_entries", "format_tags:stream=codec_type", "-of", "json", titleOnly]);
    using (var parsed = System.Text.Json.JsonDocument.Parse(titleOnlyProbe))
    {
        var onlyTags = parsed.RootElement.GetProperty("format").GetProperty("tags").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString(), StringComparer.OrdinalIgnoreCase);
        if (onlyTags.GetValueOrDefault("title") != "第二部分" || onlyTags.GetValueOrDefault("track")?.Split('/')[0] != "2" || onlyTags.ContainsKey("artist") || onlyTags.ContainsKey("album") || parsed.RootElement.GetProperty("streams").EnumerateArray().Any(x => x.GetProperty("codec_type").GetString() != "audio")) throw new Exception("Independent metadata choices were not honored");
    }
    var collectionOutput = Path.Combine(temporary, "collection-track.m4a");
    await MediaDownload.ProcessMediaAsync(null, audioPath, collectionOutput, audioMetadata: collectionTags);
    var collectionProbe = await MediaDownload.RunAsync("ffprobe", ["-v", "error", "-show_entries", "format_tags", "-of", "json", collectionOutput]);
    using (var parsed = System.Text.Json.JsonDocument.Parse(collectionProbe))
    {
        var written = parsed.RootElement.GetProperty("format").GetProperty("tags").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString(), StringComparer.OrdinalIgnoreCase);
        if (written.GetValueOrDefault("title") != "老街北" || written.GetValueOrDefault("album") != "纯蓝幻乐济南场" || written.GetValueOrDefault("track")?.Split('/')[0] != "7") throw new Exception("Collection title, album or track was not stored in the audio file");
    }
    Console.WriteLine("Audio metadata options, multipart mapping and playable M4A/MKV cover embedding passed ffprobe validation.");
    var combinedFlacPath = Path.Combine(temporary, BatchPolicy.FileName("BV1xx411c7mD", 1, "combined", false, BatchPolicy.ChooseMatroska(BatchFormat.Automatic, flacChoice)));
    await MediaDownload.ProcessMediaAsync(videoPath, flacPath, combinedFlacPath);
    await MediaDownload.ValidateEncoderAsync(VideoProcessing.Software);
    var transcoded = Path.Combine(temporary, "transcoded.mp4");
    await MediaDownload.ProcessMediaAsync(videoPath, audioPath, transcoded, VideoProcessing.Software);
    var videoCodec = await MediaDownload.RunAsync("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", transcoded]);
    if (videoCodec.Trim() != "h264") throw new Exception("Software transcoding failed");
    var streamTypes = await MediaDownload.RunAsync("ffprobe", ["-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", outputPath]);
    if (!streamTypes.Contains("video") || !streamTypes.Contains("audio")) throw new Exception("Mux verification failed");
    Console.WriteLine("FFmpeg combined, video-only, audio-only and software transcoding passed.");
    if (args.Contains("--hardware"))
    {
        foreach (var encoder in new[] { VideoProcessing.Nvenc, VideoProcessing.Qsv, VideoProcessing.Amf })
        {
            try { await MediaDownload.ValidateEncoderAsync(encoder); }
            catch (Exception) { Console.WriteLine($"{encoder}: unavailable on this machine"); continue; }
            var hardwareOutput = Path.Combine(temporary, $"{encoder}.mp4");
            await MediaDownload.ProcessMediaAsync(videoPath, audioPath, hardwareOutput, encoder);
            var hardwareCodec = await MediaDownload.RunAsync("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", hardwareOutput]);
            if (hardwareCodec.Trim() != "h264") throw new Exception($"{encoder} output check failed");
            Console.WriteLine($"{encoder}: hardware transcode and output check passed");
        }
    }
}
finally { Directory.Delete(temporary, true); }
if (args.Contains("--hardware")) return;
if (args.Contains("--live-festival"))
{
    var liveApi = new BiliApi();
    var festival = await liveApi.GetVideoAsync("https://www.bilibili.com/festival/2026BH?bvid=BV1PszTBoExj");
    var festivalStreams = await liveApi.GetStreamsAsync(festival.Bvid, festival.Pages[0].Cid);
    if (festivalStreams.Video.Count == 0) throw new Exception("Festival video has no DASH video streams.");
    Console.WriteLine($"Festival link parsed: {festival.Bvid}; {festivalStreams.Video.Count} video streams.");
    return;
}
if (args.Contains("--live-course") || args.Contains("--account-course") || args.Contains("--live-course-download"))
{
    var courseApi = new BiliApi();
    if (args.Contains("--account-course"))
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        courseApi.SetCookie(CredentialStore.Load() ?? throw new InvalidOperationException("No saved account session."));
    }
    var realCourse = await courseApi.GetCourseAsync("https://www.bilibili.com/cheese/play/ss61");
    var lesson = realCourse.Pages.FirstOrDefault(x => x.EpisodeId == 790) ?? throw new Exception("Known course lesson missing.");
    if (!lesson.Available) throw new Exception("Known course sample is unavailable.");
    var realStreams = await courseApi.GetStreamsAsync(realCourse.Bvid, lesson.Cid, lesson.EpisodeId, lesson.CourseAid);
    if (realStreams.IsPreview || realStreams.Video.Count == 0 || realStreams.Audio.Count == 0) throw new Exception("Course sample returned no complete DASH streams.");
    Console.WriteLine($"Live course lookup passed: {realCourse.Pages.Count} episodes ({realCourse.Pages.Count(x => !x.Available)} unavailable), {realStreams.Video.Count} video streams, {realStreams.Audio.Count} audio streams.");
    if (args.Contains("--live-course-download"))
    {
        var directory = Path.Combine(Path.GetTempPath(), "bili-course-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var success = false;
        try
        {
            var file = Path.Combine(directory, BatchPolicy.FileName(realCourse.Bvid, 1, "lesson", false));
            var lessonVideo = realStreams.Video.OrderBy(x => x.Bandwidth).First(); var lessonAudio = realStreams.Audio.OrderBy(x => x.Bandwidth).First();
            await MediaDownload.DownloadAsync(lessonVideo.Urls, lessonAudio.Urls, null, file, courseApi.UserAgent, new Progress<string>());
            if (new FileInfo(file).Length == 0) throw new Exception("Course output is empty.");
            Console.WriteLine($"Live course download and mux passed: {new FileInfo(file).Length} bytes."); success = true;
        }
        finally { if (success) Directory.Delete(directory, true); else Console.WriteLine("Course test artifacts retained at " + directory); }
    }
    return;
}
if (args.Contains("--live-pgc"))
{
    var liveApi = new BiliApi();
    var season = await liveApi.GetPgcAsync("ep85046");
    var episode = season.Pages.FirstOrDefault(p => p.EpisodeId == 85046) ?? throw new Exception("Requested episode not found.");
    var livePgcStreams = await liveApi.GetStreamsAsync(episode.Bvid!, episode.Cid, episode.EpisodeId);
    Console.WriteLine($"Live PGC: {season.Title}; {season.Pages.Count} episodes, {livePgcStreams.Video.Count} video streams, {livePgcStreams.Audio.Count} audio streams, preview={livePgcStreams.IsPreview}.");
    if (livePgcStreams.Video.Count == 0) throw new Exception("No PGC DASH stream returned.");
    if (args.Contains("--download"))
    {
        var directory = Path.Combine(Path.GetTempPath(), "bili-pgc-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "pgc.mp4");
            await MediaDownload.DownloadAsync(livePgcStreams.Video.OrderBy(x => x.Bandwidth).First().Urls, livePgcStreams.Audio.OrderBy(x => x.Bandwidth).First().Urls, null, output, liveApi.UserAgent, new Progress<string>());
            Console.WriteLine($"Live PGC media downloaded and checked: {new FileInfo(output).Length} bytes; account access may limit playback duration.");
        }
        finally { Directory.Delete(directory, true); }
    }
    return;
}
if (args.Contains("--live-favorite"))
{
    var liveApi = new BiliApi();
    var favoriteContents = await liveApi.GetFavoriteContentsAsync(1052622027);
    Console.WriteLine($"Live public favorite: {favoriteContents.Videos.Count} videos, {favoriteContents.Invalid} invalid, {favoriteContents.Unsupported} unsupported.");
    return;
}
if (args.Contains("--live-uploader"))
{
    var liveApi = new BiliApi();
    var sample = await liveApi.GetVideoAsync("BV1LpD3YsETa");
    if (sample.OwnerId is not long mid) throw new Exception("Sample has no uploader UID.");
    var items = await liveApi.GetUploaderVideosAsync(mid);
    if (!items.Any(x => x.Bvid == sample.Bvid)) throw new Exception("Known sample not found in uploader videos.");
    Console.WriteLine($"Live uploader query returned {items.Count} videos including the known sample.");
    return;
}
if (args.Contains("--live-collection"))
{
    var liveApi = new BiliApi();
    var collectionItems = await liveApi.GetCollectionAsync(1557073149, 2974525);
    if (collectionItems.Count == 0) throw new Exception("No collection items returned.");
    var first = await liveApi.GetVideoAsync(collectionItems[0].Bvid);
    if (first.Pages.Count == 0) throw new Exception("Collection item has no pages.");
    if (first.CollectionId != 2974525 || first.OwnerId != 1557073149) throw new Exception("Video did not expose the expected collection metadata.");
    Console.WriteLine($"Live collection returned {collectionItems.Count} videos; first video identifies its collection and has {first.Pages.Count} pages.");
    return;
}
if (args.Contains("--live-parts"))
{
    var liveApi = new BiliApi();
    var liveVideo = await liveApi.GetVideoAsync("BV1LpD3YsETa");
    if (liveVideo.Pages.Count < 2) throw new Exception("Multi-part sample no longer has two parts.");
    var directory = Path.Combine(Path.GetTempPath(), "bili-live-parts-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        await BatchScheduler.RunAsync(liveVideo.Pages.Take(2).Select((p, i) => (Page: p, Number: i + 1)), 2, async item =>
        {
            var streams = await liveApi.GetStreamsAsync(liveVideo.Bvid, item.Page.Cid);
            var v = streams.Video.OrderBy(x => x.Bandwidth).First();
            var a = streams.Audio.OrderBy(x => x.Bandwidth).First();
            var file = Path.Combine(directory, BatchPolicy.FileName(liveVideo.Bvid, item.Number, item.Page.Label, false));
            await MediaDownload.DownloadAsync(v.Urls, a.Urls, null, file, liveApi.UserAgent, new Progress<string>());
            Console.WriteLine($"Part {item.Number} downloaded and checked: {new FileInfo(file).Length} bytes.");
        }, _ => throw new Exception("Unexpected cancellation"), CancellationToken.None);
    }
    finally { Directory.Delete(directory, true); }
    return;
}
if (args.Contains("--live-search"))
{
    var liveSearch = await new BiliApi().SearchVideosAsync("音乐", 1, "totalrank");
    if (liveSearch.Items.Count == 0 || liveSearch.Items.Any(x => x.PublishUnix <= 0)) throw new Exception("Live search returned no results or missing publish timestamps.");
    Console.WriteLine($"Live search: {liveSearch.Items.Count} results, {liveSearch.PageCount} pages.");
    return;
}
if (args.Contains("--live-cover"))
{
    var coverApi = new BiliApi();
    var coverVideo = await coverApi.GetVideoAsync("BV17x411w7KC");
    var uri = new Uri(coverVideo.CoverUrl);
    if (uri.Scheme == "http") uri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
    var directory = Path.Combine(Path.GetTempPath(), "bili-cover-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var file = Path.Combine(directory, "cover.jpg");
    try
    {
        await MediaDownload.DownloadAsync(null, null, uri.AbsoluteUri, file, coverApi.UserAgent, new Progress<string>());
        var format = await MediaDownload.RunAsync("ffprobe", ["-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", file]);
        if (!format.Contains("video", StringComparison.Ordinal) || new FileInfo(file).Length == 0) throw new Exception("Downloaded cover is not a decodable image.");
        Console.WriteLine($"Live cover download and ffprobe validation passed: {new FileInfo(file).Length} bytes.");
        File.Delete(file); Directory.Delete(directory);
    }
    catch { Console.WriteLine("Cover test files retained at " + directory); throw; }
    return;
}
if (args.Contains("--live-download") || args.Contains("--live-fresh"))
{
    var liveApi = new BiliApi();
    var liveVideo = await liveApi.GetVideoAsync("BV1xx411c7mD");
    var liveStreams = await liveApi.GetStreamsAsync(liveVideo.Bvid, liveVideo.Pages[0].Cid);
    var videoStream = liveStreams.Video.OrderBy(v => v.Bandwidth).First();
    var audioStream = liveStreams.Audio.OrderBy(a => a.Bandwidth).First();
    var directory = Path.Combine(Path.GetTempPath(), "bili-live-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var file = Path.Combine(directory, "output.mp4");
        if (args.Contains("--live-fresh"))
            await MediaDownload.DownloadFreshAsync(liveApi, liveVideo.Bvid, liveVideo.Pages[0], videoStream, audioStream, false, liveApi.SessionVersion, file, BiliApi.DefaultUserAgent, new Progress<string>(Console.WriteLine));
        else
            await MediaDownload.DownloadAsync(videoStream.Urls, audioStream.Urls, null, file, BiliApi.DefaultUserAgent, new Progress<string>(Console.WriteLine));
        Console.WriteLine($"Live download and mux passed: {new FileInfo(file).Length} bytes.");
    }
    finally { Directory.Delete(directory, true); }
    return;
}
if (args.Contains("--live-av"))
{
    var liveApi = new BiliApi();
    var shortLink = await liveApi.ResolveVideoLinkAsync("https://b23.tv/BV17x411w7KC");
    if (BiliApi.ExtractBvid(shortLink) != "BV17x411w7KC") throw new Exception("Live short link lookup failed");
    Console.WriteLine("Live b23.tv redirect passed.");
    var avVideo = await liveApi.GetVideoAsync("av170001");
    if (avVideo.Bvid != "BV17x411w7KC" || avVideo.Pages.Count == 0) throw new Exception("Live AV lookup failed");
    Console.WriteLine($"Live AV lookup passed: {avVideo.Bvid}, {avVideo.Pages.Count} part(s).");
}
if (args.Contains("--account-hires") || args.Contains("--account-named"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    var storedCookie = CredentialStore.Load() ?? throw new InvalidOperationException("No saved account session.");
    var hiresApi = new BiliApi(); hiresApi.SetCookie(storedCookie);
    var hiresVideo = await hiresApi.GetVideoAsync("BV1e1421f7rA");
    var hiresStreams = await hiresApi.GetStreamsAsync(hiresVideo.Bvid, hiresVideo.Pages[0].Cid);
    var lossless = hiresStreams.Audio.FirstOrDefault(BatchPolicy.NeedsMatroska);
    if (lossless is null) { Console.WriteLine("The public Hi-Res sample did not offer a lossless audio stream for this account."); return; }
    var videoStream = hiresStreams.Video.OrderBy(x => x.Bandwidth).FirstOrDefault() ?? throw new InvalidOperationException("No video stream available for the sample.");
    var directory = Path.Combine(Path.GetTempPath(), "bili-hires-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    var success = false;
    try
    {
        var page = hiresVideo.Pages[0];
        var useNamedOutput = args.Contains("--account-named");
        var target = useNamedOutput ? DownloadNaming.Folder(directory, hiresVideo) : directory;
        Directory.CreateDirectory(target);
        var filename = useNamedOutput ? DownloadNaming.BaseName(hiresVideo, page, 1, videoStream, lossless) + ".mkv" : BatchPolicy.FileName(hiresVideo.Bvid, 1, page.Label, false, BatchPolicy.ChooseMatroska(BatchFormat.Automatic, lossless));
        var output = Path.Combine(target, filename);
        var job = new BatchJob { Number = 1, Bvid = hiresVideo.Bvid, Label = page.Label, Page = page };
        var engine = new BatchDownloadEngine(hiresApi, videoStream, lossless);
        var snapshots = 0;
        using var namedControl = new TransferControl { Progress = new InlineProgress<TransferProgress>(p => { if (p.Total > 0 && p.Connections > 0) Interlocked.Increment(ref snapshots); }) };
        await BatchScheduler.RunAsync([job], 1, item => engine.RunJobAsync(item, target, 0, BatchFormat.Automatic, hiresApi.UserAgent, control: namedControl, namingVideo: useNamedOutput ? hiresVideo : null, useNaming: useNamedOutput), _ => throw new Exception("Unexpected cancellation"), CancellationToken.None);
        if (job.Status != "完成" || !File.Exists(output)) throw new Exception("Real batch task failed: " + job.Status);
        if (snapshots == 0) throw new Exception("Real batch task did not report transfer progress");
        var codec = await MediaDownload.RunAsync("ffprobe", ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", output]);
        if (codec.Trim() != "flac" || !output.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)) throw new Exception("Lossless audio or automatic container verification failed.");
        var originalLength = new FileInfo(output).Length; var originalWrite = File.GetLastWriteTimeUtc(output);
        job.Finished = false; job.Status = "排队中";
        await BatchScheduler.RunAsync([job], 1, item => engine.RunJobAsync(item, target, 0, BatchFormat.Automatic, hiresApi.UserAgent, control: namedControl, namingVideo: useNamedOutput ? hiresVideo : null, useNaming: useNamedOutput), _ => throw new Exception("Unexpected cancellation"), CancellationToken.None);
        if (job.Status != "文件已存在，跳过" || new FileInfo(output).Length != originalLength || File.GetLastWriteTimeUtc(output) != originalWrite) throw new Exception("Real batch repeat was not safely skipped.");
        Console.WriteLine($"Real Hi-Res batch download, automatic MKV mux and repeat skip passed: {originalLength} bytes."); success = true;
    }
    finally { if (success) Directory.Delete(directory, true); else Console.WriteLine("Hi-Res test artifacts retained at " + directory); }
    return;
}
if (args.Contains("--account-search-preview"))
{
    var storedCookie = CredentialStore.Load() ?? throw new InvalidOperationException("No saved account session.");
    var searchApi = new BiliApi(); searchApi.SetCookie(storedCookie);
    var result = await searchApi.SearchVideosAsync("初音未来", 1, "totalrank");
    var sample = result.Items.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.CoverUrl) && x.Views.HasValue && x.AuthorId is > 0) ?? throw new InvalidOperationException("No search result with cover, view count and creator UID.");
    var preview = await searchApi.GetCoverPreviewAsync(sample.CoverUrl);
    var details = await searchApi.GetSearchVideoDetailsAsync(sample.Bvid);
    if (preview.Length == 0 || string.IsNullOrWhiteSpace(details.Title) || !details.Views.HasValue || details.AuthorId is not > 0 || details.AuthorId != sample.AuthorId) throw new InvalidOperationException("Search preview, creator UID or details incomplete.");
    Console.WriteLine($"Authenticated search, cover preview, view count and detail API passed ({preview.Length} cover bytes).");
    return;
}
if (args.Contains("--live-update-check"))
{
    var release = await new UpdateChecker().CheckAsync("Europe/Berlin");
    Console.WriteLine(release is null ? "Live GitHub release check succeeded: no newer recognized release." : $"Live GitHub release check succeeded: {release.Version}.");
    return;
}
if (args.Contains("--sample-collection-audio"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    var storedCookie = CredentialStore.Load() ?? throw new InvalidOperationException("No saved account session.");
    var sampleApi = new BiliApi(); sampleApi.SetCookie(storedCookie);
    var sample = await sampleApi.GetVideoAsync("BV1Kwpd6fEq7");
    Console.WriteLine($"Sample video title: {sample.Title}; collection: {sample.CollectionTitle}; item: {sample.CollectionItemTitle}; track: {sample.CollectionNumber}; parts: {sample.Pages.Count}.");
    if (sample.CollectionId is not > 0 || string.IsNullOrWhiteSpace(sample.CollectionTitle) || sample.OwnerId is not > 0) throw new InvalidOperationException("Sample collection metadata unavailable.");
    var items = await sampleApi.GetCollectionAsync(sample.OwnerId.Value, sample.CollectionId.Value);
    Console.WriteLine($"Sample collection has {items.Count} entries; API album: {items.FirstOrDefault()?.CollectionTitle}; sample position in list: {items.FirstOrDefault(x => x.Bvid == sample.Bvid)?.CollectionNumber}.");
    var song = items.FirstOrDefault(x => x.Title.Contains("老街北", StringComparison.Ordinal));
    if (song is not null)
    {
        var sampleSongVideo = await sampleApi.GetVideoAsync(song.Bvid);
        var tags = new AudioMetadataOptions(Title: true, Album: true).ForVideo(sampleSongVideo, sampleSongVideo.Pages[0], song.CollectionNumber, song.CollectionTitle, song.Title);
        Console.WriteLine($"Sample song file: {DownloadNaming.AudioBaseName(sampleSongVideo, sampleSongVideo.Pages[0], song.Title)}; title: {tags?.Title}; album: {tags?.Album}; track: {tags?.TrackNumber}; video details position: {sampleSongVideo.CollectionNumber}.");
        if (args.Contains("--sample-download-audio"))
        {
            var audioStreams = await sampleApi.GetStreamsForPageAsync(sampleSongVideo.Bvid, sampleSongVideo.Pages[0]);
            if (audioStreams.IsPreview) throw new InvalidOperationException("Sample audio is only a preview.");
            var chosenAudio = audioStreams.Audio.OrderBy(x => x.Bandwidth).First();
            var directory = Path.Combine(Path.GetTempPath(), "bili-sample-song-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var success = false;
            try
            {
                var songJob = new BatchJob { Bvid = song.Bvid, Number = 1, Label = song.Title, Page = sampleSongVideo.Pages[0], CollectionTitle = song.CollectionTitle, CollectionNumber = song.CollectionNumber, CollectionItemTitle = song.Title };
                await new BatchDownloadEngine(sampleApi, null, chosenAudio).RunJobAsync(songJob, directory, 2, BatchFormat.Automatic, sampleApi.UserAgent, namingVideo: sampleSongVideo, useNaming: true, audioOptions: new AudioMetadataOptions(Title: true, Album: true));
                if (songJob.HasError || songJob.OutputPath is null || !File.Exists(songJob.OutputPath) || Path.GetFileNameWithoutExtension(songJob.OutputPath) != "老街北") throw new InvalidOperationException("Sample song download or filename failed: " + songJob.Status);
                var json = await MediaDownload.RunAsync("ffprobe", ["-v", "error", "-show_entries", "format_tags", "-of", "json", songJob.OutputPath]);
                using var parsed = System.Text.Json.JsonDocument.Parse(json);
                var written = parsed.RootElement.GetProperty("format").GetProperty("tags").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString(), StringComparer.OrdinalIgnoreCase);
                if (written.GetValueOrDefault("title") != "老街北" || written.GetValueOrDefault("album") != sample.CollectionTitle || written.GetValueOrDefault("track")?.Split('/')[0] != song.CollectionNumber?.ToString()) throw new InvalidOperationException("Sample song title, album or track metadata failed.");
                Console.WriteLine("Provided collection song audio filename and metadata passed on a real download."); success = true;
            }
            finally { if (success) Directory.Delete(directory, true); else Console.WriteLine("Sample song test artifacts retained at " + directory); }
        }
    }
    else if (args.Contains("--sample-download-audio")) throw new InvalidOperationException("The specified song was not found in the sample collection.");
    return;
}
if (args.Contains("--account-audio-metadata"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    var storedCookie = CredentialStore.Load() ?? throw new InvalidOperationException("No saved account session.");
    var liveApi = new BiliApi(); liveApi.SetCookie(storedCookie);
    var liveVideo = await liveApi.GetVideoAsync("BV1xx411c7mD");
    var page = liveVideo.Pages.First(); var liveStreams = await liveApi.GetStreamsForPageAsync(liveVideo.Bvid, page);
    if (liveStreams.IsPreview) throw new InvalidOperationException("Fixture video returned a preview.");
    var audio = liveStreams.Audio.OrderBy(x => x.Bandwidth).First();
    var directory = Path.Combine(Path.GetTempPath(), "bili-audio-metadata-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    var success = false;
    try
    {
        var output = Path.Combine(directory, BatchPolicy.NeedsMatroska(audio) ? "audio.mkv" : "audio.m4a");
        var tags = new AudioMetadataOptions(EmbedCover: true, Artist: true, Title: true, Album: true).ForVideo(liveVideo, page);
        await MediaDownload.DownloadFreshAsync(liveApi, liveVideo.Bvid, page, null, audio, false, liveApi.SessionVersion, output, liveApi.UserAgent, new CaptureProgress(), audioMetadata: tags);
        var json = await MediaDownload.RunAsync("ffprobe", ["-v", "error", "-show_entries", "format_tags:stream=codec_type:stream_disposition=attached_pic", "-of", "json", output]);
        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        var resultTags = parsed.RootElement.GetProperty("format").GetProperty("tags").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString(), StringComparer.OrdinalIgnoreCase);
        if (resultTags.GetValueOrDefault("artist") != tags?.Artist || resultTags.GetValueOrDefault("title") != tags?.Title || resultTags.GetValueOrDefault("album") != tags?.Album || !parsed.RootElement.GetProperty("streams").EnumerateArray().Any(x => x.GetProperty("codec_type").GetString() == "video" && x.TryGetProperty("disposition", out var d) && d.GetProperty("attached_pic").GetInt32() == 1)) throw new InvalidOperationException("Live audio tags or cover missing.");
        var batchDirectory = Path.Combine(directory, "batch"); Directory.CreateDirectory(batchDirectory);
        var job = new BatchJob { Bvid = liveVideo.Bvid, Number = 1, Label = page.Label, Page = page };
        await new BatchDownloadEngine(liveApi, null, audio).RunJobAsync(job, batchDirectory, 2, BatchFormat.Automatic, liveApi.UserAgent, namingVideo: liveVideo, useNaming: true, audioOptions: new AudioMetadataOptions(true, true, true, true));
        if (job.HasError || job.OutputPath is null || !File.Exists(job.OutputPath)) throw new InvalidOperationException("Public batch audio download failed: " + job.Status);
        var batchJson = await MediaDownload.RunAsync("ffprobe", ["-v", "error", "-show_entries", "format_tags:stream=codec_type:stream_disposition=attached_pic", "-of", "json", job.OutputPath]);
        using var batchParsed = System.Text.Json.JsonDocument.Parse(batchJson);
        var batchTags = batchParsed.RootElement.GetProperty("format").GetProperty("tags").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString(), StringComparer.OrdinalIgnoreCase);
        if (batchTags.GetValueOrDefault("artist") != tags?.Artist || batchTags.GetValueOrDefault("title") != tags?.Title || batchTags.GetValueOrDefault("album") != tags?.Album || !batchParsed.RootElement.GetProperty("streams").EnumerateArray().Any(x => x.GetProperty("codec_type").GetString() == "video" && x.TryGetProperty("disposition", out var d) && d.GetProperty("attached_pic").GetInt32() == 1)) throw new InvalidOperationException("Batch audio tags or cover missing.");
        Console.WriteLine("Authenticated public single and batch audio-only downloads with embedded cover and metadata passed."); success = true;
    }
    finally { if (success) Directory.Delete(directory, true); else Console.WriteLine("Audio metadata test artifacts retained at " + directory); }
    return;
}
if (args.Contains("--account-keyword"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    var storedCookie = CredentialStore.Load() ?? throw new InvalidOperationException("No saved account session.");
    var accountApi = new BiliApi(); accountApi.SetCookie(storedCookie);
    const string keyword = "初音未来";
    var searchResult = await accountApi.SearchVideosAsync(keyword, 1, "pubdate");
    var sample = searchResult.Items.FirstOrDefault(x => x.PublishUnix > 0 && (x.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) || x.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase) || x.Tags.Contains(keyword, StringComparison.OrdinalIgnoreCase))) ?? throw new InvalidOperationException("No eligible search result for keyword test.");
    var directory = Path.Combine(Path.GetTempPath(), "bili-keyword-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    var success = false;
    try
    {
        var outputDirectory = Path.Combine(directory, "output"); Directory.CreateDirectory(outputDirectory);
        var store = new BackupStore(Path.Combine(directory, "test.db"));
        store.AddKeyword(keyword, sample.PublishUnix, outputDirectory, 5);
        var engine = new KeywordEngine(store, new LimitedLiveKeywordGateway(accountApi, sample));
        await engine.RunDueAsync();
        var files = Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories);
        if (files.Length != 1 || new FileInfo(files[0]).Length == 0 || !store.ListKeywords().Single().Status.Contains("新增 1，失败 0", StringComparison.Ordinal)) throw new InvalidOperationException("Authenticated keyword test failed: " + store.ListKeywords().Single().Status);
        store.RunKeywordNow(store.ListKeywords().Single().Id); await engine.RunDueAsync();
        if (Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories).Length != 1 || !store.ListKeywords().Single().Status.Contains("新增 0，失败 0", StringComparison.Ordinal)) throw new InvalidOperationException("Authenticated keyword deduplication failed");
        Console.WriteLine($"Authenticated keyword search, one-item download and repeat deduplication passed: {new FileInfo(files[0]).Length} bytes.");
        success = true;
    }
    finally { if (success) Directory.Delete(directory, true); else Console.WriteLine("Test artifacts retained at " + directory); }
    return;
}
if (args.Contains("--account-followed"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    var storedCookie = CredentialStore.Load() ?? throw new InvalidOperationException("No saved account session.");
    var accountApi = new BiliApi(); accountApi.SetCookie(storedCookie);
    var accountFollowed = await accountApi.GetCollectedFavoriteFoldersAsync();
    var folder = accountFollowed.OrderBy(x => x.Count).FirstOrDefault() ?? throw new InvalidOperationException("No followed favorite folder available.");
    var contents = await accountApi.GetFavoriteContentsAsync(folder.Id);
    Console.WriteLine($"Authenticated followed folder content passed: {contents.Videos.Count} videos, {contents.Invalid} unavailable, {contents.Unsupported} unsupported.");
    return;
}
if (args.Contains("--account-uploader") || args.Contains("--account-uploader-public"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    var storedCookie = CredentialStore.Load() ?? throw new InvalidOperationException("No saved account session.");
    var accountApi = new BiliApi(); accountApi.SetCookie(storedCookie);
    var account = await accountApi.GetAccountInfoAsync();
    var sample = args.Contains("--account-uploader-public") ? await accountApi.GetVideoAsync("BV1LpD3YsETa") : null;
    var uploads = await accountApi.GetUploaderVideosAsync(sample?.OwnerId ?? account.Mid);
    if (sample is not null && !uploads.Any(x => x.Bvid == sample.Bvid)) throw new InvalidOperationException("Known sample missing from uploader results.");
    Console.WriteLine($"Authenticated uploader listing passed: {uploads.Count} videos.");
    return;
}
if (args.Contains("--account-check") || args.Contains("--account-full-backup"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    var cookie = CredentialStore.Load() ?? throw new InvalidOperationException("No saved account session.");
    var accountApi = new BiliApi(); accountApi.SetCookie(cookie);
    var account = await accountApi.GetAccountInfoAsync();
    var ownFolders = await accountApi.GetFavoriteFoldersAsync();
    var accountFollowedFolders = await accountApi.GetCollectedFavoriteFoldersAsync();
    Console.WriteLine($"Authenticated account check passed: {ownFolders.Count} created folders, {accountFollowedFolders.Count} followed folders.");
    var selectedFolder = ownFolders.Where(f => f.Count > 0).OrderBy(f => f.Count).FirstOrDefault() ?? throw new InvalidOperationException("No nonempty created folder available for test.");
    var contents = await accountApi.GetFavoriteContentsAsync(selectedFolder.Id);
    var selectedVideo = contents.Videos.FirstOrDefault() ?? throw new InvalidOperationException("No available video in selected test folder.");
    var directory = Path.Combine(Path.GetTempPath(), "bili-account-check-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var success = false;
    try
    {
        var outputDirectory = Path.Combine(directory, "output"); Directory.CreateDirectory(outputDirectory);
        var store = new BackupStore(Path.Combine(directory, "test.db"));
        store.Add(account.Mid, selectedFolder, outputDirectory, 5);
        var fullBackup = args.Contains("--account-full-backup");
        IBackupGateway gateway = fullBackup ? new BiliBackupGateway(accountApi) : new LimitedLiveBackupGateway(accountApi, selectedVideo);
        var engine = new BackupEngine(store, gateway);
        await engine.RunDueAsync();
        var status = store.List().Single().Status;
        var files = Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories);
        if (!status.Contains("失败 0", StringComparison.Ordinal) || files.Length == 0 || !fullBackup && files.Length != 1 || files.Any(f => new FileInfo(f).Length == 0)) throw new InvalidOperationException("Authenticated backup failed: " + status);
        store.RunNow(store.List().Single().Id); await engine.RunDueAsync();
        if (Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories).Length != files.Length || !store.List().Single().Status.Contains("新增 0，失败 0", StringComparison.Ordinal)) throw new InvalidOperationException("Authenticated backup deduplication failed");
        Console.WriteLine($"Authenticated {(fullBackup ? "full-folder" : "single-item")} backup and repeat deduplication passed: {files.Length} files, {files.Sum(f => new FileInfo(f).Length)} bytes.");
        success = true;
    }
    finally { if (success) Directory.Delete(directory, true); else Console.WriteLine("Test artifacts retained at " + directory); }
    return;
}
if (!args.Contains("--live")) return;
var api = new BiliApi();
var video = await api.GetVideoAsync("BV1xx411c7mD");
Console.WriteLine($"Video: {video.Title}; pages: {video.Pages.Count}");
var streams = await api.GetStreamsAsync(video.Bvid, video.Pages[0].Cid);
Console.WriteLine($"DASH video streams: {streams.Video.Count}");
if (streams.Video.Count == 0) throw new Exception("No streams returned.");

sealed class LimitedLiveKeywordGateway(BiliApi api, SearchVideo sample) : IKeywordGateway
{
    private readonly LimitedLiveBackupGateway media = new(api, new CollectionVideo(sample.Bvid, sample.Title));
    public long SessionVersion => api.SessionVersion;
    public Task<AccountInfo> AccountAsync() => media.AccountAsync();
    public Task<FavoriteContents> FavoritesAsync(long folder, CancellationToken cancellation) => media.FavoritesAsync(folder, cancellation);
    public Task<VideoInfo> VideoAsync(string bvid) => media.VideoAsync(bvid);
    public Task<MediaStreams> StreamsAsync(string bvid, VideoPage page) => media.StreamsAsync(bvid, page);
    public Task InDownloadSlotAsync(Func<Task> work, CancellationToken cancellation, Func<bool> canStart) => media.InDownloadSlotAsync(work, cancellation, canStart);
    public Task DownloadWithinSlotAsync(VideoStream video, AudioStream audio, string destination, CancellationToken cancellation, Func<bool> canStart) => media.DownloadWithinSlotAsync(video, audio, destination, cancellation, canStart);
    public Task<VideoSearchPage> SearchAsync(string keyword, int page) => Task.FromResult(new VideoSearchPage(1, 1, [sample]));
}

sealed class LimitedLiveBackupGateway(BiliApi api, CollectionVideo sample) : IBackupGateway
{
    public long SessionVersion => api.SessionVersion;
    public Task<AccountInfo> AccountAsync() => api.GetAccountInfoAsync();
    public Task<FavoriteContents> FavoritesAsync(long folder, CancellationToken cancellation) => Task.FromResult(new FavoriteContents([sample], 0, 0));
    public async Task<VideoInfo> VideoAsync(string bvid)
    {
        var result = await api.GetVideoAsync(bvid);
        return result with { Pages = result.Pages.Take(1).ToArray() };
    }
    public async Task<MediaStreams> StreamsAsync(string bvid, VideoPage page)
    {
        var streams = await api.GetStreamsForPageAsync(bvid, page);
        return streams with { Video = streams.Video.OrderBy(x => x.Bandwidth).Take(1).ToArray(), Audio = streams.Audio.OrderBy(x => x.Bandwidth).Take(1).ToArray() };
    }
    public Task InDownloadSlotAsync(Func<Task> work, CancellationToken cancellation, Func<bool> canStart) => MediaDownload.InSlotAsync(work, cancellation, canStart);
    public Task DownloadWithinSlotAsync(VideoStream video, AudioStream audio, string destination, CancellationToken cancellation, Func<bool> canStart)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!canStart()) throw new InvalidOperationException("Fixture rule changed");
        return MediaDownload.DownloadWithinSlotAsync(video.Urls, audio.Urls, null, destination, api.UserAgent, new Progress<string>());
    }
}

sealed class UpdateFixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    public List<string> Hosts { get; } = [];
    public bool SentCookie { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Hosts.Add(request.RequestUri!.Host); SentCookie |= request.Headers.Contains("Cookie") || request.Headers.Authorization is not null;
        return handler(request, cancellationToken);
    }
}

sealed class MemoryStartupStore : IStartupValueStore
{
    public object? Value { get; set; }
    public int Writes { get; private set; }
    public int Deletes { get; private set; }
    public object? Read() => Value;
    public void Write(string command) { Value = command; Writes++; }
    public void Delete() { Value = null; Deletes++; }
}

sealed class CaptureProgress : IProgress<string>
{
    public List<string> Messages { get; } = [];
    public void Report(string value) => Messages.Add(value);
}

sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }

sealed class SizeProbeFixture : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Range?.ToString() != "bytes=0-0" || request.Headers.Contains("Cookie") || request.Headers.UserAgent.ToString() != "BiliDesktop-Fixture/1.0") throw new Exception("Size probe headers were incorrect");
        if (request.RequestUri!.AbsolutePath == "/fail") return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent) { Content = new ByteArrayContent([0]) };
        response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 0, 12345678);
        return Task.FromResult(response);
    }
}

sealed class SlowMediaServer : IDisposable
{
    public const int Length = 8 * 1024 * 1024;
    private readonly System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    public string Url { get; }
    public SlowMediaServer()
    {
        listener.Start(); Url = $"http://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}/slow";
        _ = ServeAsync();
    }
    private async Task ServeAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                _ = SendAsync(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
    private async Task SendAsync(System.Net.Sockets.TcpClient client)
    {
        using (client)
        try
        {
            var stream = client.GetStream(); var buffer = new byte[8192]; var received = await stream.ReadAsync(buffer, stop.Token);
            var request = System.Text.Encoding.ASCII.GetString(buffer, 0, received);
            var range = System.Text.RegularExpressions.Regex.Match(request, @"(?im)^Range:\s*bytes=(\d+)-");
            var start = range.Success ? int.Parse(range.Groups[1].Value) : 0;
            var header = range.Success ? $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{Length - 1}/{Length}\r\n" : "HTTP/1.1 200 OK\r\n";
            var headers = System.Text.Encoding.ASCII.GetBytes(header + $"Content-Length: {Length - start}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, stop.Token);
            var chunk = new byte[65536];
            for (var sent = start; sent < Length; sent += chunk.Length) { await stream.WriteAsync(chunk.AsMemory(0, Math.Min(chunk.Length, Length - sent)), stop.Token); await Task.Delay(30, stop.Token); }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException) { }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); }
}

sealed class TimeoutHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new OperationCanceledException();
}

sealed class BlockingPackageHandler : HttpMessageHandler
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("Unexpected package response");
    }
}

sealed class PackageHandler(byte[] package) : HttpMessageHandler
{
    public int Requests { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
    }
}

sealed class ShortLinkHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host != "b23.tv" || request.Headers.Contains("Cookie")) throw new Exception("Short link request leaked credentials or followed redirect");
        var location = request.RequestUri.AbsolutePath switch
        {
            "/start" => "/next",
            "/next" => "https://m.bilibili.com/video/BV1xx411c7mD?p=2",
            "/bad" => "https://example.com/video/BV1xx411c7mD",
            "/loop" => "/loop",
            "/insecure" => "http://www.bilibili.com/video/BV1xx411c7mD",
            _ => throw new Exception("Unexpected short link request")
        };
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return Task.FromResult(response);
    }
}

sealed class FixtureHandler(string collectedMode = "", bool previewVideo = false, Action? onStream = null, string? paidPreview = null) : HttpMessageHandler
{
    private int streamRequests;
    public int StreamRequests => Volatile.Read(ref streamRequests);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValues("User-Agent", out var ua) || string.Join("", ua) != "BiliDesktop-Fixture/1.0") throw new Exception("API did not send configured user agent");
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/x/player/wbi/playurl") { Interlocked.Increment(ref streamRequests); onStream?.Invoke(); }
        var json = path switch
        {
            "/x/web-interface/view" when paidPreview is not null && request.RequestUri.Query == "?bvid=BV1xx411c7mD" => """{"code":0,"data":{"bvid":"BV1xx411c7mD","title":"test","owner":{"name":"author","mid":123},"rights":{"ugc_pay":1},"pages":[{"cid":42,"page":1,"part":"one"}]}}""",
            "/x/web-interface/view" when request.RequestUri.Query == "?bvid=BV0000000001" => """{"code":0,"data":{"bvid":"BV0000000001","title":"长标题","owner":{"name":"author","mid":123},"ugc_season":{"id":456,"title":"示例合集","sections":[{"episodes":[{"bvid":"BV0000000002","title":"前一集"},{"bvid":"BV0000000001","title":"单集名称"}]}]},"pages":[{"cid":42,"page":1,"part":"one"}]}}""",
            "/x/web-interface/view" when request.RequestUri.Query is "?aid=123" or "?bvid=BV1xx411c7mD" => """{"code":0,"data":{"bvid":"BV1xx411c7mD","title":"test","owner":{"name":"author","mid":123},"ugc_season":{"id":456},"pages":[{"cid":42,"page":1,"part":"one"}]}}""",
            "/x/player/wbi/v2" when paidPreview == "true" && request.RequestUri.Query.Contains("w_rid=") => """{"code":0,"data":{"is_ugc_pay_preview":true}}""",
            "/x/player/wbi/v2" when paidPreview == "false" && request.RequestUri.Query.Contains("w_rid=") => """{"code":0,"data":{"is_ugc_pay_preview":false}}""",
            "/x/player/wbi/v2" when paidPreview == "missing" && request.RequestUri.Query.Contains("w_rid=") => """{"code":0,"data":{}}""",
            "/x/web-interface/nav" when request.Headers.TryGetValues("Cookie", out var auth) && auth.Any(c => c.Contains("SESSDATA=fixture")) => """{"code":0,"data":{"isLogin":true,"uname":"fixture","mid":123,"wbi_img":{"img_url":"https://example.com/abcdefghijklmnopqrstuvwxyz123456.png","sub_url":"https://example.com/ABCDEFGHIJKLMNOPQRSTUVWXYZ123456.png"}}}""",
            "/x/web-interface/nav" => """{"code":-101,"data":{"wbi_img":{"img_url":"https://example.com/abcdefghijklmnopqrstuvwxyz123456.png","sub_url":"https://example.com/ABCDEFGHIJKLMNOPQRSTUVWXYZ123456.png"}}}""",
            "/x/player/wbi/playurl" when previewVideo && request.RequestUri.Query.Contains("w_rid=") => """{"code":0,"data":{"is_preview":1,"dash":{"video":[],"audio":[]}}}""",
            "/x/player/wbi/playurl" when request.RequestUri.Query.Contains("w_rid=") => """{"code":0,"data":{"dash":{"video":[{"id":32,"codecs":"avc","bandwidth":100,"baseUrl":"https://example.com/a"},{"id":80,"codecs":"avc","bandwidth":200,"baseUrl":"https://example.com/b","backupUrl":["https://example.com/backup"]}],"audio":[{"id":30280,"codecs":"aac","bandwidth":192000,"baseUrl":"https://example.com/audio"}],"dolby":{"audio":[{"id":30250,"codecs":"ec-3","bandwidth":448000,"baseUrl":"https://example.com/dolby"}]},"flac":{"audio":{"id":30251,"codecs":"flac","bandwidth":1000000,"baseUrl":"https://example.com/flac","backupUrl":["https://example.com/flac2"]}}}}}""",
            "/x/passport-login/web/qrcode/generate" => """{"code":0,"data":{"url":"https://passport.bilibili.com/qr","qrcode_key":"test-key"}}""",
            "/x/passport-login/web/qrcode/poll" => """{"code":0,"data":{"code":0}}""",
            "/x/frontend/finger/spi" => """{"code":0,"data":{"b_3":"fixture-device"}}""",
            "/x/polymer/web-space/seasons_archives_list" when request.RequestUri.Query.Contains("w_rid=") => CollectionJson(request.RequestUri.Query.Contains("page_num=2") ? 2 : 1),
            "/x/space/wbi/arc/search" when request.RequestUri.Query.Contains("w_rid=") => UploaderJson(request.RequestUri.Query.Contains("pn=2") ? 2 : 1),
            "/x/v3/fav/folder/created/list-all" => """{"code":0,"data":{"count":1,"list":[{"id":456,"title":"fixture folder","media_count":21}]}}""",
            "/x/v3/fav/folder/collected/list" when request.RequestUri.Query.Contains("up_mid=123") => CollectedFoldersJson(request.RequestUri.Query.Contains("pn=2") ? 2 : 1, collectedMode),
            "/x/v3/fav/resource/list" => FavoriteJson(request.RequestUri.Query.Contains("pn=2") ? 2 : 1),
            "/pgc/view/web/season" => """{"code":0,"result":{"title":"fixture season","cover":"https://example.com/c.jpg","episodes":[{"id":123,"cid":1,"bvid":"BV0000000001","title":"1","long_title":"one"}],"section":[{"title":"extra","episodes":[{"id":124,"cid":2,"bvid":"BV0000000002","title":"2","long_title":"two"}]}]}}""",
            "/pgc/player/web/playurl" when request.RequestUri.Query.Contains("ep_id=123") => """{"code":0,"result":{"dash":{"video":[{"id":64,"codecs":"avc","bandwidth":200,"base_url":"https://example.com/v","backup_url":["https://example.com/v2"]}]}}}""",
            "/pgc/player/web/playurl" when request.RequestUri.Query.Contains("ep_id=124") => """{"code":0,"result":{"is_preview":1,"dash":{"video":[{"id":64,"codecs":"avc","bandwidth":200,"baseUrl":"https://example.com/v"}]}}}""",
            "/pgc/player/web/playurl" when request.RequestUri.Query.Contains("ep_id=125") => """{"code":0,"result":{"dash":{"audio":[{"id":30216,"codecs":"aac","bandwidth":64000,"base_url":"https://example.com/a","backup_url":["https://example.com/a2"]}]}}}""",
            "/pugv/view/web/season" => """{"code":0,"data":{"season_id":61,"title":"fixture course","cover":"https://example.com/cover.jpg","episodes":[],"episode_page":{"next":true,"total":4},"up_info":{"uname":"fixture teacher"}}}""",
            "/pugv/view/web/ep/list" when request.RequestUri.Query.Contains("pn=1") => """{"code":0,"data":{"page":{"next":true,"total":4},"items":[{"id":790,"cid":21,"aid":11,"status":1,"title":"first"},{"id":792,"cid":22,"aid":12,"status":2,"title":"locked"}]}}""",
            "/pugv/view/web/ep/list" when request.RequestUri.Query.Contains("pn=2") => """{"code":0,"data":{"page":{"next":false,"total":4},"items":[{"id":793,"cid":23,"aid":13,"status":1,"title":"third"},{"id":794,"cid":0,"aid":0,"title":"upcoming"}]}}""",
            "/pugv/player/web/playurl" when request.RequestUri.Query.Contains("ep_id=790") && request.RequestUri.Query.Contains("avid=11") && request.RequestUri.Query.Contains("cid=21") => """{"code":0,"data":{"is_preview":0,"dash":{"video":[{"id":64,"codecs":"avc","bandwidth":200,"baseUrl":"https://example.com/v"}],"audio":[{"id":30216,"codecs":"aac","bandwidth":100,"baseUrl":"https://example.com/a"}]}}}""",
            "/pugv/player/web/playurl" when request.RequestUri.Query.Contains("ep_id=791") => """{"code":0,"data":{"durl":[{"url":"https://example.com/part"}]}}""",
            "/x/web-interface/wbi/search/type" when request.RequestUri.Query.Contains("keyword=%E6%B5%8B%E8%AF%95%20keyword") && request.RequestUri.Query.Contains("order=pubdate") && request.Headers.TryGetValues("Cookie", out var cookies) && cookies.Any(c => c.Contains("buvid3=fixture-device")) => """{"code":0,"data":{"numPages":2,"result":[{"bvid":"BV1xx411c7mD","title":"<em class=\"keyword\">测试</em> &amp; video","author":"author","duration":"1:00"}]}}""",
            _ => throw new Exception("Unexpected request: " + request.RequestUri)
        };
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };
        if (path.EndsWith("/qrcode/poll")) response.Headers.TryAddWithoutValidation("Set-Cookie", "SESSDATA=test-session; Domain=.bilibili.com; Path=/; Max-Age=3600; Secure; HttpOnly");
        return Task.FromResult(response);
    }

    private static string CollectionJson(int page)
    {
        var numbers = page == 1 ? Enumerable.Range(1, 30) : [31];
        return System.Text.Json.JsonSerializer.Serialize(new { code = 0, data = new { meta = new { name = "合集·示例合集" }, page = new { total = 31 }, archives = numbers.Select(n => new { bvid = $"BV{n:D10}", title = $"video {n}" }) } });
    }

    private static string UploaderJson(int page)
    {
        var numbers = page == 1 ? Enumerable.Range(1, 30) : [31];
        return System.Text.Json.JsonSerializer.Serialize(new { code = 0, data = new { page = new { count = 31 }, list = new { vlist = numbers.Select(n => new { bvid = $"BV{n:D10}", title = $"video {n}" }) } } });
    }

    private static string FavoriteJson(int page)
    {
        var numbers = page == 1 ? Enumerable.Range(1, 20) : [21];
        var items = numbers.Select(n => new { type = n == 19 ? 12 : 2, attr = n == 20 ? 1 : 0, bvid = $"BV{n:D10}", title = $"favorite {n}" });
        return System.Text.Json.JsonSerializer.Serialize(new { code = 0, data = new { info = new { media_count = 21 }, medias = items, has_more = page == 1 } });
    }

    private static string CollectedFoldersJson(int page, string mode)
    {
        var numbers = page == 1 ? Enumerable.Range(1, 20) : mode == "short" ? [] : mode == "duplicate" ? [1] : [21];
        return System.Text.Json.JsonSerializer.Serialize(new { code = 0, data = new { count = 21, list = numbers.Select(n => new { id = 1000 + n, title = $"folder {n}", media_count = n, state = n == 20 ? 1 : 0 }) } });
    }
}

sealed class BackupFixtureGateway : IKeywordGateway
{
    public long SessionVersion { get; set; }
    public long AccountId { get; set; } = 123;
    public string FirstLabel { get; set; } = "P1 · one";
    public bool ChangeSessionDuringStreams { get; set; }
    public int SearchCalls { get; private set; }
    private int successes;
    public int Successes => Volatile.Read(ref successes);
    public bool FailSecondOnce { get; set; }
    public bool UseFlac { get; set; }
    public bool IncludeDuration { get; set; }
    public string? SearchError { get; set; }
    public int StreamCalls { get; private set; }
    public Task<AccountInfo> AccountAsync() => Task.FromResult(new AccountInfo(AccountId, "fixture"));
    public Task<FavoriteContents> FavoritesAsync(long folder, CancellationToken cancellation) => Task.FromResult(new FavoriteContents([new("BV1xx411c7mD", "fixture")], 0, 0));
    public Task<VideoSearchPage> SearchAsync(string keyword, int page)
    {
        if (SearchError is not null) throw new InvalidOperationException(SearchError);
        SearchCalls++;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Task.FromResult(page == 1
            ? new VideoSearchPage(1, 2, [new("BV1xx411c7mD", "cat video", "owner", "1:00", now), new("BV0000000001", "unrelated", "owner", "1:00", now)])
            : new VideoSearchPage(2, 2, [new("BV0000000002", "cat old", "owner", "1:00", 1000)]));
    }
    public Task<VideoInfo> VideoAsync(string bvid) => Task.FromResult(new VideoInfo(bvid, "fixture", "owner", "", [new(11, FirstLabel, DurationSeconds: IncludeDuration ? 10 : null), new(22, "P2 · two", DurationSeconds: IncludeDuration ? 10 : null)], 123, null));
    public Task<MediaStreams> StreamsAsync(string bvid, VideoPage page)
    {
        StreamCalls++;
        if (ChangeSessionDuringStreams) { SessionVersion++; ChangeSessionDuringStreams = false; }
        return Task.FromResult(new MediaStreams([new(80, "avc", 200, "https://example.com/v", [])], UseFlac ? [new(30251, "flac", 1000, "https://example.com/a", [])] : [new(1, "aac", 100, "https://example.com/a", [])]));
    }
    public Task InDownloadSlotAsync(Func<Task> work, CancellationToken cancellation, Func<bool> canStart) => MediaDownload.InSlotAsync(work, cancellation, canStart);
    public Task<bool> IsCompleteVideoAsync(string path, double? duration, CancellationToken cancellation) => Task.FromResult(File.Exists(path) && File.ReadAllText(path) != "corrupt");
    public async Task DownloadWithinSlotAsync(VideoStream video, AudioStream audio, string destination, CancellationToken cancellation, Func<bool> canStart)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!canStart()) throw new InvalidOperationException("Fixture rule changed");
        if (FailSecondOnce && (Path.GetFileName(destination).Contains("_P002_") || Path.GetFileName(destination).Contains("-002-"))) { FailSecondOnce = false; throw new IOException("fixture failure"); }
        await File.WriteAllTextAsync(destination, "fixture"); Interlocked.Increment(ref successes);
    }
}

sealed class ConcurrentBackupFixtureGateway : IBackupGateway
{
    private readonly BackupFixtureGateway inner = new();
    private int active;
    private readonly object sync = new();
    public int Maximum { get; private set; }
    public int Successes => inner.Successes;
    public long SessionVersion => inner.SessionVersion;
    public Task<AccountInfo> AccountAsync() => inner.AccountAsync();
    public Task<FavoriteContents> FavoritesAsync(long folder, CancellationToken cancellation) => inner.FavoritesAsync(folder, cancellation);
    public Task<VideoInfo> VideoAsync(string bvid) => inner.VideoAsync(bvid);
    public Task<MediaStreams> StreamsAsync(string bvid, VideoPage page) => inner.StreamsAsync(bvid, page);
    public Task InDownloadSlotAsync(Func<Task> work, CancellationToken cancellation, Func<bool> canStart) => MediaDownload.InSlotAsync(work, cancellation, canStart);
    public Task<bool> IsCompleteVideoAsync(string path, double? duration, CancellationToken cancellation) => inner.IsCompleteVideoAsync(path, duration, cancellation);
    public async Task DownloadWithinSlotAsync(VideoStream video, AudioStream audio, string destination, CancellationToken cancellation, Func<bool> canStart)
    {
        var count = Interlocked.Increment(ref active);
        lock (sync) Maximum = Math.Max(Maximum, count);
        try { await Task.Delay(150, cancellation); await inner.DownloadWithinSlotAsync(video, audio, destination, cancellation, canStart); }
        finally { Interlocked.Decrement(ref active); }
    }
}
