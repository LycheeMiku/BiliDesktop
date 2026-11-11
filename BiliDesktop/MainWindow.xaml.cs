using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.IO;
using System.Windows.Threading;
using System.Security.Cryptography;
using System.Text;

namespace BiliDesktop;

public partial class MainWindow : Window
{
    private readonly BiliApi api;
    private VideoInfo? video;
    private MediaStreams? media;
    private bool loadingCollection;
    private int requestVersion;
    private string? lastParsedInput;
    private string? parsingInput;
    private int sizeVersion;
    private CancellationTokenSource? sizeCancellation;
    private int authVersion;
    private BackupStore? backupStore;
    private BackupEngine? backupEngine;
    private KeywordEngine? keywordEngine;
    private readonly CancellationTokenSource backupCancellation = new();
    private readonly DispatcherTimer backupTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(2) };
    private readonly CancellationTokenSource updateCancellation = new();
    private readonly UpdateChecker updateChecker;
    private bool updateChecking;
    private bool updatesStarted;
    private AvailableRelease? availableRelease;
    private UpdateWindow? updateWindow;
    private Mutex? backupMutex;
    private bool ownsBackupMutex;
    private LiveManager? liveManager;
    private bool liveStopping;
    private bool liveCloseAllowed;
    private readonly DownloadCloseGuard downloadCloseGuard = new();
    private bool allowBackground;
    private bool mainClosed;
    private bool exitRequested;
    private BackgroundWindowController? background;
    private KeywordWindow? keywordWindow;
    private AboutWindow? aboutWindow;
    private SettingsWindow? settingsWindow;
    private readonly IStartupValueStore startupStore;
    private string? startupExecutable;

    public MainWindow() : this(new BiliApi(), true) { }
    internal MainWindow(BiliApi api, bool enableBackground, IStartupValueStore? startupValueStore = null, string? startupPath = null, UpdateChecker? updateChecker = null, bool? startUpdateChecks = null)
    {
        this.api = api; this.updateChecker = updateChecker ?? new UpdateChecker(); startupStore = startupValueStore ?? new WindowsStartupStore();
        InitializeComponent();
        if (enableBackground || startupValueStore is not null) try
        {
            startupExecutable = startupPath ?? Environment.ProcessPath;
            if (startupExecutable is null || !Path.GetFileName(startupExecutable).Equals("BiliDesktop.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("无法确定当前程序路径。");
        }
        catch (Exception ex) { startupExecutable = null; Status.Text = "开机启动不可用：" + ex.Message; }
        QualityLabel.SetResourceReference(TextBlock.TextProperty, Localizer.Key("清晰度")); CodecLabel.SetResourceReference(TextBlock.TextProperty, Localizer.Key("视频编码"));
        SettingsButton.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("设置"));
        LiveButton.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("直播录制"));
        AccountLabel.SetResourceReference(TextBlock.TextProperty, Localizer.Key("账号"));
        EmptyState.SetResourceReference(TextBlock.TextProperty, Localizer.Key("输入视频链接、BV 或 AV 号"));
        DownloadQueueButton.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("下载任务"));
        AboutButton.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("关于"));
        UpdateAvailable.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("更新可用"));
        try { allowBackground = AppSettings.LoadAllowBackground(); } catch (Exception) { allowBackground = false; }
        try { api.SetUserAgent(AppSettings.LoadUserAgent()); } catch (Exception) { api.SetUserAgent(BiliApi.DefaultUserAgent); }
        try { MediaDownload.Configure(AppSettings.LoadTransferOptions()); } catch (Exception) { MediaDownload.Configure(new(8, 16)); }
        if (enableBackground) try
        {
            backupStore = new BackupStore(); var gateway = new QueuedBackupGateway(api);
            backupEngine = new BackupEngine(backupStore, gateway); keywordEngine = new KeywordEngine(backupStore, gateway);
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..16];
            backupMutex = new Mutex(true, @"Local\BiliDesktopBackup-" + identity, out ownsBackupMutex);
            backupTimer.Tick += (_, _) => RunBackups();
        }
        catch (Exception ex) { Status.Text = "自动备份不可用：" + ex.Message; }
        if (enableBackground) Loaded += async (_, _) =>
        {
            if (Environment.GetCommandLineArgs().Skip(1).Any(arg => arg.Equals("--background", StringComparison.OrdinalIgnoreCase))) EnterBackground();
            try { var stored = CredentialStore.LoadCredential(); if (stored is not null && !string.IsNullOrEmpty(stored.Cookie)) await AuthenticateAsync(stored.Cookie, true, stored.Expires); }
            catch (Exception) { LoginStatus.Text = "无法读取保存的登录信息，请重新登录。"; }
            try { if (!backupCancellation.IsCancellationRequested) liveManager = new LiveManager(api.RecorderCookie); }
            catch (Exception ex) { Status.Text = "直播录制不可用：" + ex.Message; }
            if (!backupCancellation.IsCancellationRequested && backupEngine is not null) { backupTimer.Start(); RunBackups(); }
        };
        if (startUpdateChecks ?? enableBackground) Loaded += (_, _) => { if (updatesStarted) return; updatesStarted = true; updateTimer.Tick += async (_, _) => await CheckUpdatesAsync(); updateTimer.Start(); _ = CheckUpdatesAsync(); };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            mainClosed = true; ++authVersion;
            backupTimer.Stop(); backupCancellation.Cancel(); updateTimer.Stop(); updateCancellation.Cancel();
            updateWindow?.Close();
            keywordWindow?.Close();
            settingsWindow?.Close();
            sizeCancellation?.Cancel(); sizeCancellation?.Dispose();
            background?.Dispose();
            liveManager?.Dispose();
            if (ownsBackupMutex) { try { backupMutex?.ReleaseMutex(); } catch (ApplicationException) { } }
            backupMutex?.Dispose();
        };
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    internal void ActivateFromAnotherInstance()
    {
        if (mainClosed) return;
        if (background?.IsActive == true) background.RestoreForActivation();
        else
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        }
        if (!ExclusiveWindow.ActivateExisting()) Activate();
    }
    internal SettingsWindow? ShowSettings()
    {
        if (settingsWindow is not null)
        {
            if (!settingsWindow.IsVisible) settingsWindow.Show();
            if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
            settingsWindow.Activate(); return settingsWindow;
        }
        if (ExclusiveWindow.ActivateExisting()) return null;
        var window = new SettingsWindow(api, startupStore, startupExecutable, ReparseAfterUserAgentChangeAsync, enabled => allowBackground = enabled);
        settingsWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(settingsWindow, window)) settingsWindow = null; };
        try { ExclusiveWindow.Show(window); } catch { settingsWindow = null; throw; }
        return window;
    }
    private async Task ReparseAfterUserAgentChangeAsync()
    {
        if (mainClosed) return;
        var previous = parsingInput ?? (video is not null ? lastParsedInput : null);
        if (previous is not null) await ParseVideoAsync(previous, video is null ? null : Pages.SelectedIndex);
    }
    private void EnterBackground()
    {
        try
        {
            background ??= new BackgroundWindowController(this, Localizer.Translate, () => { exitRequested = true; Close(); }, ShowAbout);
            background.Enter();
        }
        catch (Exception ex) { Status.Text = "后台运行不可用：" + ex.Message; }
    }
    private void DownloadQueue_Click(object sender, RoutedEventArgs e) => WindowCoordinator.ShowDownloads(DownloadQueue.Shared);
    internal async Task CheckUpdatesAsync(string? zone = null)
    {
        if (updateChecking || mainClosed || updateCancellation.IsCancellationRequested) return;
        updateChecking = true;
        try
        {
            var result = await updateChecker.CheckAsync(zone ?? TimeZoneInfo.Local.Id, updateCancellation.Token);
            if (mainClosed || updateCancellation.IsCancellationRequested) return;
            availableRelease = result; UpdateAvailable.Visibility = result is null ? Visibility.Collapsed : Visibility.Visible;
            if (result is not null) ShowUpdateResult(result, null);
            else updateWindow?.Close();
        }
        catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { }
        catch (Exception) { if (!mainClosed && !updateCancellation.IsCancellationRequested) { Status.Text = Localizer.Translate("更新检查失败，请检查网络连接后重试。"); ShowUpdateResult(null, "更新检查失败，请检查网络连接后重试。"); } }
        finally { updateChecking = false; }
    }
    private void UpdateAvailable_Click(object sender, RoutedEventArgs e) { if (availableRelease is not null) ShowUpdateResult(availableRelease, null); }
    private void ShowUpdateResult(AvailableRelease? release, string? error)
    {
        if (updateWindow is not null)
        {
            updateWindow.SetResult(release, error);
            if (!updateWindow.IsVisible) updateWindow.Show();
            if (updateWindow.WindowState == WindowState.Minimized) updateWindow.WindowState = WindowState.Normal;
            updateWindow.Activate(); return;
        }
        var window = new UpdateWindow(release, error); updateWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(updateWindow, window)) updateWindow = null; };
        try { window.Show(); } catch { updateWindow = null; throw; }
    }
    private void About_Click(object sender, RoutedEventArgs e) => ShowAbout();
    private void ShowAbout()
    {
        if (aboutWindow is not null)
        {
            if (!aboutWindow.IsVisible) aboutWindow.Show();
            if (aboutWindow.WindowState == WindowState.Minimized) aboutWindow.WindowState = WindowState.Normal;
            aboutWindow.Activate(); return;
        }
        if (ExclusiveWindow.ActivateExisting()) return;
        var window = new AboutWindow(); aboutWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(aboutWindow, window)) aboutWindow = null; };
        try { ExclusiveWindow.Show(window); } catch { aboutWindow = null; throw; }
    }

    private void Options_Changed(object sender, SelectionChangedEventArgs e) { if (AudioOptions is not null && DownloadMode is not null) AudioOptions.Visibility = DownloadMode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed; UpdateDownloadAvailability(); UpdateSelectedSize(); }

    private sealed record QualityChoice(int Quality) { public string Label => StreamLabels.Video(Quality); }
    private sealed record CodecChoice(VideoStream Stream) { public string Label => $"{Stream.Codec} · {Stream.Bandwidth / 1000} kbps"; }
    private void Qualities_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Codecs is null || Streams is null) return;
        var quality = (Qualities.SelectedItem as QualityChoice)?.Quality;
        var available = media?.Video.Where(v => v.Quality == quality).Select(v => new CodecChoice(v)).ToArray() ?? [];
        Codecs.ItemsSource = available; Codecs.SelectedIndex = available.Length > 0 ? 0 : -1;
    }
    private void Codecs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Streams is null || Codecs is null) return;
        Streams.SelectedItem = (Codecs.SelectedItem as CodecChoice)?.Stream;
    }
    private async void UpdateSelectedSize()
    {
        if (SelectedSize is null || DownloadMode is null || Streams is null || AudioStreams is null) return;
        sizeCancellation?.Cancel(); sizeCancellation?.Dispose(); sizeCancellation = new CancellationTokenSource();
        var cancellation = sizeCancellation.Token; var version = ++sizeVersion;
        var mode = DownloadMode.SelectedIndex;
        var selectedVideo = mode is 0 or 1 ? Streams.SelectedItem as VideoStream : null;
        var selectedAudio = mode is 0 or 2 ? AudioStreams.SelectedItem as AudioStream : null;
        if (mode == 3 || selectedVideo is null && selectedAudio is null) { SelectedSize.Text = ""; return; }
        if (selectedVideo?.LegacySegments is not null || selectedAudio?.LegacySegments is not null) { SelectedSize.Text = Localizer.Translate("所选流大小未知"); return; }
        SelectedSize.Text = Localizer.Translate("正在查询所选规格大小…");
        var tasks = new[] { selectedVideo?.Urls, selectedAudio?.Urls }.Where(x => x is not null).Select(x => MediaSizeProbe.GetSizeAsync(x!, api.UserAgent, cancellation)).ToArray();
        try
        {
            var sizes = await Task.WhenAll(tasks);
            if (mainClosed || version != sizeVersion || cancellation.IsCancellationRequested) return;
            SelectedSize.Text = sizes.All(x => x.HasValue) ? Localizer.Translate("所选流大小") + $": {sizes.Sum(x => x!.Value) / 1048576d:F1} MiB" : Localizer.Translate("所选流大小未知");
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!mainClosed && version == sizeVersion && !cancellation.IsCancellationRequested) SelectedSize.Text = Localizer.Translate("所选流大小未知"); }
    }

    private void UpdateDownloadAvailability()
    {
        if (!IsInitialized || DownloadButton is null || DownloadMode is null) return;
        var hasVideo = Streams.SelectedItem is VideoStream;
        var hasAudio = AudioStreams.SelectedItem is AudioStream;
        VideoProcess.IsEnabled = DownloadMode.SelectedIndex is 0 or 1;
        DownloadButton.SetResourceReference(ContentControl.ContentProperty, Localizer.Key(media?.IsPreview == true && DownloadMode.SelectedIndex != 3 ? "下载预览" : "下载"));
        BatchButton.IsEnabled = video is not null && video.Pages.Count > 0;
        CollectionButton.IsEnabled = !loadingCollection && video?.OwnerId is > 0 && video.CollectionId is > 0;
        DownloadButton.IsEnabled = DownloadMode.SelectedIndex switch
        {
            0 => hasVideo && hasAudio,
            1 => hasVideo,
            2 => hasAudio,
            3 => !string.IsNullOrWhiteSpace(video?.CoverUrl),
            _ => false
        };
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        await AuthenticateAsync(CookieText.Password, RememberLogin.IsChecked == true);
        CookieText.Clear();
    }

    private async Task AuthenticateAsync(string cookie, bool remember, DateTimeOffset? expires = null)
    {
        var version = ++authVersion;
        try
        {
            api.SetCookie(cookie); LoginStatus.Text = "正在验证…";
            var account = await api.GetAccountInfoAsync();
            if (version != authVersion) return;
            if (remember) CredentialStore.Save(cookie, expires: expires); else CredentialStore.Delete();
            liveManager?.UpdateCookie(api.RecorderCookie);
            LoginStatus.Text = expires.HasValue ? $"{account.Name} · 已登录 · Cookie 到期时间 {expires.Value.LocalDateTime:yyyy-MM-dd HH:mm}" : $"{account.Name} · 已登录 · 有效期未知";
            try
            {
                if (backupStore is not null) foreach (var rule in backupStore.List().Where(r => r.Enabled && r.AccountId == account.Mid)) backupStore.RunNow(rule.Id);
                if (backupStore is not null) foreach (var rule in backupStore.ListKeywords().Where(r => r.Enabled)) backupStore.RunKeywordNow(rule.Id);
                RunBackups();
            }
            catch (Exception ex) { Status.Text = "无法安排自动备份：" + ex.Message; }
        }
        catch (Exception ex)
        {
            if (version != authVersion) return;
            api.SetCookie(""); liveManager?.UpdateCookie(""); LoginStatus.Text = $"验证失败：{ex.Message}";
        }
    }

    private void Qr_Click(object sender, RoutedEventArgs e)
    {
        var expectedVersion = authVersion;
        var window = new QrWindow(api);
        window.LoginCompleted += async (cookie, expires) => { if (!mainClosed && expectedVersion == authVersion) await AuthenticateAsync(cookie, RememberLogin.IsChecked == true, expires); };
        window.Show();
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        ++authVersion; api.SetCookie(""); liveManager?.UpdateCookie(""); CookieText.Clear();
        try { CredentialStore.Delete(); LoginStatus.Text = "未登录 · 有效期未知"; }
        catch (Exception ex) { LoginStatus.Text = $"清除保存的登录信息失败：{ex.Message}"; }
    }

    private async void Parse_Click(object sender, RoutedEventArgs e) => await ParseVideoAsync(Input.Text);
    private void Input_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        if (!string.Equals(parsingInput, Input.Text, StringComparison.Ordinal)) Parse_Click(sender, e);
    }
    internal async Task ParseVideoAsync(string inputText, int? selectedPage = null)
    {
        var version = ++requestVersion;
        parsingInput = inputText;
        try
        {
            Status.Text = "正在解析…"; TitleText.Text = ""; ResultsPanel.Visibility = Visibility.Collapsed; EmptyState.Visibility = Visibility.Visible; video = null; media = null; Pages.ItemsSource = null; Streams.ItemsSource = null; Qualities.ItemsSource = null; Codecs.ItemsSource = null; AudioStreams.ItemsSource = null; sizeCancellation?.Cancel(); SelectedSize.Text = ""; DownloadButton.IsEnabled = false; BatchButton.IsEnabled = false; CollectionButton.IsEnabled = false;
            var input = await api.ResolveVideoLinkAsync(inputText);
            if (version != requestVersion) return;
            if (BiliApi.TryParseCollectionLink(input, out var mid, out var collectionId)) { parsingInput = null; await OpenCollectionLinkAsync(mid, collectionId); return; }
            var isCourse = BiliApi.TryParseCourse(input, out var courseKind, out var courseId);
            var isPgc = BiliApi.TryParsePgc(input, out var pgcKind, out var pgcId);
            var result = isCourse ? await api.GetCourseAsync(input) : isPgc ? await api.GetPgcAsync(input) : await api.GetVideoAsync(input);
            if (version != requestVersion) return;
            video = result; lastParsedInput = input; parsingInput = null; TitleText.Text = $"{result.Title} · {result.Owner}"; ResultsPanel.Visibility = Visibility.Visible; EmptyState.Visibility = Visibility.Collapsed;
            Pages.ItemsSource = result.Pages;
            var selectedIndex = isCourse && courseKind == "ep" ? result.Pages.ToList().FindIndex(p => p.EpisodeId == courseId) : isPgc && pgcKind == "ep" ? result.Pages.ToList().FindIndex(p => p.EpisodeId == pgcId) : isPgc || isCourse ? 0 : BiliApi.RequestedPage(input, result.Pages.Count);
            Pages.SelectedIndex = selectedPage is >= 0 && selectedPage < result.Pages.Count ? selectedPage.Value : Math.Max(0, selectedIndex); UpdateDownloadAvailability();
        }
        catch (Exception ex) { if (version == requestVersion) { parsingInput = null; Status.Text = $"解析失败：{ex.Message}"; } }
    }

    private async Task OpenCollectionLinkAsync(long mid, long collectionId)
    {
        if (loadingCollection) return;
        var version = ++requestVersion;
        loadingCollection = true; UpdateDownloadAvailability();
        try
        {
            Status.Text = "正在读取合集…";
            var collection = await api.GetCollectionAsync(mid, collectionId);
            if (version != requestVersion) return;
            if (collection.Count == 0) { Status.Text = "合集没有可访问的视频。"; return; }
            var first = await api.GetVideoAsync(collection[0].Bvid);
            if (version != requestVersion) return;
            video = first; lastParsedInput = first.Bvid; TitleText.Text = $"{first.Title} · {first.Owner}"; ResultsPanel.Visibility = Visibility.Visible; EmptyState.Visibility = Visibility.Collapsed;
            Pages.ItemsSource = first.Pages; Pages.SelectedIndex = 0; UpdateDownloadAvailability();
            Status.Text = $"合集共 {collection.Count} 个视频";
            new BatchWindow(api, first, collection, null, null).Show();
        }
        catch (Exception ex) { if (version == requestVersion) Status.Text = "读取合集失败：" + ex.Message; }
        finally { loadingCollection = false; UpdateDownloadAvailability(); }
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        var window = new SearchWindow(api);
        window.VideoSelected += bvid => { if (!mainClosed) { Input.Text = bvid; Parse_Click(sender, e); } };
        window.Show();
    }

    private void Uploader_Click(object sender, RoutedEventArgs e) => new UploaderWindow(api, video?.OwnerId).Show();
    private void Favorites_Click(object sender, RoutedEventArgs e)
    {
        var window = new BackupWindow(api, backupStore, RunBackups);
        if (backupStore is not null && !ownsBackupMutex) window.Title += "（由另一个窗口执行）";
        window.Show();
    }
    private void Live_Click(object sender, RoutedEventArgs e)
    {
        if (liveManager is null) { Status.Text = "直播录制不可用，请检查录制目录或是否已有另一个实例。"; return; }
        WindowCoordinator.ShowLive(liveManager);
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (liveStopping && !liveCloseAllowed) { e.Cancel = true; return; }
        var explicitExit = exitRequested || liveCloseAllowed; exitRequested = false;
        if (!explicitExit && allowBackground) { e.Cancel = true; EnterBackground(); return; }
        try
        {
            var hasBatch = Application.Current.Windows.OfType<BatchWindow>().Any(w => w.IsRunning);
            if (!downloadCloseGuard.CanClose(Math.Max(MediaDownload.ActiveCount, DownloadQueue.Shared.HasActive || hasBatch ? 1 : 0), () => MessageBox.Show(Localizer.Translate("仍有下载任务正在进行。退出会中断这些任务，并保留已有的临时文件。是否退出？"), Localizer.Translate("退出程序"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)) { e.Cancel = true; return; }
        }
        catch (Exception ex) { e.Cancel = true; downloadCloseGuard.Reset(); Status.Text = "退出失败：" + ex.Message; return; }
        if (liveCloseAllowed || liveManager is null) return;
        bool recording;
        try { recording = liveManager.Rooms().Any(r => r.Recording); }
        catch (Exception ex) { e.Cancel = true; downloadCloseGuard.Reset(); Status.Text = "停止录制失败：" + ex.Message; return; }
        if (!recording) return;
        e.Cancel = true;
        liveStopping = true;
        try
        {
            liveManager.StopAll(); Status.Text = "正在结束直播录制…";
            for (var i = 0; i < 150 && liveManager.Rooms().Any(r => r.Recording); i++) await Task.Delay(200);
            if (liveManager.Rooms().Any(r => r.Recording) && MessageBox.Show(Localizer.Translate("录制仍在结束中。强制退出可能导致录制文件不完整，是否退出？"), Localizer.Translate("直播录制"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { downloadCloseGuard.Reset(); return; }
            liveCloseAllowed = true; Close();
        }
        catch (Exception ex) { downloadCloseGuard.Reset(); Status.Text = "停止录制失败：" + ex.Message; }
        finally { liveStopping = false; }
    }

    private async void RunBackups()
    {
        if (backupEngine is null || backupCancellation.IsCancellationRequested) return;
        try
        {
            if (!ownsBackupMutex && backupMutex is not null)
            {
                try { ownsBackupMutex = backupMutex.WaitOne(0); }
                catch (AbandonedMutexException) { ownsBackupMutex = true; }
            }
            if (!ownsBackupMutex) return;
            await Task.WhenAll(backupEngine.RunDueAsync(backupCancellation.Token), keywordEngine?.RunDueAsync(backupCancellation.Token) ?? Task.CompletedTask);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!backupCancellation.IsCancellationRequested) Status.Text = "自动备份失败：" + ex.Message; }
    }

    private void Keywords_Click(object sender, RoutedEventArgs e)
    {
        if (backupStore is null) { Status.Text = "关键词自动下载不可用。"; return; }
        ShowKeywordWindow(backupStore, RunBackups, ownsBackupMutex);
    }
    internal void ShowKeywordWindow(BackupStore store, Action trigger, bool isExecutor)
    {
        if (keywordWindow is not null)
        {
            if (keywordWindow.WindowState == WindowState.Minimized) keywordWindow.WindowState = WindowState.Normal;
            keywordWindow.Activate(); return;
        }
        var window = new KeywordWindow(store, trigger) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        if (!isExecutor) window.Title += "（由另一个窗口执行）";
        keywordWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(keywordWindow, window)) keywordWindow = null; };
        try { window.Show(); } catch { keywordWindow = null; throw; }
    }

    private void Batch_Click(object sender, RoutedEventArgs e)
    {
        if (video is null) return;
        new BatchWindow(api, video, null, Streams.SelectedItem as VideoStream, AudioStreams.SelectedItem as AudioStream).Show();
    }

    private async void Collection_Click(object sender, RoutedEventArgs e)
    {
        if (video?.OwnerId is not long mid || video.CollectionId is not long collectionId || loadingCollection) return;
        var captured = video;
        var preferredVideo = Streams.SelectedItem as VideoStream;
        var preferredAudio = AudioStreams.SelectedItem as AudioStream;
        loadingCollection = true; UpdateDownloadAvailability();
        try
        {
            Status.Text = "正在读取合集…";
            var collection = await api.GetCollectionAsync(mid, collectionId, new Progress<int>(count => Status.Text = $"已读取 {count} 个合集视频…"));
            if (collection.Count == 0) { Status.Text = "合集没有可访问的视频。"; return; }
            Status.Text = $"合集共 {collection.Count} 个视频";
            new BatchWindow(api, captured, collection, preferredVideo, preferredAudio).Show();
        }
        catch (Exception ex) { Status.Text = "读取合集失败：" + ex.Message; }
        finally { loadingCollection = false; UpdateDownloadAvailability(); }
    }

    private async void Pages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (video is null || Pages.SelectedItem is not VideoPage page) return;
        var version = ++requestVersion;
        try
        {
            Streams.ItemsSource = null; Qualities.ItemsSource = null; Codecs.ItemsSource = null; AudioStreams.ItemsSource = null; media = null; UpdateDownloadAvailability(); Status.Text = "正在获取可用画质…";
            var streams = await api.GetStreamsForPageAsync(video.Bvid, page);
            if (version != requestVersion) return;
            media = streams; Streams.ItemsSource = streams.Video; Qualities.ItemsSource = streams.Video.Select(v => v.Quality).Distinct().Select(q => new QualityChoice(q)).ToArray(); Qualities.SelectedIndex = streams.Video.Count > 0 ? 0 : -1;
            AudioStreams.ItemsSource = streams.Audio; AudioStreams.SelectedIndex = streams.Audio.Count > 0 ? 0 : -1;
            UpdateDownloadAvailability();
            Status.Text = streams.IsPreview ? "当前账号仅能获取预览内容。" : streams.Video.Count > 0 || streams.Audio.Count > 0 ? "已获取可用流" : "未获取到可用的音视频流";
        }
        catch (Exception ex) { if (version == requestVersion) { UpdateDownloadAvailability(); Status.Text = $"获取画质失败：{ex.Message}"; } }
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (video is null) return;
        var mode = DownloadMode.SelectedIndex;
        var selected = mode is 0 or 1 ? Streams.SelectedItem as VideoStream : null;
        var chosenAudio = mode is 0 or 2 ? AudioStreams.SelectedItem as AudioStream : null;
        var page = Pages.SelectedItem as VideoPage;
        var bvid = video.Bvid; var sessionVersion = api.SessionVersion; var allowPreview = media?.IsPreview == true; var ua = api.UserAgent;
        var cover = mode == 3 ? video.CoverUrl : null;
        if (cover is not null && Uri.TryCreate(cover, UriKind.Absolute, out var coverUri) && coverUri.Scheme == "http") cover = new UriBuilder(coverUri) { Scheme = "https", Port = -1 }.Uri.AbsoluteUri;
        var coverExtension = cover is not null && Uri.TryCreate(cover, UriKind.Absolute, out var parsed) ? Path.GetExtension(parsed.AbsolutePath).ToLowerInvariant() : "";
        if (coverExtension is not (".jpg" or ".jpeg" or ".png" or ".webp")) coverExtension = ".jpg";
        var filter = mode == 3 ? $"图片|*{coverExtension}" : mode == 2 ? "M4A 音频|*.m4a|Matroska|*.mkv" : "MP4 视频|*.mp4|Matroska|*.mkv";
        var preferMatroska = chosenAudio is not null && BatchPolicy.NeedsMatroska(chosenAudio);
        var suggestedName = mode == 2 ? DownloadNaming.AudioBaseName(video, page) : DownloadNaming.BaseName(video, page, Math.Max(1, Pages.SelectedIndex + 1), selected, chosenAudio);
        try
        {
            var processing = mode is 0 or 1 ? (VideoProcessing)Math.Max(0, VideoProcess.SelectedIndex) : VideoProcessing.Copy;
            if (cover is null && page is null) throw new InvalidOperationException("请选择视频分 P。");
            var audioMetadata = mode == 2 ? AudioOptions.Options.ForVideo(video, page) : null;
            var root = AppSettings.LoadDownloadDirectory();
            var automatic = AppSettings.LoadUseDefaultVideoDirectory();
            if (!Directory.Exists(root))
            {
                if (automatic) throw new DirectoryNotFoundException("默认视频下载目录不存在，请在设置中重新选择。");
                root = AppSettings.IndependentDownloadDirectory();
            }
            var initialDirectory = DownloadNaming.Folder(root, video);
            Directory.CreateDirectory(initialDirectory);
            var extension = mode == 3 ? coverExtension : preferMatroska ? ".mkv" : mode == 2 ? ".m4a" : ".mp4";
            var path = ChooseDownloadPath(automatic, initialDirectory, suggestedName, extension, filter, preferMatroska, DownloadQueue.Shared, dialog => dialog.ShowDialog(this));
            if (path is null) return;
            if (File.Exists(path)) throw new IOException("目标文件已存在，请选择其他文件名。");
            EnqueueManualDownload(DownloadQueue.Shared, Path.GetFileName(path), path, async (control, progress) =>
            {
                if (cover is not null) await MediaDownload.DownloadAsync(null, null, cover, path, ua, progress, processing: processing, control: control);
                else await MediaDownload.DownloadFreshAsync(api, bvid, page!, selected, chosenAudio, allowPreview, sessionVersion, path, ua, progress, processing, control, audioMetadata);
                return path;
            });
            Status.Text = Localizer.Translate("已添加下载任务");
        }
        catch (Exception ex) { Status.Text = $"下载失败：{ex.Message}"; }
    }
    internal static string? ChooseDownloadPath(bool automatic, string directory, string name, string extension, string filter, bool preferMatroska, DownloadQueue queue, Func<SaveFileDialog, bool?> showDialog)
    {
        if (automatic) return AvailableDownloadPath(directory, name, extension, queue);
        var dialog = new SaveFileDialog { Filter = filter, FilterIndex = preferMatroska ? 2 : 1, FileName = name, InitialDirectory = directory, AddExtension = true, OverwritePrompt = false };
        return showDialog(dialog) == true ? dialog.FileName : null;
    }
    internal static string AvailableDownloadPath(string directory, string name, string extension, DownloadQueue queue)
    {
        var reserved = queue.Items.Where(x => x.State is DownloadState.Waiting or DownloadState.Running or DownloadState.Paused or DownloadState.Canceling)
            .Select(x => Path.GetFullPath(x.Destination)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var number = 1; ; number++)
        {
            var candidate = Path.GetFullPath(Path.Combine(directory, name + (number == 1 ? "" : $" ({number})") + extension));
            if (!File.Exists(candidate) && !Directory.Exists(candidate) && !reserved.Contains(candidate)) return candidate;
        }
    }
    internal static DownloadEntry EnqueueManualDownload(DownloadQueue queue, string title, string path, Func<TransferControl, IProgress<string>, Task<string?>> work)
    {
        var entry = queue.Enqueue(title, path, work);
        WindowCoordinator.ShowDownloads(queue);
        return entry;
    }
}
