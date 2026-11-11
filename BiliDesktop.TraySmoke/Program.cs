using System.Windows;
using System.Windows.Threading;
using System.Net.Http;
using System.IO.Pipes;
using BiliDesktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 3 && args[0] is "--elevation-server" or "--elevation-client")
        {
            var role = args[0]; var identity = args[1]; var directory = args[2];
            var elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            try
            {
                using var instance = new SingleInstance(identity);
                if (role == "--elevation-server")
                {
                    if (!instance.IsPrimary) throw new InvalidOperationException("Server did not acquire the mutex.");
                    instance.StartListening(() => System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "signal.txt"), "received"));
                    System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "ready.txt"), $"elevated={elevated}");
                    var end = DateTime.UtcNow.AddMinutes(2);
                    while (DateTime.UtcNow < end && !System.IO.File.Exists(System.IO.Path.Combine(directory, "stop.txt"))) Thread.Sleep(200);
                }
                else
                {
                    var notified = !instance.IsPrimary && instance.NotifyPrimary(TimeSpan.FromSeconds(5));
                    System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "client.txt"), $"elevated={elevated};primary={instance.IsPrimary};notified={notified}");
                }
            }
            catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(directory, role == "--elevation-server" ? "server-error.txt" : "client.txt"), $"elevated={elevated};error={ex.GetType().Name}: {ex.Message}"); }
            return;
        }
        if (args.Length == 2 && args[0] == "--instance-probe")
        {
            using var probe = new SingleInstance(args[1]);
            Environment.ExitCode = !probe.IsPrimary && probe.NotifyPrimary(TimeSpan.FromSeconds(5)) ? 0 : 2;
            return;
        }
        if (args.Length == 2 && args[0] == "--instance-hold")
        {
            using var holder = new SingleInstance(args[1]);
            Console.WriteLine(holder.IsPrimary ? "READY" : "FAILED"); Console.Out.Flush();
            if (holder.IsPrimary) Thread.Sleep(Timeout.Infinite);
            return;
        }
        TestSingleInstanceProcesses();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources["StatusConverter"] = new LocalizedStatusConverter();
        Localizer.Apply("en");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        if (args.Contains("--preview-main") || args.Contains("--preview-results"))
        {
            var previewRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bili-layout-preview-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(previewRoot); AppSettings.FileNameOverride = System.IO.Path.Combine(previewRoot, "settings.json");
            Localizer.Apply(args.Contains("--preview-compact") ? "en" : "zh-CN");
            var preview = new MainWindow(new BiliApi(new MainWindowApiFixture()), false);
            if (args.Contains("--preview-compact")) { preview.Width = 780; preview.Height = 520; }
            if (args.Contains("--preview-results")) preview.Loaded += async (_, _) => await preview.ParseVideoAsync("BV1xx411c7mD");
            app.ShutdownMode = ShutdownMode.OnMainWindowClose; app.MainWindow = preview; app.Run(preview); return;
        }
        var uaApi = new BiliApi();
        foreach (var preset in UserAgentPresets.All)
        {
            if (preset.Value is null) continue;
            uaApi.SetUserAgent(preset.Value);
            if (uaApi.UserAgent != preset.Value) throw new Exception("User-Agent preset was changed or rejected");
        }
        if (new[] { "Windows", "Android", "iOS", "iPadOS", "macOS", "Linux" }.Any(platform => !UserAgentPresets.All.Any(preset => preset.Name.StartsWith(platform, StringComparison.Ordinal)))) throw new Exception("A platform preset is missing");
        if (!UserAgentPresets.All.Any(preset => preset.Name.Contains("Apple Silicon", StringComparison.Ordinal) && preset.Value is not null) || UserAgentPresets.All.Last().Name != "Custom" || UserAgentPresets.All.Last().Value is not null) throw new Exception("Apple Silicon or custom preset is missing");
        Console.WriteLine("User-Agent presets for desktop and mobile platforms passed validation.");
        var liveRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bili-live-ui-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(liveRoot);
        try
        {
            AppSettings.FileNameOverride = System.IO.Path.Combine(liveRoot, "app-settings.json");
            System.IO.File.WriteAllText(AppSettings.FileNameOverride, """{"userAgent":"legacy-agent","connections":4,"cacheMiB":32,"language":"en"}""");
            AppSettings.SaveDownloadDirectory(liveRoot); AppSettings.SaveLanguage("en");
            if (AppSettings.LoadUserAgent() != "legacy-agent" || AppSettings.LoadTransferOptions() != new TransferOptions(4, 32)) throw new Exception("Adding default directory discarded existing settings");
            TestStartupAndCloseToTray(liveRoot);
            TestUpdateWindows(args.Contains("--render-update"));
            var apiFixture = new MainWindowApiFixture(); var fixtureApi = new BiliApi(apiFixture);
            var mainWindow = new MainWindow(fixtureApi, false) { ShowInTaskbar = false, ShowActivated = false };
            if (mainWindow.FindName("Connections") is not null || ((System.Windows.FrameworkElement)mainWindow.FindName("ResultsPanel")).Visibility != Visibility.Collapsed || ((System.Windows.Controls.Expander)mainWindow.FindName("AccountExpander")).IsExpanded || !Equals(((System.Windows.Controls.Button)mainWindow.FindName("LiveButton")).Content, Localizer.Translate("直播录制"))) throw new Exception("Main window initially exposes settings, results or an incorrectly named live action");
            if (System.Windows.Controls.Grid.GetRow((System.Windows.FrameworkElement)mainWindow.FindName("Navigation")) == System.Windows.Controls.Grid.GetRow((System.Windows.FrameworkElement)mainWindow.FindName("ParseBar"))) throw new Exception("Navigation and video parsing were not separated");
            var audioOptions = (AudioOptionsControl)mainWindow.FindName("AudioOptions");
            var downloadMode = (System.Windows.Controls.ComboBox)mainWindow.FindName("DownloadMode");
            if (audioOptions.Visibility != Visibility.Collapsed) throw new Exception("Audio metadata options should be hidden for video");
            downloadMode.SelectedIndex = 2;
            if (audioOptions.Visibility != Visibility.Visible || audioOptions.Options.Enabled) throw new Exception("Audio metadata options did not appear with safe defaults");
            ((System.Windows.Controls.CheckBox)audioOptions.FindName("Cover")).IsChecked = true;
            ((System.Windows.Controls.CheckBox)audioOptions.FindName("Artist")).IsChecked = true;
            ((System.Windows.Controls.CheckBox)audioOptions.FindName("Title")).IsChecked = true;
            ((System.Windows.Controls.CheckBox)audioOptions.FindName("Album")).IsChecked = true;
            if (audioOptions.Options != new AudioMetadataOptions(true, true, true, true)) throw new Exception("Individual audio metadata controls were not captured");
            downloadMode.SelectedIndex = 0;
            if (audioOptions.Visibility != Visibility.Collapsed) throw new Exception("Audio metadata options remained visible for video");
            var settingsWindow = mainWindow.ShowSettings() ?? throw new Exception("Settings not shown");
            if (!ReferenceEquals(settingsWindow, mainWindow.ShowSettings()) || !settingsWindow.Topmost || settingsWindow.ResizeMode != ResizeMode.NoResize || mainWindow.IsEnabled) throw new Exception("Settings window was duplicated or blocks other windows");
            if (!ReferenceEquals(settingsWindow, mainWindow.ShowSettings())) throw new Exception("Settings window duplicated");
            var language = (System.Windows.Controls.ComboBox)settingsWindow.FindName("LanguageSelector");
            language.SelectedIndex = 1; Pump();
            if (AppSettings.LoadLanguage() != "en") throw new Exception("Language applied before confirmation");
            if (!settingsWindow.ApplySettings()) throw new Exception("Settings apply failed");
            if (AppSettings.LoadLanguage() != "zh-TW" || !Equals(((System.Windows.Controls.Button)mainWindow.FindName("LiveButton")).Content, Localizer.Translate("直播录制"))) throw new Exception("Settings did not update language across windows");
            language.SelectedIndex = 2; if (!settingsWindow.ApplySettings()) throw new Exception("Settings apply failed"); Pump();
            var maxTasks = (System.Windows.Controls.TextBox)settingsWindow.FindName("MaxTasks");
            var unlimitedTasks = (System.Windows.Controls.CheckBox)settingsWindow.FindName("UnlimitedTasks");
            if (maxTasks.Text != "4" || unlimitedTasks.IsChecked == true) throw new Exception("Legacy settings did not use the original concurrency limit");
            maxTasks.Text = "7"; settingsWindow.ApplySettings();
            if (AppSettings.LoadTransferOptions().MaxConcurrentTasks != 7 || MediaDownload.CurrentOptions.MaxConcurrentTasks != 7) throw new Exception("Custom concurrency was not applied and saved");
            unlimitedTasks.IsChecked = true; settingsWindow.ApplySettings();
            if (maxTasks.IsEnabled || AppSettings.LoadTransferOptions().MaxConcurrentTasks != 0 || MediaDownload.CurrentOptions.MaxConcurrentTasks != 0) throw new Exception("Unlimited concurrency was not applied and saved");
            unlimitedTasks.IsChecked = false; maxTasks.Text = "0"; settingsWindow.ApplySettings();
            if (MediaDownload.CurrentOptions.MaxConcurrentTasks != 0 || AppSettings.LoadTransferOptions().MaxConcurrentTasks != 0) throw new Exception("Invalid limit overwrote settings");
            maxTasks.Text = "4"; settingsWindow.ApplySettings();
            PumpUntil(mainWindow.ParseVideoAsync("BV1xx411c7mD"));
            if (((System.Windows.FrameworkElement)mainWindow.FindName("ResultsPanel")).Visibility != Visibility.Visible || ((System.Windows.FrameworkElement)mainWindow.FindName("EmptyState")).Visibility != Visibility.Collapsed) throw new Exception("Parsing did not reveal the video controls");
            var pages = (System.Windows.Controls.ComboBox)mainWindow.FindName("Pages");
            var qualities = (System.Windows.Controls.ComboBox)mainWindow.FindName("Qualities");
            var codecs = (System.Windows.Controls.ComboBox)mainWindow.FindName("Codecs");
            PumpUntil(WaitForAsync(() => qualities.Items.Count == 2 && codecs.Items.Count == 2));
            pages.SelectedIndex = 1;
            PumpUntil(WaitForAsync(() => apiFixture.StreamRequests >= 2 && codecs.Items.Count == 2));
            ((System.Windows.Controls.TextBox)settingsWindow.FindName("UserAgentBox")).Text = "BiliDesktop-UI-Fixture/2.0";
            settingsWindow.ApplySettings();
            PumpUntil(WaitForAsync(() => apiFixture.ViewRequests >= 2 && apiFixture.StreamRequests >= 3 && pages.SelectedIndex == 1 && codecs.Items.Count == 2));
            if (apiFixture.LastViewUserAgent != "BiliDesktop-UI-Fixture/2.0" || AppSettings.LoadUserAgent() != "BiliDesktop-UI-Fixture/2.0" || AppSettings.LoadDownloadDirectory() != liveRoot) throw new Exception("UA reparse, selected part or download directory persistence failed");
            settingsWindow.Close(); Pump();
            var reopenedSettings = mainWindow.ShowSettings() ?? throw new Exception("Settings not reopened");
            if (ReferenceEquals(settingsWindow, reopenedSettings) || ((System.Windows.Controls.TextBox)reopenedSettings.FindName("UserAgentBox")).Text != "BiliDesktop-UI-Fixture/2.0" || ((System.Windows.Controls.TextBox)reopenedSettings.FindName("DownloadDirectoryBox")).Text != liveRoot) throw new Exception("Settings did not reopen with the saved values");
            ((System.Windows.Controls.CheckBox)reopenedSettings.FindName("UseDefaultVideoDirectory")).IsChecked = false;
            if (!reopenedSettings.ApplySettings() || AppSettings.LoadUseDefaultVideoDirectory()) throw new Exception("Disabling the default folder did not apply");
            ((System.Windows.Controls.CheckBox)reopenedSettings.FindName("UseDefaultVideoDirectory")).IsChecked = true;
            reopenedSettings.Close(); Pump();
            if (AppSettings.LoadUseDefaultVideoDirectory() || !mainWindow.IsEnabled) throw new Exception("Cancel changed settings or failed to restore main window");
            var confirmSettings = mainWindow.ShowSettings() ?? throw new Exception("Settings not reopened");
            ((System.Windows.Controls.CheckBox)confirmSettings.FindName("UseDefaultVideoDirectory")).IsChecked = true;
            ((System.Windows.Controls.Button)confirmSettings.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
            if (!AppSettings.LoadUseDefaultVideoDirectory() || confirmSettings.IsVisible || !mainWindow.IsEnabled) throw new Exception("OK did not apply and close settings");
            var pathQueue = new DownloadQueue(System.IO.Path.Combine(liveRoot, "name-history.json"));
            var firstPath = System.IO.Path.Combine(liveRoot, "name.mp4"); System.IO.File.WriteAllText(firstPath, "existing");
            var reserved = pathQueue.Enqueue("reserved", System.IO.Path.Combine(liveRoot, "name (2).mp4"), async (control, _) => { await Task.Delay(Timeout.Infinite, control.Token); return null; });
            if (MainWindow.AvailableDownloadPath(liveRoot, "name", ".mp4", pathQueue) != System.IO.Path.Combine(liveRoot, "name (3).mp4")) throw new Exception("Automatic path collided with a file or active task");
            var dialogCalls = 0;
            var automaticPath = MainWindow.ChooseDownloadPath(true, liveRoot, "name", ".mp4", "MP4|*.mp4", false, pathQueue, _ => { dialogCalls++; return false; });
            if (dialogCalls != 0 || automaticPath != System.IO.Path.Combine(liveRoot, "name (3).mp4")) throw new Exception("Automatic download prompted for a path");
            var selectedPath = System.IO.Path.Combine(liveRoot, "chosen.mp4");
            var promptedPath = MainWindow.ChooseDownloadPath(false, liveRoot, "name", ".mp4", "MP4|*.mp4", false, pathQueue, dialog => { dialogCalls++; dialog.FileName = selectedPath; return true; });
            if (dialogCalls != 1 || promptedPath != selectedPath) throw new Exception("Manual download did not ask for a path");
            if (MainWindow.ChooseDownloadPath(false, liveRoot, "name", ".mp4", "MP4|*.mp4", false, pathQueue, _ => false) is not null) throw new Exception("Canceling the save dialog started a download");
            reserved.Cancel(); PumpUntil(reserved.Completion);
            AppSettings.SaveTransferOptions(new TransferOptions(4, 32));
            if (AppSettings.LoadDownloadDirectory() != liveRoot || AppSettings.LoadUserAgent() != "BiliDesktop-UI-Fixture/2.0") throw new Exception("Saving transfer settings discarded the UA or download directory");
            mainWindow.Show(); Pump();
            TestAboutWindow(mainWindow, args.Contains("--render-about"));
            TestIndependentMainWindows(mainWindow, apiFixture);
            TestSingletonDownloadWindow(liveRoot);
            TestModelessQr();
            PumpUntil(TestKeywordQueueAsync(liveRoot));
            PumpUntil(TestFavoriteBackupQueueAsync(liveRoot));
            PumpUntil(TestBatchMetadataCancellationAsync(liveRoot));
            PumpUntil(TestQueueStageAsync(liveRoot));
            PumpUntil(TestLiveActionGuardAsync(liveRoot));
            PumpUntil(TestBackupRuleRemovalAsync(liveRoot));
            TestSearchPreview(args.Contains("--render-search"));
            TestFavoritesAndBackup(liveRoot);
            var keywordStore = new BackupStore(System.IO.Path.Combine(liveRoot, "keyword-window.db"));
            mainWindow.ShowKeywordWindow(keywordStore, () => { }, true); Pump();
            var keywordWindow = Application.Current.Windows.OfType<KeywordWindow>().Single();
            ((System.Windows.Controls.TextBox)keywordWindow.FindName("DirectoryBox")).Text = liveRoot;
            var termInput = (System.Windows.Controls.TextBox)keywordWindow.FindName("Keyword");
            var addTerm = (System.Windows.Controls.Button)keywordWindow.FindName("AddTermButton");
            termInput.Text = "cat"; addTerm.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            termInput.Text = "music"; addTerm.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var intervalChoice = (System.Windows.Controls.ComboBox)keywordWindow.FindName("Interval");
            var matchChoice = (System.Windows.Controls.ComboBox)keywordWindow.FindName("MatchMode");
            var rateChoice = (System.Windows.Controls.ComboBox)keywordWindow.FindName("Rate");
            intervalChoice.SelectedIndex = 0; matchChoice.SelectedIndex = 1; rateChoice.SelectedIndex = 1;
            Localizer.Apply("zh-TW"); Pump();
            if (intervalChoice.SelectedIndex != 0 || matchChoice.SelectedIndex != 1 || rateChoice.SelectedIndex != 1) throw new Exception("Changing language reset keyword rule choices");
            Localizer.Apply("en"); Pump();
            ((System.Windows.Controls.Button)keywordWindow.FindName("AddRuleButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var uiRule = keywordStore.ListKeywords().Single();
            if (!uiRule.Keywords.SequenceEqual(["cat", "music"]) || uiRule.EffectiveIntervalSeconds != 30 || uiRule.MatchMode != KeywordMatchMode.All || uiRule.QueryDelayMs != 1500 || uiRule.Directory != liveRoot) throw new Exception("Keyword rule UI did not save its choices");
            var peerQueue = new DownloadQueue(System.IO.Path.Combine(liveRoot, "peer-history.json"));
            var peerTasks = new DownloadQueueWindow(peerQueue) { Owner = mainWindow, ShowInTaskbar = false, ShowActivated = false };
            peerTasks.Show(); Pump();
            if (keywordWindow.Topmost || keywordWindow.Owner is not null || !mainWindow.IsEnabled || !keywordWindow.IsEnabled || !peerTasks.IsEnabled || !IsWindowEnabled(new System.Windows.Interop.WindowInteropHelper(peerTasks).Handle)) throw new Exception("Keyword window blocks the task window or remains on top");
            mainWindow.ShowKeywordWindow(keywordStore, () => { }, true); Pump();
            if (Application.Current.Windows.OfType<KeywordWindow>().Count() != 1) throw new Exception("Repeated keyword action opened duplicate windows");
            keywordWindow.Activate(); Pump(); peerTasks.Activate(); Pump();
            if (!IsWindowEnabled(new System.Windows.Interop.WindowInteropHelper(keywordWindow).Handle) || !IsWindowEnabled(new System.Windows.Interop.WindowInteropHelper(peerTasks).Handle)) throw new Exception("Keyword and task windows cannot both be operated");
            keywordWindow.Close(); Pump();
            mainWindow.ShowKeywordWindow(keywordStore, () => { }, true); Pump();
            if (Application.Current.Windows.OfType<KeywordWindow>().Count() != 1) throw new Exception("Keyword window could not be reopened");
            peerTasks.Close(); Pump();
            Console.WriteLine("Keyword window is modeless and can coexist with the enabled task window.");
            var settingsOnClose = mainWindow.ShowSettings();
            mainWindow.Close(); Pump();
            if (Application.Current.Windows.OfType<SettingsWindow>().Contains(settingsOnClose)) throw new Exception("Closing the main window left the settings window open");
            if (Application.Current.Windows.OfType<KeywordWindow>().Any()) throw new Exception("Closing the main window left the keyword window open");
            var delayedFixture = new MainWindowApiFixture { BlockFirstView = true };
            var delayedWindow = new MainWindow(new BiliApi(delayedFixture), false) { ShowInTaskbar = false, ShowActivated = false };
            var earlierParse = delayedWindow.ParseVideoAsync("BV1xx411c7mD");
            PumpUntil(WaitForAsync(() => delayedFixture.ViewRequests == 1));
            var delayedSettings = delayedWindow.ShowSettings();
            ((System.Windows.Controls.TextBox)delayedSettings.FindName("UserAgentBox")).Text = "BiliDesktop-UI-Fixture/3.0";
            delayedSettings!.ApplySettings();
            PumpUntil(WaitForAsync(() => delayedFixture.ViewRequests >= 2 && delayedFixture.StreamRequests >= 1));
            delayedFixture.FirstViewRelease.TrySetResult(); PumpUntil(earlierParse);
            if (delayedFixture.LastViewUserAgent != "BiliDesktop-UI-Fixture/3.0" || ((System.Windows.Controls.ComboBox)delayedWindow.FindName("Pages")).Items.Count != 2) throw new Exception("UA change during parsing did not restart with the new UA");
            delayedWindow.Close(); Pump();
            Console.WriteLine("Separate resolution and codec selection, UA reparse, selected part and settings persistence passed.");
            var queueHistory = System.IO.Path.Combine(liveRoot, "queue-history.json");
            var queue = new DownloadQueue(queueHistory); var attempts = 0;
            var resultPath = System.IO.Path.Combine(liveRoot, "result.mp4");
            var testEntry = queue.Enqueue("fixture", resultPath, async (control, progress) =>
            {
                await Task.Yield();
                if (++attempts == 1) throw new System.IO.IOException("fixture failure");
                await System.IO.File.WriteAllTextAsync(resultPath, "fixture", control.Token);
                return resultPath;
            });
            PumpUntil(testEntry.Completion);
            if (testEntry.State != DownloadState.Failed) throw new Exception("Download queue failure was not displayed");
            testEntry.Retry(); PumpUntil(testEntry.Completion);
            if (testEntry.State != DownloadState.Completed || testEntry.Percent != 100 || new DownloadQueue(queueHistory).Items.Single().Destination != resultPath) throw new Exception("Download retry or completed history failed");
            var blocked = queue.Enqueue("blocked", resultPath + ".other", async (control, progress) => { await Task.Delay(Timeout.Infinite, control.Token); return null; });
            string? openedFile = null; string? openedFolder = null;
            var queueWindow = new DownloadQueueWindow(queue, () => true, path => openedFile = path, path => openedFolder = path) { ShowInTaskbar = false, ShowActivated = false };
            queueWindow.Show(); Pump();
            var localizedPause = (System.Windows.Controls.Button)queueWindow.FindName("PauseButton");
            Localizer.Apply("zh-TW"); Pump(); if (!Equals(localizedPause.Content, Localizer.Translate("暂停"))) throw new Exception("Open queue did not update its language");
            Localizer.Apply("en"); Pump();
            ((System.Windows.Controls.DataGrid)queueWindow.FindName("Tasks")).SelectedItem = testEntry; Pump();
            ((System.Windows.Controls.Button)queueWindow.FindName("OpenButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            ((System.Windows.Controls.Button)queueWindow.FindName("FolderButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (openedFile != resultPath || openedFolder != liveRoot) throw new Exception("Open file or folder used the wrong target");
            var nestedFolder = System.IO.Path.Combine(liveRoot, "目录 空格"); System.IO.Directory.CreateDirectory(nestedFolder);
            var nestedFile = System.IO.Path.Combine(nestedFolder, "视频 名称.mp4"); System.IO.File.WriteAllText(nestedFile, "fixture");
            if (DownloadQueueWindow.ContainingFolder(nestedFile) != nestedFolder || DownloadQueueWindow.ContainingFolder(System.IO.Path.Combine(nestedFolder, "not-yet-created.m4a")) != nestedFolder || DownloadQueueWindow.ContainingFolder(nestedFolder) != nestedFolder) throw new Exception("Containing folder resolution failed for Unicode, spaces or queued output");
            ((System.Windows.Controls.DataGrid)queueWindow.FindName("Tasks")).SelectedItem = blocked; Pump();
            var pauseButton = (System.Windows.Controls.Button)queueWindow.FindName("PauseButton");
            if (!pauseButton.IsEnabled) throw new Exception("Download queue pause button is disabled");
            pauseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (blocked.State != DownloadState.Paused) throw new Exception("Download queue pause failed");
            ((System.Windows.Controls.Button)queueWindow.FindName("ResumeButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (blocked.State != DownloadState.Waiting) throw new Exception("Resuming a queued task incorrectly displayed it as downloading");
            ((System.Windows.Controls.Button)queueWindow.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); PumpUntil(blocked.Completion);
            if (blocked.State != DownloadState.Canceled) throw new Exception("Download queue cancellation failed");
            queue.Remove(blocked);
            var removeOnly = queue.Enqueue("remove only", resultPath, (_, _) => Task.FromResult<string?>(resultPath));
            PumpUntil(removeOnly.Completion); queue.Remove(removeOnly);
            if (!System.IO.File.Exists(resultPath)) throw new Exception("Removing a history entry deleted its file");
            ((System.Windows.Controls.DataGrid)queueWindow.FindName("Tasks")).SelectedItem = testEntry; Pump();
            ((System.Windows.Controls.Button)queueWindow.FindName("DeleteButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (System.IO.File.Exists(resultPath) || queue.Items.Contains(testEntry)) throw new Exception("Delete did not remove the selected file and record");
            queueWindow.Close(); Pump();
            TestBatchQueueActions(liveRoot);
            System.IO.File.WriteAllText(resultPath, "fixture");
            var brokenHistory = System.IO.Path.Combine(liveRoot, "broken-history.json");
            System.IO.File.WriteAllText(brokenHistory, "not-json");
            var brokenQueue = new DownloadQueue(brokenHistory);
            var completedWithBrokenHistory = brokenQueue.Enqueue("fixture", resultPath, (_, _) => Task.FromResult<string?>(resultPath));
            PumpUntil(completedWithBrokenHistory.Completion);
            if (brokenQueue.HistoryWarning is null || System.IO.File.ReadAllText(brokenHistory) != "not-json") throw new Exception("Corrupt download history was overwritten or not reported");
            System.IO.File.WriteAllText(brokenHistory, "[null]");
            if (new DownloadQueue(brokenHistory).HistoryWarning is null || System.IO.File.ReadAllText(brokenHistory) != "[null]") throw new Exception("Invalid history record was not preserved");
            var batchVideoTask = fixtureApi.GetVideoAsync("BV1xx411c7mD"); PumpUntil(batchVideoTask);
            var batchVideo = batchVideoTask.Result;
            var partFolder = DownloadNaming.Folder(liveRoot, batchVideo); System.IO.Directory.CreateDirectory(partFolder);
            var selectedVideo = new VideoStream(80, "avc1", 200, "http://example.test/a", []);
            var selectedAudio = new AudioStream(30280, "aac", 20, "http://example.test/audio", []);
            var existingParts = batchVideo.Pages.Select((page, index) => System.IO.Path.Combine(partFolder, DownloadNaming.BaseName(batchVideo, page, index + 1, selectedVideo, selectedAudio) + ".mp4")).ToArray();
            foreach (var path in existingParts) System.IO.File.WriteAllText(path, "existing");
            var batchWindow = new BatchWindow(fixtureApi, null, [new CollectionVideo(batchVideo.Bvid, batchVideo.Title)], null, null, queue) { ShowInTaskbar = false, ShowActivated = false };
            batchWindow.Show(); Pump();
            var batchJobs = (System.Windows.Controls.DataGrid)batchWindow.FindName("Jobs");
            batchJobs.UpdateLayout(); batchJobs.CurrentCell = new System.Windows.Controls.DataGridCellInfo(batchJobs.Items[0], batchJobs.Columns[0]);
            if (!batchJobs.BeginEdit()) throw new Exception("Could not begin editing a batch checkbox");
            ((System.Windows.Controls.Button)batchWindow.FindName("SelectNone")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (batchJobs.Items.Cast<BatchJob>().Any(job => job.Selected)) throw new Exception("Select none failed while a checkbox was being edited");
            ((System.Windows.Controls.Button)batchWindow.FindName("SelectAll")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (batchJobs.Items.Cast<BatchJob>().Any(job => !job.Selected)) throw new Exception("Select all failed after checkbox editing");
            var batchMode = (System.Windows.Controls.ComboBox)batchWindow.FindName("Mode");
            var batchAudio = (AudioOptionsControl)batchWindow.FindName("AudioOptions");
            batchMode.SelectedIndex = 2;
            if (batchAudio.Visibility != Visibility.Visible || batchAudio.Options.Enabled) throw new Exception("Batch audio metadata options were not available");
            ((System.Windows.Controls.CheckBox)batchAudio.FindName("Album")).IsChecked = true;
            if (!batchAudio.Options.Album) throw new Exception("Batch part album option did not apply");
            batchMode.SelectedIndex = 0;
            ((System.Windows.Controls.Button)batchWindow.FindName("Start")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PumpUntil(WaitForAsync(() => queue.Items.Count == 2 && queue.Items.All(x => x.State == DownloadState.Skipped) && ((System.Windows.Controls.Button)batchWindow.FindName("Start")).IsEnabled));
            var autoTasksWindow = Application.Current.Windows.OfType<DownloadQueueWindow>().Single();
            if (!ReferenceEquals(((System.Windows.Controls.DataGrid)autoTasksWindow.FindName("Tasks")).ItemsSource, queue.Items)) throw new Exception("Batch download did not automatically display its task queue");
            autoTasksWindow.WindowState = WindowState.Minimized;
            ((System.Windows.Controls.Button)batchWindow.FindName("QueueButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
            if (Application.Current.Windows.OfType<DownloadQueueWindow>().Count() != 1 || autoTasksWindow.WindowState == WindowState.Minimized) throw new Exception("Batch queue button did not reuse and restore the task window");
            if (existingParts.Any(path => System.IO.File.ReadAllText(path) != "existing")) throw new Exception("Batch queue overwrote an existing part");
            batchWindow.Close(); autoTasksWindow.Close(); Pump();
            foreach (var skipped in queue.Items.ToArray()) queue.Remove(skipped);
            Console.WriteLine("Batch collection expands parts into separate queue entries and preserves existing files.");
            Console.WriteLine("Download queue pause, cancel, retry, history and safe removal passed.");
            var gateway = new QueuedBackupGateway(new BiliApi(), queue);
            var failedAutomatic = gateway.InDownloadSlotAsync(() => Task.FromException(new System.IO.IOException("automatic fixture")), CancellationToken.None, () => true);
            try { PumpUntil(failedAutomatic); throw new Exception("Automatic gateway hid an error"); }
            catch (System.IO.IOException ex) when (ex.Message == "automatic fixture") { }
            if (queue.Items[0].State != DownloadState.Failed) throw new Exception("Automatic task failure was not listed");
            queue.Remove(queue.Items[0]);
            Console.WriteLine("Automatic download queue integration propagates failures.");
            if (args.Contains("--account-queue"))
            {
                var integration = TestAutomaticQueueAsync(queue, liveRoot);
                PumpUntil(integration, TimeSpan.FromSeconds(50));
                Console.WriteLine("Authenticated automatic gateway, queue, output and completed history passed.");
            }
            using var liveManager = new LiveManager("", liveRoot);
            liveManager.SetDirectory(liveRoot);
            var firstRoomId = liveManager.AddRoom("2147483647"); liveManager.ToggleAuto(firstRoomId);
            var secondRoomId = liveManager.AddRoom("2147483646"); liveManager.ToggleAuto(secondRoomId);
            var liveSingleton = WindowCoordinator.ShowLive(liveManager); Pump();
            liveSingleton.WindowState = WindowState.Minimized;
            if (!ReferenceEquals(liveSingleton, WindowCoordinator.ShowLive(liveManager)) || liveSingleton.WindowState == WindowState.Minimized || liveSingleton.Topmost || Application.Current.Windows.OfType<LiveWindow>().Count() != 1) throw new Exception("Live window was not reused and restored");
            liveSingleton.Close(); Pump();
            var reopenedLive = WindowCoordinator.ShowLive(liveManager); Pump();
            if (ReferenceEquals(liveSingleton, reopenedLive) || Application.Current.Windows.OfType<LiveWindow>().Count() != 1) throw new Exception("Closed live window was not recreated");
            reopenedLive.Close(); Pump();
            string? openedLiveFolder = null;
            var liveWindow = new LiveWindow(liveManager, path => openedLiveFolder = path) { ShowInTaskbar = false, ShowActivated = false };
            liveWindow.Show(); Pump();
            typeof(LiveWindow).GetMethod("Events_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(liveWindow, [liveWindow, new RoutedEventArgs()]); Pump();
            var eventsWindow = Application.Current.Windows.OfType<LiveEventsWindow>().Single();
            if (eventsWindow.Owner is not null || eventsWindow.Topmost || !liveWindow.IsEnabled || !eventsWindow.IsEnabled) throw new Exception("Live records window blocks live controls");
            eventsWindow.Close(); Pump();
            var roomGrid = (System.Windows.Controls.DataGrid)liveWindow.FindName("Rooms");
            if (roomGrid.Columns.OfType<System.Windows.Controls.DataGridCheckBoxColumn>().Any()) throw new Exception("Live status still uses checkboxes");
            ((System.Windows.Controls.Button)liveWindow.FindName("OpenDirectoryButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (openedLiveFolder != liveManager.DirectoryPath) throw new Exception("Open recording folder used the wrong directory");
            var firstRow = (LiveRoomRow)roomGrid.Items[0]; var secondRow = (LiveRoomRow)roomGrid.Items[1];
            var oldRoomState = liveManager.Rooms().First(room => room.Id == firstRoomId);
            firstRow.Update(oldRoomState with { Name = "Example streamer", Title = "Example live stream", Streaming = true, Recording = true, NetworkMibps = 2.5, DurationRatio = 1.23 });
            if (firstRow.LiveStatus != "Live" || firstRow.RecordingStatus != "Recording" || firstRow.BandwidthText != "2.50 Mib/s" || firstRow.RatioText != "123%") throw new Exception("Live status or real-time metrics formatting failed");
            if (args.Contains("--render-live"))
            {
                Pump();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)liveWindow.ActualWidth, (int)liveWindow.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(liveWindow);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var image = System.IO.File.Create(System.IO.Path.Combine(Environment.CurrentDirectory, "BiliDesktop", "artifacts", "live-window-review.png")); encoder.Save(image);
            }
            firstRow.Update(oldRoomState with { Recording = true, NetworkMibps = double.NaN, DurationRatio = null });
            if (firstRow.BandwidthText != "—" || firstRow.RatioText != "—") throw new Exception("Unknown live metrics were shown as real values");
            firstRow.Update(oldRoomState);
            roomGrid.SelectedItem = firstRow; Pump();
            var firstCell = ((System.Windows.Controls.DataGridTemplateColumn)roomGrid.Columns[0]).GetCellContent(firstRow);
            var firstCheck = FindVisual<System.Windows.Controls.CheckBox>(firstCell) ?? throw new Exception("Room selection checkbox not visible");
            firstCheck.IsChecked = true; secondRow.IsChecked = true;
            firstRow.Update(oldRoomState with { Streaming = true, Recording = true, NetworkMibps = 3.2, DurationRatio = 0.98 });
            if (!ReferenceEquals(roomGrid.SelectedItem, firstRow) || !firstRow.IsChecked || firstRow.RatioText != "98%") throw new Exception("Live metric update disrupted the selected room");
            var autoButton = (System.Windows.Controls.Button)liveWindow.FindName("AutoButton");
            autoButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (liveManager.Rooms().Any(room => !room.AutoRecord)) throw new Exception("Checked live rooms did not receive the action");
            autoButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (liveManager.Rooms().Any(room => room.AutoRecord)) throw new Exception("Second checked-room action did not toggle back");
            PumpUntil(Task.Delay(2300));
            if (!ReferenceEquals(roomGrid.SelectedItem, firstRow) || !ReferenceEquals(roomGrid.Items[0], firstRow) || !firstRow.IsChecked || !secondRow.IsChecked) throw new Exception("Live room selection or checkbox was lost during refresh");
            var selectedIds = (Guid[])typeof(LiveWindow).GetMethod("SelectedIds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(liveWindow, null)!;
            if (!selectedIds.ToHashSet().SetEquals([firstRoomId, secondRoomId])) throw new Exception("Checked live rooms were not selected for actions");
            var mode = (System.Windows.Controls.ComboBox)liveWindow.FindName("ModeBox");
            var modeDescription = (System.Windows.Controls.TextBlock)liveWindow.FindName("ModeDescription");
            if (!modeDescription.Text.StartsWith("Standard:", StringComparison.Ordinal)) throw new Exception("Standard recording description not displayed");
            var priority = (System.Windows.Controls.TextBox)liveWindow.FindName("PriorityBox");
            var codec = (System.Windows.Controls.ComboBox)liveWindow.FindName("CodecBox");
            mode.SelectedIndex = 1; codec.SelectedIndex = 1;
            if (!modeDescription.Text.StartsWith("Raw data:", StringComparison.Ordinal)) throw new Exception("Raw recording description not updated");
            if (!priority.Text.StartsWith("hevc30000,avc30000", StringComparison.Ordinal)) throw new Exception("Codec preset did not update priority");
            ((System.Windows.Controls.Button)liveWindow.FindName("SaveOptionsButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (liveManager.Options.Mode != BililiveRecorder.Core.Config.RecordMode.RawData || !liveManager.Options.Quality.StartsWith("hevc30000", StringComparison.Ordinal)) throw new Exception("Live settings UI did not apply choices");
            liveWindow.Close(); Pump();
            liveManager.SetOptions(liveManager.Options with { Quality = "hevc400,avc150" });
            var customWindow = new LiveWindow(liveManager) { ShowInTaskbar = false, ShowActivated = false };
            customWindow.Show(); Pump();
            var customQuality = (System.Windows.Controls.ComboBox)customWindow.FindName("QualityBox");
            var customCodec = (System.Windows.Controls.ComboBox)customWindow.FindName("CodecBox");
            var customPriority = (System.Windows.Controls.TextBox)customWindow.FindName("PriorityBox");
            if (customQuality.SelectedIndex != -1 || customCodec.SelectedIndex != -1 || customPriority.Text != "hevc400,avc150") throw new Exception("Custom priority was misrepresented as a preset");
            customQuality.SelectedIndex = 2; customCodec.SelectedIndex = 1;
            if (!customPriority.Text.StartsWith("hevc20000,avc20000", StringComparison.Ordinal)) throw new Exception("Custom priority could not be replaced by a preset");
            customWindow.Close(); Pump();
            Console.WriteLine("Live status text, bandwidth and rate, folder action, stable selection and multi-room actions passed.");
        }
        finally { AppSettings.FileNameOverride = null; System.IO.Directory.Delete(liveRoot, true); }
        var guard = new DownloadCloseGuard(); var prompts = 0; var confirm = false; var guardClosed = false;
        if (!guard.CanClose(0, () => throw new Exception("Idle close unexpectedly prompted"))) throw new Exception("Idle close was rejected.");
        var guardedWindow = new Window { Width = 100, Height = 100, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.ToolWindow };
        guardedWindow.Closing += (_, e) => e.Cancel = !guard.CanClose(1, () => { prompts++; return confirm; });
        guardedWindow.Closed += (_, _) => guardClosed = true;
        guardedWindow.Show(); guardedWindow.Close();
        if (guardClosed || !guardedWindow.IsVisible || prompts != 1) throw new Exception("Declining an active-download close did not keep the window open.");
        confirm = true; guardedWindow.Close();
        if (!guardClosed || prompts != 2 || !guard.CanClose(1, () => throw new Exception("Previously approved close prompted again"))) throw new Exception("Confirmed close was not honored.");
        guard.Reset();
        if (guard.CanClose(1, () => false)) throw new Exception("Reset did not restore active-download protection.");
        try { guard.CanClose(1, () => throw new InvalidOperationException("fixture")); throw new Exception("Failed confirmation was accepted."); }
        catch (InvalidOperationException ex) when (ex.Message == "fixture") { }
        if (guard.CanClose(1, () => false)) throw new Exception("Failed confirmation left the guard approved.");
        Console.WriteLine("Idle close, declined and confirmed active-download close, and guard reset passed.");
        var window = new Window { Width = 100, Height = 100, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.ToolWindow };
        var backgroundPeer = new Window { Width = 100, Height = 100, ShowInTaskbar = false, ShowActivated = false };
        var closed = false; window.Closed += (_, _) => closed = true;
        using var background = new BackgroundWindowController(window, value => value);
        try
        {
            window.Show(); backgroundPeer.Show();
            background.Enter();
            if (window.IsVisible || backgroundPeer.IsVisible || !background.IsActive) throw new Exception("Windows were not hidden with a visible tray icon.");
            var ticked = false;
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += (_, _) => { ticked = true; timer.Stop(); frame.Continue = false; };
            timer.Start();
            using var timeout = new CancellationTokenSource();
            _ = Task.Delay(TimeSpan.FromSeconds(2), timeout.Token).ContinueWith(_ => window.Dispatcher.BeginInvoke(() => frame.Continue = false), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            timeout.Cancel(); timer.Stop();
            if (!ticked) throw new Exception("Dispatcher timer did not run while the window was hidden.");
            background.RequestOpen(); Pump();
            if (!window.IsVisible || !backgroundPeer.IsVisible || background.IsActive || closed) throw new Exception("Tray restore did not restore windows and release the icon.");
            background.Enter();
            if (window.IsVisible || !background.IsActive) throw new Exception("Second background entry failed.");
            background.RequestExit(); Pump();
            if (!closed || background.IsActive) throw new Exception("Tray exit did not close the window and release the icon.");
            Console.WriteLine("Background hide, tray activation, hidden dispatcher timer, restore, repeat entry, and exit lifecycle passed.");
        }
        finally { background.Dispose(); backgroundPeer.Close(); if (!closed) window.Close(); app.Shutdown(); }
    }
    private static System.Diagnostics.Process StartInstanceProbe(string mode, string identity)
    {
        var executable = Environment.ProcessPath ?? throw new Exception("Missing process path");
        var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (System.IO.Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add(mode); start.ArgumentList.Add(identity);
        return System.Diagnostics.Process.Start(start) ?? throw new Exception("Could not launch instance test");
    }
    private static void TestSingleInstanceProcesses()
    {
        var identity = "BiliDesktop-test-" + Guid.NewGuid().ToString("N");
        var activations = 0;
        using (var primary = new SingleInstance(identity))
        {
            if (!primary.IsPrimary) throw new Exception("First instance could not acquire lock");
            primary.StartListening(() => Interlocked.Increment(ref activations));
            var pipe = (NamedPipeServerStream?)typeof(SingleInstance).GetField("server", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(primary) ?? throw new Exception("Activation pipe not created");
            using (var windowsIdentity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                var acl = pipe.GetAccessControl();
                var rules = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.AuthorizationRule>().ToArray();
                if (windowsIdentity.User is null || rules.Length != 1 || !windowsIdentity.User.Equals(rules[0].IdentityReference)) throw new Exception("Activation pipe grants access beyond the current Windows account");
            }
            using var secondary = StartInstanceProbe("--instance-probe", identity);
            if (!secondary.WaitForExit(10000)) { secondary.Kill(); throw new Exception("Second instance did not exit"); }
            if (secondary.ExitCode != 0 || activations != 1) throw new Exception("Second instance did not notify the primary");
        }
        using (var afterExit = new SingleInstance(identity)) if (!afterExit.IsPrimary) throw new Exception("Lock not released on normal exit");
        using (var holder = StartInstanceProbe("--instance-hold", identity))
        {
            var ready = holder.StandardOutput.ReadLineAsync();
            if (!ready.Wait(TimeSpan.FromSeconds(10)) || ready.Result != "READY") { holder.Kill(); throw new Exception("Lock holder did not start"); }
            using (var blocked = new SingleInstance(identity))
                if (blocked.IsPrimary || blocked.NotifyPrimary(TimeSpan.FromMilliseconds(150))) throw new Exception("A nonresponsive primary did not block a second instance");
            holder.Kill(); holder.WaitForExit();
        }
        using (var afterCrash = new SingleInstance(identity)) if (!afterCrash.IsPrimary) throw new Exception("Lock not recovered after process termination");
        Console.WriteLine("Cross-process single-instance notification, normal exit and crash recovery passed.");
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void PumpUntil(Task task, TimeSpan? maxWait = null)
    {
        var frame = new DispatcherFrame();
        using var timeout = new CancellationTokenSource(maxWait ?? TimeSpan.FromSeconds(15));
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var registration = timeout.Token.Register(() => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        if (!task.IsCompleted) throw new TimeoutException("UI smoke task timed out");
        task.GetAwaiter().GetResult();
    }
    private static async Task TestAutomaticQueueAsync(DownloadQueue queue, string root)
    {
        var credential = CredentialStore.Load() ?? throw new InvalidOperationException("Saved account session not available.");
        var api = new BiliApi(); api.SetCookie(credential);
        var video = await api.GetVideoAsync("BV1xx411c7mD");
        var streams = await api.GetStreamsForPageAsync(video.Bvid, video.Pages[0]);
        var selectedVideo = streams.Video.OrderBy(v => v.Bandwidth).First();
        var selectedAudio = streams.Audio.OrderBy(a => a.Bandwidth).First();
        var path = System.IO.Path.Combine(root, "automatic.mp4");
        var gateway = new QueuedBackupGateway(api, queue);
        await gateway.InDownloadSlotAsync(() => gateway.DownloadWithinSlotAsync(selectedVideo, selectedAudio, path, CancellationToken.None, () => true), CancellationToken.None, () => true);
        var item = queue.Items.FirstOrDefault(x => x.Destination == path);
        if (item?.State != DownloadState.Completed || !System.IO.File.Exists(path) || new System.IO.FileInfo(path).Length == 0) throw new Exception("Automatic queued output missing");
        queue.Remove(item);
    }
    private static void TestIndependentMainWindows(MainWindow main, MainWindowApiFixture fixture)
    {
        void Click(string method) => typeof(MainWindow).GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(main, [main, new RoutedEventArgs()]);
        Click("Search_Click"); Click("DownloadQueue_Click"); Click("Favorites_Click"); Click("Uploader_Click"); Click("Qr_Click"); Pump();
        var search = Application.Current.Windows.OfType<SearchWindow>().Single();
        var peers = new Window[] { search, Application.Current.Windows.OfType<DownloadQueueWindow>().Single(), Application.Current.Windows.OfType<BackupWindow>().Single(), Application.Current.Windows.OfType<UploaderWindow>().Single(), Application.Current.Windows.OfType<QrWindow>().Single() };
        if (!main.IsEnabled || peers.Any(w => w.Owner is not null || w.Topmost || !w.IsEnabled || !IsWindowEnabled(new System.Windows.Interop.WindowInteropHelper(w).Handle))) throw new Exception("Application windows are modal or always above their parent");
        ((System.Windows.Controls.TextBox)search.FindName("Keyword")).Text = "cat";
        ((System.Windows.Controls.Button)search.FindName("SearchButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var results = (System.Windows.Controls.ListBox)search.FindName("Results"); PumpUntil(WaitForAsync(() => results.Items.Count == 1));
        results.SelectedIndex = 0; var before = fixture.ViewRequests;
        typeof(SearchWindow).GetMethod("Open_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(search, [search, new RoutedEventArgs()]);
        PumpUntil(WaitForAsync(() => fixture.ViewRequests > before && ((System.Windows.Controls.TextBox)main.FindName("Input")).Text == "BV1xx411c7mD"));
        foreach (var peer in peers.Where(w => !ReferenceEquals(w, search))) peer.Close(); Pump();
        Console.WriteLine("Application windows are independent, and modeless search still selects and parses a video.");
    }
    private static void TestAboutWindow(MainWindow main, bool render)
    {
        var button = (System.Windows.Controls.Button)main.FindName("AboutButton");
        var location = button.TranslatePoint(new Point(0, 0), main);
        if (location.X < main.ActualWidth / 2 || location.Y < main.ActualHeight / 2) throw new Exception("About entry is not at the bottom right");
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
        var fromMain = Application.Current.Windows.OfType<AboutWindow>().Single();
        if (!fromMain.Topmost || fromMain.ResizeMode != ResizeMode.NoResize || main.IsEnabled) throw new Exception("About window did not stay on top and block its peers");
        main.ActivateFromAnotherInstance(); Pump();
        if (main.IsEnabled || !fromMain.IsVisible) throw new Exception("Activation bypassed the modal About window");
        var laterWindow = new Window { ShowInTaskbar = false, ShowActivated = false }; laterWindow.Show(); Pump();
        PumpUntil(WaitForAsync(() => !laterWindow.IsEnabled));
        var aboutHandle = new System.Windows.Interop.WindowInteropHelper(fromMain).Handle;
        if ((GetWindowLong(aboutHandle, -16) & 0x00030000) != 0 || (GetWindowLong(aboutHandle, -20) & 0x00000008) == 0) throw new Exception("Native About window has minimize/maximize buttons or is not topmost");
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
        if (Application.Current.Windows.OfType<AboutWindow>().Count() != 1) throw new Exception("Repeated About click opened duplicate windows");
        fromMain.Close(); Pump();
        if (!main.IsEnabled || !laterWindow.IsEnabled) throw new Exception("Closing About did not restore other windows");
        laterWindow.Close(); Pump();
        var opened = new List<Uri>(); var about = new AboutWindow(uri => opened.Add(uri)) { ShowInTaskbar = false, ShowActivated = false };
        about.Show(); Pump();
        var links = new Dictionary<string, string>
        {
            ["DeveloperLink"] = "https://github.com/LycheeMiku/BiliDesktop",
            ["RecorderLink"] = "https://github.com/BililiveRecorder/BililiveRecorder",
            ["DownkyiLink"] = "https://github.com/yaobiao131/downkyi",
            ["DownkyiAlternativeLink"] = "https://github.com/leiurayer/downkyi",
            ["BilibiliDownLink"] = "https://github.com/QuanXplor/BilibiliDown",
            ["BilidownWebLink"] = "https://github.com/firstack-it/bilidown-web",
            ["ApiLink"] = "https://github.com/rinnein/bilibili-API-collect"
        };
        foreach (var (name, url) in links)
        {
            var link = (System.Windows.Documents.Hyperlink)about.FindName(name);
            if (link.NavigateUri.AbsoluteUri != url) throw new Exception("Incorrect About repository link");
            link.RaiseEvent(new System.Windows.Navigation.RequestNavigateEventArgs(link.NavigateUri, null) { RoutedEvent = System.Windows.Documents.Hyperlink.RequestNavigateEvent });
        }
        if (opened.Count != links.Count || ((System.Windows.Controls.TextBlock)about.FindName("VersionValue")).Text.Trim() != AppVersion.Current) throw new Exception("About repository links or version failed");
        foreach (var language in new[] { "zh-CN", "zh-TW", "en" })
        {
            Localizer.Apply(language); Pump();
            if (about.Title != Localizer.Translate("关于") || ((System.Windows.Controls.TextBlock)about.FindName("ReferencesLabel")).Text != Localizer.Translate("参考项目")) throw new Exception("About labels did not localize");
            if (render && language == "zh-CN")
            {
                about.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)about.ActualWidth, (int)about.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(about);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = System.IO.File.Create(System.IO.Path.Combine(Environment.CurrentDirectory, "BiliDesktop", "artifacts", "about-window-review.png")); encoder.Save(file);
            }
        }
        about.Close(); Pump();
        var failure = new AboutWindow(_ => throw new InvalidOperationException("fixture")) { ShowInTaskbar = false, ShowActivated = false }; failure.Show(); Pump();
        var failedLink = (System.Windows.Documents.Hyperlink)failure.FindName("DeveloperLink");
        failedLink.RaiseEvent(new System.Windows.Navigation.RequestNavigateEventArgs(failedLink.NavigateUri, null) { RoutedEvent = System.Windows.Documents.Hyperlink.RequestNavigateEvent });
        if (string.IsNullOrWhiteSpace(((System.Windows.Controls.TextBlock)failure.FindName("LinkStatus")).Text)) throw new Exception("About link failure was not shown");
        failure.Close(); Pump();
        Console.WriteLine("About entry, topmost window, version, localized labels and repository links passed.");
    }
    private static void TestUpdateWindows(bool render)
    {
        var release = new AvailableRelease("beta5", new DateTimeOffset(2026, 10, 1, 10, 30, 0, TimeSpan.Zero), "更新一\n- 更新二");
        var opened = new List<Uri>();
        var githubDialog = new UpdateWindow(release, null, uri => opened.Add(uri)) { ShowInTaskbar = false, ShowActivated = false };
        githubDialog.Show(); Pump();
        if (!((System.Windows.Controls.TextBlock)githubDialog.FindName("CurrentVersion")).Text.Contains(AppVersion.Current, StringComparison.Ordinal) || !((System.Windows.Controls.TextBlock)githubDialog.FindName("NewVersion")).Text.Contains(release.Version, StringComparison.Ordinal) || !((System.Windows.Controls.TextBlock)githubDialog.FindName("Published")).Text.Contains(release.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), StringComparison.Ordinal) || ((System.Windows.Controls.TextBox)githubDialog.FindName("Notes")).Text != release.Notes) throw new Exception("Update dialog omitted version, date or release notes");
        if (render)
        {
            Localizer.Apply("zh-CN"); Pump(); githubDialog.UpdateLayout();
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)githubDialog.ActualWidth, (int)githubDialog.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); image.Render(githubDialog);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var file = System.IO.File.Create(System.IO.Path.Combine(Environment.CurrentDirectory, "BiliDesktop", "artifacts", "update-window-review.png")); encoder.Save(file);
            Localizer.Apply("en"); Pump();
        }
        Localizer.Apply("zh-TW"); Pump();
        if (githubDialog.Title != Localizer.Translate("发现新版本") || !((System.Windows.Controls.TextBlock)githubDialog.FindName("CurrentVersion")).Text.Contains(Localizer.Translate("当前版本"), StringComparison.Ordinal)) throw new Exception("Update dialog did not localize");
        Localizer.Apply("en"); Pump();
        ((System.Windows.Controls.Button)githubDialog.FindName("GithubButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
        if (opened.Single() != release.GithubUrl || githubDialog.IsVisible) throw new Exception("GitHub Release button did not open the correct URL");
        var alternateDialog = new UpdateWindow(release, null, uri => opened.Add(uri)) { ShowInTaskbar = false, ShowActivated = false };
        alternateDialog.Show(); Pump();
        ((System.Windows.Controls.Button)alternateDialog.FindName("AlternateButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
        if (opened.Last() != AvailableRelease.AlternateUrl || alternateDialog.IsVisible) throw new Exception("Alternative download button did not open the correct URL");
        var handler = new UpdateUiFixture();
        using var client = new HttpClient(handler);
        var checker = new UpdateChecker(client, TimeSpan.FromSeconds(1));
        var window = new MainWindow(new BiliApi(new MainWindowApiFixture()), false, updateChecker: checker, startUpdateChecks: true) { ShowInTaskbar = false, ShowActivated = false };
        window.Show(); Pump();
        var indicator = (System.Windows.Controls.Button)window.FindName("UpdateAvailable");
        PumpUntil(WaitForAsync(() => handler.Calls >= 1 && indicator.Visibility == Visibility.Visible));
        var about = (System.Windows.Controls.Button)window.FindName("AboutButton");
        if (indicator.TranslatePoint(new Point(0, 0), window).X >= about.TranslatePoint(new Point(0, 0), window).X) throw new Exception("Update indicator is not beside About");
        var popup = Application.Current.Windows.OfType<UpdateWindow>().Single();
        var timer = (DispatcherTimer)typeof(MainWindow).GetField("updateTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
        if (timer.Interval != TimeSpan.FromHours(2) || !timer.IsEnabled) throw new Exception("Update schedule is not every two hours");
        timer.Interval = TimeSpan.FromMilliseconds(70); PumpUntil(WaitForAsync(() => handler.Calls >= 2)); timer.Interval = TimeSpan.FromHours(2);
        if (Application.Current.Windows.OfType<UpdateWindow>().Count() != 1) throw new Exception("Repeated check opened duplicate update dialogs");
        ((System.Windows.Controls.Button)popup.FindName("LaterButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
        if (popup.IsVisible || indicator.Visibility != Visibility.Visible) throw new Exception("Dismissing update did not preserve the indicator");
        indicator.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
        if (Application.Current.Windows.OfType<UpdateWindow>().Count() != 1) throw new Exception("Update indicator did not reopen release details");
        handler.Fail = true; PumpUntil(window.CheckUpdatesAsync("Europe/Berlin"));
        var failure = Application.Current.Windows.OfType<UpdateWindow>().Single();
        if (failure.Title != Localizer.Translate("更新检查失败") || !((System.Windows.Controls.TextBox)failure.FindName("Notes")).Text.Contains("failed", StringComparison.OrdinalIgnoreCase) || ((System.Windows.Controls.Button)failure.FindName("GithubButton")).Visibility != Visibility.Collapsed || indicator.Visibility != Visibility.Visible) throw new Exception("Update failure was not reported or erased a known update");
        handler.Fail = false; handler.Empty = true; PumpUntil(window.CheckUpdatesAsync("Europe/Berlin")); Pump();
        if (indicator.Visibility != Visibility.Collapsed || Application.Current.Windows.OfType<UpdateWindow>().Any()) throw new Exception("An empty release list was not treated as successful without updates");
        window.Close(); Pump();
        Console.WriteLine("Startup and periodic update checks, indicator, release prompt, both browser targets and failure UI passed.");
    }
    private static void TestStartupAndCloseToTray(string root)
    {
        var current = System.IO.Path.Combine(root, "current", "BiliDesktop.exe");
        var old = System.IO.Path.Combine(root, "previous", "BiliDesktop.exe");
        var startup = new UiStartupStore { Value = StartupRegistration.CommandFor(old) };
        var window = new MainWindow(new BiliApi(new MainWindowApiFixture()), false, startup, current) { ShowInTaskbar = false, ShowActivated = false };
        var settings = window.ShowSettings() ?? throw new Exception("Settings not shown");
        var atLogin = (System.Windows.Controls.CheckBox)settings.FindName("StartAtLogin");
        if (!atLogin.IsEnabled || atLogin.IsChecked == true || !Equals(startup.Value, StartupRegistration.CommandFor(old))) throw new Exception("Older startup entry was not safely made selectable");
        atLogin.IsChecked = true;
        if (StartupRegistration.GetState(startup, current) == StartupState.Enabled) throw new Exception("Startup changed before apply");
        if (!settings.ApplySettings()) throw new Exception("Startup apply failed");
        if (StartupRegistration.GetState(startup, current) != StartupState.Enabled) throw new Exception("Startup checkbox could not switch to current version");
        atLogin.IsChecked = false; if (!settings.ApplySettings()) throw new Exception("Startup apply failed");
        if (startup.Value is not null) throw new Exception("Startup checkbox could not disable current version");
        var background = (System.Windows.Controls.CheckBox)settings.FindName("AllowBackground");
        if (background.IsChecked == true || AppSettings.LoadAllowBackground()) throw new Exception("Background should default to off");
        background.IsChecked = true; if (AppSettings.LoadAllowBackground()) throw new Exception("Background changed before apply");
        if (!settings.ApplySettings()) throw new Exception("Background apply failed");
        settings.Close(); Pump();
        AppSettings.SaveTransferOptions(new TransferOptions(4, 32));
        AppSettings.SaveUserAgent("legacy-agent"); AppSettings.SaveLanguage("en"); AppSettings.SaveDownloadDirectory(root);
        if (!AppSettings.LoadAllowBackground() || AppSettings.LoadUserAgent() != "legacy-agent" || AppSettings.LoadDownloadDirectory() != root) throw new Exception("Background setting was not persisted alongside existing settings");
        var closed = false; window.Closed += (_, _) => closed = true;
        var peer = new Window { ShowInTaskbar = false, ShowActivated = false };
        window.Show(); peer.Show(); Pump();
        window.Close(); Pump();
        var controller = (BackgroundWindowController?)typeof(MainWindow).GetField("background", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window);
        if (closed || window.IsVisible || peer.IsVisible || settings.IsVisible || controller?.IsActive != true) throw new Exception("Closing with background enabled did not hide windows to the tray");
        var trayMenu = (System.Windows.Forms.ContextMenuStrip?)typeof(BackgroundWindowController).GetField("menu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(controller) ?? throw new Exception("Tray menu missing");
        var aboutMenu = trayMenu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().SingleOrDefault(item => item.Text == Localizer.Translate("关于")) ?? throw new Exception("Tray About menu item missing");
        aboutMenu.PerformClick(); Pump();
        var trayAbout = Application.Current.Windows.OfType<AboutWindow>().Single();
        if (!trayAbout.IsVisible || !trayAbout.Topmost || trayAbout.ResizeMode != ResizeMode.NoResize || window.IsVisible) throw new Exception("Tray About did not restore the existing topmost window independently");
        aboutMenu.PerformClick(); Pump();
        if (Application.Current.Windows.OfType<AboutWindow>().Count() != 1) throw new Exception("Tray About opened duplicate windows");
        if (window.IsEnabled || peer.IsEnabled) throw new Exception("Tray About did not block other windows");
        trayAbout.Close(); Pump();
        window.ActivateFromAnotherInstance(); Pump();
        if (!window.IsVisible || !peer.IsVisible || !window.IsEnabled || controller.IsActive) throw new Exception("Restoring the app after About closed failed");
        window.Close(); Pump();
        trayMenu = (System.Windows.Forms.ContextMenuStrip?)typeof(BackgroundWindowController).GetField("menu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(controller) ?? throw new Exception("Tray menu missing after re-entry");
        aboutMenu = trayMenu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Single(item => item.Text == Localizer.Translate("关于"));
        aboutMenu.PerformClick(); Pump();
        var reopenedAbout = Application.Current.Windows.OfType<AboutWindow>().Single();
        if (ReferenceEquals(trayAbout, reopenedAbout) || !reopenedAbout.IsVisible || !reopenedAbout.Topmost) throw new Exception("Tray About did not reopen after closing");
        reopenedAbout.Close(); Pump();
        controller.RequestOpen(); Pump();
        if (!window.IsVisible || !peer.IsVisible || controller.IsActive) throw new Exception("Tray did not restore windows after close");
        window.Close(); Pump();
        if (closed || controller.IsActive != true) throw new Exception("Second close did not hide to the tray");
        controller.RequestExit(); Pump();
        if (!closed || controller.IsActive) throw new Exception("Tray exit did not bypass close-to-tray");
        peer.Close(); Pump();
        AppSettings.SaveAllowBackground(false);
        var ordinary = new MainWindow(new BiliApi(new MainWindowApiFixture()), false) { ShowInTaskbar = false, ShowActivated = false };
        var ordinaryClosed = false; ordinary.Closed += (_, _) => ordinaryClosed = true; ordinary.Show(); ordinary.Close(); Pump();
        if (!ordinaryClosed) throw new Exception("Closing with background disabled did not exit normally");
        Console.WriteLine("Startup version switch, saved background toggle, close-to-tray, restore and explicit exit passed.");
    }
    private static void TestModelessQr()
    {
        var fixture = new QrUiFixture(); var qr = new QrWindow(new BiliApi(fixture)) { ShowInTaskbar = false, ShowActivated = false };
        string? result = null; DateTimeOffset? expiry = null; var closed = false;
        qr.LoginCompleted += (cookie, expires) => { result = cookie; expiry = expires; };
        qr.Closed += (_, _) => closed = true;
        qr.Show(); PumpUntil(WaitForAsync(() => fixture.Polling));
        if (qr.Owner is not null || qr.Topmost || !qr.IsEnabled) throw new Exception("QR window is not independent");
        fixture.Release.TrySetResult(); PumpUntil(WaitForAsync(() => closed));
        if (result is null || !result.Contains("SESSDATA=", StringComparison.Ordinal) || !expiry.HasValue) throw new Exception("Modeless QR result was not delivered");
        Console.WriteLine("Modeless QR login returns its session and expiry.");
    }
    private static void TestSingletonDownloadWindow(string root)
    {
        var queue = new DownloadQueue(System.IO.Path.Combine(root, "single-window-history.json"));
        async Task<string?> WaitForCancel(TransferControl control, IProgress<string> _) { await Task.Delay(Timeout.Infinite, control.Token); return null; }
        var first = MainWindow.EnqueueManualDownload(queue, "first", root, WaitForCancel); Pump();
        var window = Application.Current.Windows.OfType<DownloadQueueWindow>().Single();
        if (!window.IsVisible || window.Topmost || !ReferenceEquals(((System.Windows.Controls.DataGrid)window.FindName("Tasks")).ItemsSource, queue.Items)) throw new Exception("Manual download did not open the task window");
        window.WindowState = WindowState.Minimized;
        var second = MainWindow.EnqueueManualDownload(queue, "second", root, WaitForCancel); Pump();
        if (Application.Current.Windows.OfType<DownloadQueueWindow>().Count() != 1 || !ReferenceEquals(window, WindowCoordinator.ShowDownloads(queue)) || window.WindowState == WindowState.Minimized || queue.Items.Count != 2) throw new Exception("Consecutive downloads did not reuse and restore the task window");
        first.Cancel(); second.Cancel(); PumpUntil(Task.WhenAll(first.Completion, second.Completion));
        window.Close(); Pump();
        var reopened = WindowCoordinator.ShowDownloads(queue); Pump();
        if (ReferenceEquals(window, reopened) || Application.Current.Windows.OfType<DownloadQueueWindow>().Count() != 1) throw new Exception("Closed task window was not recreated");
        reopened.Close(); Pump();
        Console.WriteLine("Manual downloads automatically open and reuse the single task window, including restore and reopen.");
    }
    private static async Task TestKeywordQueueAsync(string root)
    {
        var output = System.IO.Path.Combine(root, "keyword-queue-output"); System.IO.Directory.CreateDirectory(output);
        var store = new BackupStore(System.IO.Path.Combine(root, "keyword-queue-test.db"));
        store.AddKeyword("cat", DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds(), output, 5);
        var fixture = new MainWindowApiFixture(); var queue = new DownloadQueue(System.IO.Path.Combine(root, "keyword-queue-history.json"));
        var gateway = new QueuedBackupGateway(new BiliApi(fixture), queue);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaDownload.Configure(new(8, 16, 1));
        var blocker = MediaDownload.InSlotAsync(async () => { held.TrySetResult(); await release.Task; });
        Task? run = null;
        try
        {
            await held.Task;
            run = new KeywordEngine(store, gateway).RunDueAsync();
            await WaitForAsync(() => queue.Items.Count == 2);
            if (queue.Items.Any(x => x.State != DownloadState.Waiting || !x.Title.StartsWith("fixture · P", StringComparison.Ordinal)) || fixture.StreamRequests != 0 || run.IsCompleted) throw new Exception("Keyword matches were not all visible by title while waiting for a slot");
            queue.Items[0].Cancel();
            store.SetKeywordEnabled(store.ListKeywords().Single().Id, false);
            release.TrySetResult();
            await Task.WhenAll(run, blocker);
            if (fixture.StreamRequests != 0 || queue.Items.All(x => x.State != DownloadState.Canceled)) throw new Exception("Canceled or paused keyword tasks started unexpectedly");
            Console.WriteLine("Keyword matches appear in the download queue before slots are available, with titles and cancellation.");
        }
        finally { release.TrySetResult(); if (run is not null) await run; await blocker; MediaDownload.Configure(new(4, 32)); }
    }
    private static async Task TestFavoriteBackupQueueAsync(string root)
    {
        var output = System.IO.Path.Combine(root, "favorite-queue-output"); System.IO.Directory.CreateDirectory(output);
        var store = new BackupStore(System.IO.Path.Combine(root, "favorite-queue-test.db"));
        store.Add(123, new FavoriteFolder(456, "Created folder", 1), output, 5);
        var api = new BiliApi(new FavoritesUiFixture());
        var queue = new DownloadQueue(System.IO.Path.Combine(root, "favorite-queue-history.json"));
        var window = new DownloadQueueWindow(queue) { ShowInTaskbar = false, ShowActivated = false }; window.Show();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaDownload.Configure(new(8, 16, 1));
        var blocker = MediaDownload.InSlotAsync(async () => { held.TrySetResult(); await release.Task; });
        Task? run = null;
        try
        {
            await held.Task;
            run = new BackupEngine(store, new QueuedBackupGateway(api, queue)).RunDueAsync();
            await WaitForAsync(() => queue.Items.Count == 1);
            var grid = (System.Windows.Controls.DataGrid)window.FindName("Tasks");
            if (grid.Items.Count != 1 || queue.Items[0].State != DownloadState.Waiting || !queue.Items[0].Title.Contains("fixture", StringComparison.Ordinal) || queue.Items[0].BackupRuleId != store.List().Single().Id) throw new Exception("Favorite backup was not displayed or linked to its rule in the download queue");
            queue.Items[0].Cancel(); store.SetEnabled(store.List().Single().Id, false); release.TrySetResult();
            await Task.WhenAll(run, blocker);
            Console.WriteLine("Favorite backup entries appear in the download task window while waiting.");
        }
        finally { release.TrySetResult(); if (run is not null) await run; await blocker; window.Close(); MediaDownload.Configure(new(4, 32)); }
    }
    private static async Task TestBatchMetadataCancellationAsync(string root)
    {
        var summaryJobs = new[] { new BatchJob { Number = 1, Bvid = "BV0000000001", Label = "ok", Finished = true, Status = "完成" }, new BatchJob { Number = 2, Bvid = "BV0000000002", Label = "failed", Finished = true, HasError = true, Status = "完成 0，失败 1，跳过 0" }, new BatchJob { Number = 3, Bvid = "BV0000000003", Label = "stopped", Finished = true, Status = "已停止排队" } };
        if (BatchWindow.BatchSummary(summaryJobs) != string.Format(Localizer.Translate("完成 {0}，失败 {1}，已停止 {2}"), 1, 1, 1)) throw new Exception("Batch summary misreported failed or stopped jobs");
        var fixture = new BlockingBatchMetadataFixture();
        var queue = new DownloadQueue(System.IO.Path.Combine(root, "bounded-batch-history.json"));
        var videos = Enumerable.Range(0, 44).Select(i => new CollectionVideo("BV" + i.ToString("D10"), "fixture " + i)).ToArray();
        var batch = new BatchWindow(new BiliApi(fixture), null, videos, null, null, queue) { ShowInTaskbar = false, ShowActivated = false };
        batch.Show();
        ((System.Windows.Controls.Button)batch.FindName("Start")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await WaitForAsync(() => fixture.Active == 4);
        await Task.Delay(150);
        if (fixture.Maximum != 4 || fixture.Requests != 4 || !batch.IsRunning || queue.Items.Count != 0) throw new Exception("Batch metadata requests were not bounded before downloading");
        ((System.Windows.Controls.Button)batch.FindName("Stop")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await WaitForAsync(() => !batch.IsRunning);
        if (fixture.Canceled != 4 || queue.Items.Count != 0) throw new Exception("Stopping the batch did not cancel pending metadata requests");
        batch.Close();
        foreach (var taskWindow in Application.Current.Windows.OfType<DownloadQueueWindow>().ToArray()) taskWindow.Close();
        Console.WriteLine("Batch metadata concurrency is bounded and blocked reads cancel promptly.");
    }
    private static async Task TestQueueStageAsync(string root)
    {
        var queue = new DownloadQueue(System.IO.Path.Combine(root, "queue-stage-history.json"));
        var entry = queue.Enqueue("fixture", root, async (control, progress) => { progress.Report("等待准备 FFmpeg…"); await Task.Delay(Timeout.Infinite, control.Token); return null; });
        await WaitForAsync(() => entry.State == DownloadState.Running && entry.StateText == Localizer.Translate("等待准备 FFmpeg…"));
        entry.Pause(); entry.Resume();
        if (entry.State != DownloadState.Running) throw new Exception("Resuming an active task lost its running state");
        entry.Cancel(); await entry.Completion;
        if (entry.State != DownloadState.Canceled) throw new Exception("Canceling a dependency preparation task failed");
        Console.WriteLine("Download queue shows the current preparation stage and cancels it.");
    }
    private static async Task TestLiveActionGuardAsync(string root)
    {
        var stateDir = System.IO.Path.Combine(root, "live-action-guard");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshes = 0;
        BililiveRecorder.Core.IRoom? refreshedRoom = null;
        using var manager = new LiveManager("", stateDir, refreshRoom: async room =>
        {
            refreshedRoom = room; Interlocked.Increment(ref refreshes); entered.TrySetResult(); await release.Task;
            room.GetType().GetField("name", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(room, "fixture");
            room.GetType().GetField("streaming", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(room, true);
        });
        var id = manager.AddRoom("2147483645"); manager.ToggleAuto(id);
        var window = new LiveWindow(manager) { ShowInTaskbar = false, ShowActivated = false }; window.Show();
        var rooms = (System.Windows.Controls.DataGrid)window.FindName("Rooms"); rooms.SelectedItem = rooms.Items[0];
        var start = typeof(LiveWindow).GetMethod("Start_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        start.Invoke(window, [window, new RoutedEventArgs()]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (((System.Windows.Controls.Button)window.FindName("StartRoomButton")).IsEnabled || ((System.Windows.Controls.Button)window.FindName("AddRoomButton")).IsEnabled || !((System.Windows.Controls.Button)window.FindName("StopRoomButton")).IsEnabled) throw new Exception("Live controls did not preserve stop while preventing overlapping requests");
        start.Invoke(window, [window, new RoutedEventArgs()]);
        if (refreshes != 1) throw new Exception("Live window started overlapping room operations");
        ((System.Windows.Controls.Button)window.FindName("StopRoomButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        release.TrySetResult();
        await WaitForAsync(() => ((System.Windows.Controls.Button)window.FindName("StartRoomButton")).IsEnabled);
        if (refreshedRoom is null || refreshedRoom.AutoRecordForThisSession) throw new Exception("A pending start restarted recording after stop");
        if (((System.Windows.Controls.TextBlock)window.FindName("Status")).Text != "已请求停止录制") throw new Exception("A pending start replaced the user's stop result");
        window.Close();
        Console.WriteLine("Live room controls prevent overlapping operations and recover after an error.");
    }
    private static async Task TestBackupRuleRemovalAsync(string root)
    {
        var baseDir = System.IO.Path.Combine(root, "remove-backup"); System.IO.Directory.CreateDirectory(baseDir);
        var outsideDir = System.IO.Path.Combine(root, "outside-backup"); System.IO.Directory.CreateDirectory(outsideDir);
        var store = new BackupStore(System.IO.Path.Combine(root, "remove-backup.db"));
        var queue = new DownloadQueue(System.IO.Path.Combine(root, "remove-backup-history.json"));
        store.Add(123, new FavoriteFolder(3001, "keep", 1), baseDir, 5);
        var keepRule = store.List().Single();
        var keepFile = System.IO.Path.Combine(baseDir, "keep.mp4"); System.IO.File.WriteAllText(keepFile, "keep");
        store.MarkCompleted(keepRule.Id, "BV1xx411c7mD", 1, keepFile);
        var chosen = (BackupRemovalChoice?)null; var confirm = false; var chooseCalls = 0;
        var window = new BackupWindow(new BiliApi(new FavoritesUiFixture()), store, () => { }, queue, _ => confirm, _ => { chooseCalls++; return chosen; }) { ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        var dialog = new BackupRemovalDialog(window, 1);
        var dialogPanel = (System.Windows.Controls.StackPanel)dialog.Content;
        var dialogButtons = ((System.Windows.Controls.WrapPanel)dialogPanel.Children[2]).Children.OfType<System.Windows.Controls.Button>().ToArray();
        if (dialogButtons.Length != 3 || !Equals(dialogButtons[0].Content, Localizer.Translate("取消下载并保留文件")) || !Equals(dialogButtons[1].Content, Localizer.Translate("取消下载并删除文件")) || !dialogButtons[2].IsCancel) throw new Exception("Backup removal file choices were not clearly presented");
        var grid = (System.Windows.Controls.DataGrid)window.FindName("Rules");
        await WaitForAsync(() => grid.Items.Count == 1 && ((System.Windows.Controls.Button)window.FindName("RemoveRule")).IsEnabled);
        grid.SelectedItem = grid.Items[0];
        var button = (System.Windows.Controls.Button)window.FindName("RemoveRule");
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (chooseCalls != 0 || store.List().Count != 1) throw new Exception("Refusing the first deletion confirmation changed the rule");
        confirm = true;
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (chooseCalls != 1 || store.List().Count != 1 || !store.List().Single().Enabled) throw new Exception("Canceling the second deletion confirmation changed the rule");
        var active = queue.Enqueue("backup", baseDir, async (control, _) => { await Task.Delay(Timeout.Infinite, control.Token); return null; }, keepRule.Id);
        var unrelated = queue.Enqueue("unrelated", baseDir, async (control, _) => { await Task.Delay(Timeout.Infinite, control.Token); return null; });
        chosen = BackupRemovalChoice.KeepFiles;
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await WaitForAsync(() => store.List().Count == 0);
        await active.Completion;
        if (active.State != DownloadState.Canceled || active.CanRetry || !System.IO.File.Exists(keepFile) || unrelated.State == DownloadState.Canceled) throw new Exception("Rule removal did not cancel only its downloads and preserve files");
        active.Retry(); if (active.State != DownloadState.Canceled) throw new Exception("Deleted rule's download was allowed to restart");
        store.Add(123, new FavoriteFolder(3002, "delete", 1), baseDir, 5);
        var deleteRule = store.List().Single();
        var deleteFile = System.IO.Path.Combine(baseDir, "delete.mp4"); var untracked = System.IO.Path.Combine(baseDir, "untracked.mp4");
        System.IO.File.WriteAllText(deleteFile, "delete"); System.IO.File.WriteAllText(untracked, "keep");
        store.MarkCompleted(deleteRule.Id, "BV1xx411c7mD", 2, deleteFile); store.RecordFavoriteFile(123, 3002, "BV1xx411c7mD", 2, deleteFile);
        typeof(BackupWindow).GetMethod("RefreshRules", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null);
        grid.SelectedItem = grid.Items[0]; chosen = BackupRemovalChoice.DeleteFiles;
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await WaitForAsync(() => store.List().Count == 0);
        if (System.IO.File.Exists(deleteFile) || !System.IO.File.Exists(untracked) || !System.IO.File.Exists(keepFile) || store.FavoriteFile(123, 3002, "BV1xx411c7mD", 2) is not null || unrelated.State == DownloadState.Canceled) throw new Exception("Deleting rule files affected unrelated files or failed to clean its record");
        store.Add(123, new FavoriteFolder(3003, "unsafe", 1), baseDir, 5);
        var unsafeRule = store.List().Single(); var outsideFile = System.IO.Path.Combine(outsideDir, "outside.mp4"); System.IO.File.WriteAllText(outsideFile, "outside");
        store.MarkCompleted(unsafeRule.Id, "BV1xx411c7mD", 3, outsideFile);
        typeof(BackupWindow).GetMethod("RefreshRules", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null);
        grid.SelectedItem = grid.Items[0]; button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await WaitForAsync(() => !store.List().Single().Enabled);
        if (!System.IO.File.Exists(outsideFile) || store.List().Count != 1 || !((System.Windows.Controls.TextBlock)window.FindName("Status")).Text.Contains("outside.mp4")) throw new Exception("Unsafe recorded path was deleted or failure was not reported");
        store.Remove(unsafeRule.Id);
        store.Add(123, new FavoriteFolder(3004, "checking", 1), baseDir, 5);
        var checkingRule = store.List().Single();
        var checkingFile = System.IO.Path.Combine(baseDir, "checking.mp4"); System.IO.File.WriteAllText(checkingFile, "fixture");
        store.MarkCompleted(checkingRule.Id, "BV1xx411c7mD", 1, checkingFile);
        var checkingGateway = new DeferredBackupValidationGateway();
        var running = new BackupEngine(store, checkingGateway).RunDueAsync();
        await checkingGateway.CheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await BackupRuleRemoval.RemoveAsync(store, queue, [checkingRule], false);
        checkingGateway.CheckRelease.TrySetResult(true);
        await running;
        if (checkingGateway.DownloadAttempts != 0 || store.List().Count != 0 || !System.IO.File.Exists(checkingFile)) throw new Exception("Backup continued to enqueue after a rule was removed during file validation");
        unrelated.Cancel(); await unrelated.Completion; window.Close();
        Console.WriteLine("Backup deletion confirmations, cancellation, file choices, retry guards and unsafe-path protection passed.");
    }
    private static void TestFavoritesAndBackup(string root)
    {
        var store = new BackupStore(System.IO.Path.Combine(root, "favorites-unified.db"));
        var triggerCount = 0; var api = new BiliApi(new FavoritesUiFixture());
        var window = new BackupWindow(api, store, () => triggerCount++) { ShowInTaskbar = false, ShowActivated = false };
        window.Show(); Pump();
        ((System.Windows.Controls.TextBox)window.FindName("DirectoryBox")).Text = root;
        var group = (System.Windows.Controls.GroupBox)window.FindName("BackupGroup");
        var selectedButton = (System.Windows.Controls.Button)window.FindName("AddSelected");
        var allButton = (System.Windows.Controls.Button)window.FindName("AddAll");
        if (!group.IsAncestorOf((System.Windows.DependencyObject)window.FindName("Interval")) || !group.IsAncestorOf(selectedButton) || !group.IsAncestorOf(allButton) || !group.IsAncestorOf((System.Windows.DependencyObject)window.FindName("Rules")) || !group.IsAncestorOf((System.Windows.DependencyObject)window.FindName("RuleActions"))) throw new Exception("Backup controls were not inside the backup group");
        foreach (var language in new[] { "zh-CN", "zh-TW", "en" })
        {
            Localizer.Apply(language); Pump();
            if (!Equals(selectedButton.Content, Localizer.Translate("自动备份该收藏夹")) || !Equals(allButton.Content, Localizer.Translate("自动备份所有收藏夹"))) throw new Exception("Backup action labels were not localized");
        }
        var folders = (System.Windows.Controls.ComboBox)window.FindName("Folders");
        PumpUntil(WaitForAsync(() => folders.Items.Count == 1));
        ((System.Windows.Controls.Button)window.FindName("AddSelected")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        PumpUntil(WaitForAsync(() => store.List().Count == 1));
        if (store.List()[0].FolderId != 456 || triggerCount != 1) throw new Exception("Unified favorites window did not add backup rule");
        var rules = (System.Windows.Controls.DataGrid)window.FindName("Rules"); rules.SelectedItem = rules.Items[0];
        ((System.Windows.Controls.Button)window.FindName("ToggleRule")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (store.List().Single().Enabled) throw new Exception("Unified favorites window did not pause backup rule");
        PumpUntil(window.FetchFolderAsync());
        var batch = Application.Current.Windows.OfType<BatchWindow>().SingleOrDefault();
        if (batch is null || batch.Owner is not null || !window.IsEnabled || ((System.Windows.Controls.DataGrid)batch.FindName("Jobs")).Items.Count != 1) throw new Exception("Unified favorites window did not open independent batch video selection");
        ((System.Windows.Controls.Button)batch.FindName("Start")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        PumpUntil(WaitForAsync(() => !batch.IsRunning));
        if (!System.IO.Directory.Exists(BackupEngine.FolderDirectory(root, "Created folder", 456))) throw new Exception("Manual favorites download did not create its dedicated folder");
        batch.Close(); Pump();
        foreach (var taskWindow in Application.Current.Windows.OfType<DownloadQueueWindow>().ToArray()) taskWindow.Close();
        ((System.Windows.Controls.ComboBox)window.FindName("FolderKind")).SelectedIndex = 1;
        PumpUntil(window.LoadFoldersAsync());
        if ((folders.SelectedItem as FavoriteFolder)?.Id != 789) throw new Exception("Followed favorites were not available in unified window");
        ((System.Windows.Controls.Button)window.FindName("AddAll")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        PumpUntil(WaitForAsync(() => store.List().Count == 2));
        if (store.List().All(x => x.FolderId != 789)) throw new Exception("Followed favorites could not be added for backup");
        var selectAll = (System.Windows.Controls.CheckBox)window.FindName("SelectAllRules");
        selectAll.IsChecked = true;
        selectAll.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (rules.Items.Cast<BackupRuleRow>().Any(row => !row.IsChecked) || rules.Columns.OfType<System.Windows.Controls.DataGridCheckBoxColumn>().Any()) throw new Exception("Backup rule selection or enabled-state display failed");
        ((System.Windows.Controls.Button)window.FindName("ToggleRule")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (rules.Items.Cast<BackupRuleRow>().Any(row => !row.IsChecked) || store.List().Single(r => r.FolderId == 456).Enabled == false || store.List().Single(r => r.FolderId == 789).Enabled) throw new Exception("Checked backup rules were not retained or toggled");
        window.Close(); Pump();
        var fallback = new BackupWindow(api, null, () => { }) { ShowInTaskbar = false, ShowActivated = false };
        fallback.Show(); Pump();
        PumpUntil(WaitForAsync(() => ((System.Windows.Controls.ComboBox)fallback.FindName("Folders")).Items.Count == 1));
        if (((System.Windows.Controls.Button)fallback.FindName("Fetch")).IsEnabled == false || ((System.Windows.Controls.Button)fallback.FindName("AddSelected")).IsEnabled) throw new Exception("Favorites browsing did not remain available without backup storage");
        fallback.Close(); Pump();
        Console.WriteLine("Unified favorites and backup window loads both folder kinds, opens downloads and manages backup rules.");
    }
    private static void TestBatchQueueActions(string root)
    {
        var queue = new DownloadQueue(System.IO.Path.Combine(root, "batch-actions-history.json"));
        var onePath = System.IO.Path.Combine(root, "batch-one.mp4"); var twoPath = System.IO.Path.Combine(root, "batch-two.mp4"); var keepPath = System.IO.Path.Combine(root, "batch-keep.mp4");
        System.IO.File.WriteAllText(onePath, "one"); System.IO.File.WriteAllText(twoPath, "two"); System.IO.File.WriteAllText(keepPath, "keep");
        var one = queue.Enqueue("one", onePath, (_, _) => Task.FromResult<string?>(onePath));
        var two = queue.Enqueue("two", twoPath, (_, _) => Task.FromResult<string?>(twoPath));
        var keep = queue.Enqueue("keep", keepPath, (_, _) => Task.FromResult<string?>(keepPath));
        var first = queue.Enqueue("active 1", root, async (control, _) => { await Task.Delay(Timeout.Infinite, control.Token); return null; });
        var second = queue.Enqueue("active 2", root, async (control, _) => { await Task.Delay(Timeout.Infinite, control.Token); return null; });
        var confirmed = false; var confirmations = 0; var opened = new List<string>();
        var window = new DownloadQueueWindow(queue, () => { confirmations++; return confirmed; }, path => opened.Add(path), _ => { }) { ShowInTaskbar = false, ShowActivated = false };
        window.Show(); Pump();
        var grid = (System.Windows.Controls.DataGrid)window.FindName("Tasks"); grid.UpdateLayout();
        var row = (System.Windows.Controls.DataGridRow?)grid.ItemContainerGenerator.ContainerFromItem(first) ?? throw new Exception("Download row not displayed");
        var check = FindVisual<System.Windows.Controls.CheckBox>(row) ?? throw new Exception("Download row checkbox missing");
        if (!check.IsEnabled) throw new Exception("Download checkbox is disabled in read-only table");
        check.IsChecked = true; check.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump();
        if (!first.IsChecked) throw new Exception("Task checkbox did not update its task");
        second.IsChecked = true; PumpUntil(Task.Delay(550));
        ((System.Windows.Controls.Button)window.FindName("PauseButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (first.State != DownloadState.Paused || second.State != DownloadState.Paused) throw new Exception("Batch pause failed");
        ((System.Windows.Controls.Button)window.FindName("ResumeButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (first.State != DownloadState.Waiting || second.State != DownloadState.Waiting) throw new Exception("Resumed queued tasks did not return to the waiting state");
        ((System.Windows.Controls.Button)window.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        PumpUntil(Task.WhenAll(first.Completion, second.Completion));
        if (first.State != DownloadState.Canceled || second.State != DownloadState.Canceled) throw new Exception("Batch cancel failed");
        PumpUntil(Task.Delay(550));
        ((System.Windows.Controls.Button)window.FindName("RetryButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (first.State != DownloadState.Waiting || second.State != DownloadState.Waiting) throw new Exception("Batch retry failed");
        ((System.Windows.Controls.Button)window.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        PumpUntil(Task.WhenAll(first.Completion, second.Completion));
        var selectAll = (System.Windows.Controls.CheckBox)window.FindName("SelectAll");
        selectAll.IsChecked = true; selectAll.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (queue.Items.Any(x => !x.IsChecked)) throw new Exception("Select all failed");
        selectAll.IsChecked = false; selectAll.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (queue.Items.Any(x => x.IsChecked)) throw new Exception("Clear all failed");
        one.IsChecked = two.IsChecked = true; PumpUntil(Task.Delay(550));
        ((System.Windows.Controls.Button)window.FindName("OpenButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (opened.Count != 2 || !opened.Contains(onePath) || !opened.Contains(twoPath)) throw new Exception("Batch open did not use checked tasks");
        var delete = (System.Windows.Controls.Button)window.FindName("DeleteButton");
        delete.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (confirmations != 1 || !System.IO.File.Exists(onePath) || !System.IO.File.Exists(twoPath)) throw new Exception("Declined batch deletion changed files");
        confirmed = true; delete.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (confirmations != 2 || System.IO.File.Exists(onePath) || System.IO.File.Exists(twoPath) || !System.IO.File.Exists(keepPath) || queue.Items.Contains(one) || queue.Items.Contains(two)) throw new Exception("Batch delete removed incorrect files or records");
        first.IsChecked = second.IsChecked = true; PumpUntil(Task.Delay(550));
        ((System.Windows.Controls.Button)window.FindName("RemoveButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (queue.Items.Contains(first) || queue.Items.Contains(second) || !queue.Items.Contains(keep)) throw new Exception("Batch remove used wrong tasks");
        window.Close(); Pump();
        Console.WriteLine("Download checkboxes, select all, batch pause/resume/cancel, open, confirmation, delete and removal passed.");
    }
    private static void TestSearchPreview(bool render)
    {
        var fixture = new SearchUiFixture(); var window = new SearchWindow(new BiliApi(fixture)) { ShowInTaskbar = false, ShowActivated = false };
        window.Show(); Pump();
        ((System.Windows.Controls.TextBox)window.FindName("Keyword")).Text = "fixture";
        ((System.Windows.Controls.Button)window.FindName("SearchButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var results = (System.Windows.Controls.ListBox)window.FindName("Results");
        PumpUntil(WaitForAsync(() => results.Items.Count == 2 && ((SearchResultItem)results.Items[0]).Preview is not null && fixture.BlockedImageStarted));
        var old = (SearchResultItem)results.Items[0];
        if (old.Video.Views != 12345 || old.Video.AuthorId != 101 || !old.ViewsLabel.Contains("12,345", StringComparison.Ordinal)) throw new Exception("Search playback count or creator UID was not parsed or displayed");
        results.SelectedIndex = 0;
        PumpUntil(WaitForAsync(() => fixture.BlockedDetailStarted));
        var authorLink = (System.Windows.Documents.Hyperlink)window.FindName("AuthorLink");
        if (!authorLink.IsEnabled) throw new Exception("Search creator UID was not available while details loaded");
        results.SelectedIndex = 1;
        var title = (System.Windows.Controls.TextBlock)window.FindName("DetailTitle");
        var stats = (System.Windows.Controls.TextBlock)window.FindName("DetailStats");
        var description = (System.Windows.Controls.TextBlock)window.FindName("DetailDescription");
        PumpUntil(WaitForAsync(() => title.Text == "Detailed second video" && stats.Text.Contains("456", StringComparison.Ordinal)));
        if (!description.Text.Contains("Full description", StringComparison.Ordinal) || !fixture.DetailCanceled) throw new Exception("Video details or stale selection cancellation failed");
        if (((System.Windows.Documents.Run)window.FindName("AuthorName")).Text != "creator" || !authorLink.IsEnabled) throw new Exception("Creator hyperlink did not show the detail author");
        var sawUploader = false; var sawBatch = false; var polls = 0;
        var inspect = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        inspect.Tick += (_, _) =>
        {
            polls++;
            var uploader = Application.Current.Windows.OfType<UploaderWindow>().SingleOrDefault();
            if (uploader is not null) sawUploader |= uploader.Owner is null && !uploader.Topmost && window.IsEnabled && ((System.Windows.Controls.TextBox)uploader.FindName("Input")).Text == "777";
            var batch = Application.Current.Windows.OfType<BatchWindow>().SingleOrDefault();
            if (batch is not null) { sawBatch = batch.Owner is null && !batch.Topmost && uploader?.IsEnabled == true && ((System.Windows.Controls.DataGrid)batch.FindName("Jobs")).Items.Count == 1; batch.Close(); uploader?.Close(); inspect.Stop(); }
            else if (polls > 100) { uploader?.Close(); inspect.Stop(); }
        };
        inspect.Start(); authorLink.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
        PumpUntil(WaitForAsync(() => sawBatch || polls > 100)); inspect.Stop();
        if (!sawUploader || !sawBatch || fixture.UploaderMid != 777) throw new Exception("Creator link did not open and query the correct uploader's batch videos");
        if (render)
        {
            window.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = System.IO.File.Create(System.IO.Path.Combine(Environment.CurrentDirectory, "BiliDesktop", "artifacts", "search-window-review.png")); encoder.Save(file);
        }
        ((System.Windows.Controls.Button)window.FindName("Next")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        PumpUntil(WaitForAsync(() => results.Items.Count == 1 && ((SearchResultItem)results.Items[0]).Preview is not null && fixture.ImageCanceled));
        if (old.Preview is not null || !string.IsNullOrEmpty(title.Text)) throw new Exception("Previous page preview or details were retained");
        var current = (SearchResultItem)results.Items[0];
        results.SelectedIndex = 0; PumpUntil(WaitForAsync(() => title.Text == "No creator UID"));
        if (authorLink.IsEnabled) throw new Exception("Creator link allowed navigation without a valid UID");
        window.Close(); Pump();
        if (current.Preview is not null || fixture.CoverSentCookie) throw new Exception("Closing did not release previews or sent cookies to a cover host");
        Console.WriteLine("Search cover previews, views, details, rapid selection and current-page image cleanup passed.");
    }
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        if (!condition()) throw new TimeoutException("Expected UI state was not reached.");
    }
    private static T? FindVisual<T>(System.Windows.DependencyObject? parent) where T : System.Windows.DependencyObject
    {
        if (parent is T match) return match;
        if (parent is null) return null;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindVisual<T>(System.Windows.Media.VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
}

sealed class DeferredBackupValidationGateway : IBackupGateway
{
    public TaskCompletionSource CheckStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> CheckRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int DownloadAttempts { get; private set; }
    public long SessionVersion => 1;
    public Task<AccountInfo> AccountAsync() => Task.FromResult(new AccountInfo(123, "fixture"));
    public Task<FavoriteContents> FavoritesAsync(long folder, CancellationToken cancellation) => Task.FromResult(new FavoriteContents([new("BV1xx411c7mD", "fixture")], 0, 0));
    public Task<VideoInfo> VideoAsync(string bvid) => Task.FromResult(new VideoInfo(bvid, "fixture", "owner", "", [new(1, "P1 · one")], 123, null));
    public Task<MediaStreams> StreamsAsync(string bvid, VideoPage page) => throw new Exception("Removed rule requested streams");
    public Task InDownloadSlotAsync(Func<Task> work, CancellationToken cancellation, Func<bool> canStart) { DownloadAttempts++; return Task.CompletedTask; }
    public Task DownloadWithinSlotAsync(VideoStream video, AudioStream? audio, string destination, CancellationToken cancellation, Func<bool> canStart) => throw new Exception("Removed rule started a download");
    public async Task<bool> IsCompleteVideoAsync(string path, double? duration, CancellationToken cancellation) { CheckStarted.TrySetResult(); return await CheckRelease.Task.WaitAsync(cancellation); }
}

sealed class BlockingBatchMetadataFixture : HttpMessageHandler
{
    private int active, maximum, requests, canceled;
    public int Active => Volatile.Read(ref active);
    public int Maximum => Volatile.Read(ref maximum);
    public int Requests => Volatile.Read(ref requests);
    public int Canceled => Volatile.Read(ref canceled);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath == "/x/frontend/finger/spi") return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"code":0,"data":{"b_3":"fixture-device"}}""") };
        if (request.RequestUri.AbsolutePath != "/x/web-interface/view") throw new InvalidOperationException("Unexpected fixture request");
        Interlocked.Increment(ref requests); var count = Interlocked.Increment(ref active);
        int previous; do { previous = maximum; if (count <= previous) break; } while (Interlocked.CompareExchange(ref maximum, count, previous) != previous);
        try { await Task.Delay(Timeout.Infinite, cancellationToken); throw new Exception("Unexpected request completion"); }
        catch (OperationCanceledException) { Interlocked.Increment(ref canceled); throw; }
        finally { Interlocked.Decrement(ref active); }
    }
}

sealed class UpdateUiFixture : HttpMessageHandler
{
    public int Calls { get; private set; }
    public bool Fail { get; set; }
    public bool Empty { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Fail ? new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Empty ? "[]" : """[{"tag_name":"beta5","draft":false,"prerelease":true,"published_at":"2026-10-01T10:30:00Z","body":"release notes"}]""") });
    }
}

sealed class UiStartupStore : IStartupValueStore
{
    public object? Value { get; set; }
    public object? Read() => Value;
    public void Write(string command) => Value = command;
    public void Delete() => Value = null;
}

sealed class QrUiFixture : HttpMessageHandler
{
    public bool Polling { get; private set; }
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/generate", StringComparison.Ordinal)) return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"code":0,"data":{"url":"https://passport.bilibili.com/qr","qrcode_key":"fixture"}}""") };
        if (!path.EndsWith("/poll", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected QR request");
        Polling = true; await Release.Task.WaitAsync(cancellationToken);
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"code":0,"data":{"code":0}}""") };
        response.Headers.TryAddWithoutValidation("Set-Cookie", "SESSDATA=fixture; Domain=.bilibili.com; Path=/; Max-Age=3600; Secure; HttpOnly");
        return response;
    }
}

sealed class SearchUiFixture : HttpMessageHandler
{
    public bool BlockedImageStarted { get; private set; }
    public bool ImageCanceled { get; private set; }
    public bool BlockedDetailStarted { get; private set; }
    public bool DetailCanceled { get; private set; }
    public bool CoverSentCookie { get; private set; }
    public long UploaderMid { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!; var path = uri.AbsolutePath;
        if (uri.Host == "i0.hdslb.com")
        {
            CoverSentCookie |= request.Headers.Contains("Cookie");
            if (path == "/old2.png")
            {
                BlockedImageStarted = true;
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { ImageCanceled = true; throw; }
            }
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lX8AAAAASUVORK5CYII=");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(png) };
        }
        if (path == "/x/web-interface/view" && uri.Query.Contains("BV1xx411c7mD", StringComparison.Ordinal))
        {
            BlockedDetailStarted = true;
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { DetailCanceled = true; throw; }
        }
        if (path == "/x/space/wbi/arc/search") UploaderMid = uri.Query.Contains("mid=777", StringComparison.Ordinal) ? 777 : -1;
        var json = path switch
        {
            "/x/frontend/finger/spi" => """{"code":0,"data":{"b_3":"fixture-device"}}""",
            "/x/web-interface/nav" => """{"code":-101,"data":{"wbi_img":{"img_url":"https://example.com/abcdefghijklmnopqrstuvwxyz123456.png","sub_url":"https://example.com/ABCDEFGHIJKLMNOPQRSTUVWXYZ123456.png"}}}""",
            "/x/web-interface/wbi/search/type" when uri.Query.Contains("page=2", StringComparison.Ordinal) => """{"code":0,"data":{"numPages":2,"result":[{"bvid":"BV0000000003","title":"third","author":"author","duration":"0:20","play":88,"pic":"//i0.hdslb.com/current.png"}]}}""",
            "/x/web-interface/wbi/search/type" => """{"code":0,"data":{"numPages":2,"result":[{"bvid":"BV1xx411c7mD","title":"first","author":"author","mid":"101","duration":"1:00","play":"12345","pic":"//i0.hdslb.com/old1.png"},{"bvid":"BV0000000002","title":"second","author":"author","mid":222,"duration":"2:00","play":44,"pic":"https://i0.hdslb.com/old2.png"}]}}""",
            "/x/web-interface/view" when uri.Query.Contains("BV0000000003", StringComparison.Ordinal) => """{"code":0,"data":{"title":"No creator UID","owner":{"name":"unknown"},"stat":{"view":1}}}""",
            "/x/web-interface/view" => """{"code":0,"data":{"title":"Detailed second video","owner":{"name":"creator","mid":777},"desc":"Full description","tname":"Music","pubdate":1700000000,"duration":120,"stat":{"view":456,"like":34,"coin":12,"favorite":5,"reply":3,"danmaku":2,"share":1}}}""",
            "/x/space/wbi/arc/search" => """{"code":0,"data":{"page":{"count":1},"list":{"vlist":[{"bvid":"BV0000000002","title":"creator video"}]}}}""",
            _ => throw new InvalidOperationException("Unexpected fixture request: " + path)
        };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };
    }
}

sealed class FavoritesUiFixture : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var json = request.RequestUri!.AbsolutePath switch
        {
            "/x/frontend/finger/spi" => """{"code":0,"data":{"b_3":"fixture-device"}}""",
            "/x/web-interface/nav" => """{"code":0,"data":{"isLogin":true,"mid":123,"uname":"fixture"}}""",
            "/x/v3/fav/folder/created/list-all" => """{"code":0,"data":{"count":1,"list":[{"id":456,"title":"Created folder","media_count":1}]}}""",
            "/x/v3/fav/folder/collected/list" => """{"code":0,"data":{"count":1,"list":[{"id":789,"title":"Followed folder","media_count":1,"state":0}]}}""",
            "/x/v3/fav/resource/list" => """{"code":0,"data":{"info":{"media_count":1},"medias":[{"type":2,"attr":0,"bvid":"BV1xx411c7mD","title":"fixture video"}],"has_more":false}}""",
            "/x/web-interface/view" => """{"code":0,"data":{"bvid":"BV1xx411c7mD","title":"fixture","owner":{"name":"author","mid":123},"pages":[{"cid":1,"page":1,"part":"one"}]}}""",
            _ => throw new InvalidOperationException("Unexpected fixture request: " + request.RequestUri.AbsolutePath)
        };
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}

sealed class MainWindowApiFixture : HttpMessageHandler
{
    public int ViewRequests { get; private set; }
    public int StreamRequests { get; private set; }
    public string? LastViewUserAgent { get; private set; }
    public bool BlockFirstView { get; init; }
    public TaskCompletionSource FirstViewRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/x/web-interface/view") { ViewRequests++; LastViewUserAgent = request.Headers.UserAgent.ToString(); if (BlockFirstView && ViewRequests == 1) await FirstViewRelease.Task.WaitAsync(cancellationToken); }
        if (path == "/x/player/wbi/playurl") StreamRequests++;
        var json = path switch
        {
            "/x/frontend/finger/spi" => """{"code":0,"data":{"b_3":"fixture-device"}}""",
            "/x/web-interface/view" => """{"code":0,"data":{"bvid":"BV1xx411c7mD","title":"fixture","owner":{"name":"author","mid":123},"pages":[{"cid":1,"page":1,"part":"one"},{"cid":2,"page":2,"part":"two"}]}}""",
            "/x/web-interface/nav" => """{"code":-101,"data":{"wbi_img":{"img_url":"https://example.com/abcdefghijklmnopqrstuvwxyz123456.png","sub_url":"https://example.com/ABCDEFGHIJKLMNOPQRSTUVWXYZ123456.png"}}}""",
            "/x/web-interface/wbi/search/type" => """{"code":0,"data":{"numPages":1,"result":[{"bvid":"BV1xx411c7mD","title":"cat fixture","author":"author","duration":"1:00","pubdate":2000000000}]}}""",
            "/x/player/wbi/playurl" => """{"code":0,"data":{"dash":{"video":[{"id":80,"codecs":"avc1","bandwidth":200,"baseUrl":"http://example.test/a"},{"id":80,"codecs":"hev1","bandwidth":100,"baseUrl":"http://example.test/b"},{"id":64,"codecs":"avc1","bandwidth":80,"baseUrl":"http://example.test/c"}],"audio":[{"id":30280,"codecs":"aac","bandwidth":20,"baseUrl":"http://example.test/audio"}]}}}""",
            _ => throw new InvalidOperationException("Unexpected fixture request: " + path)
        };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };
    }
}
