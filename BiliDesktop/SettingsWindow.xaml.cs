using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace BiliDesktop;

public partial class SettingsWindow : Window
{
    private readonly BiliApi api;
    private readonly IStartupValueStore startupStore;
    private readonly string? startupExecutable;
    private readonly Func<Task> onUserAgentChanged;
    private readonly Action<bool> onBackgroundChanged;

    internal SettingsWindow(BiliApi api, IStartupValueStore startupStore, string? startupExecutable, Func<Task> onUserAgentChanged, Action<bool> onBackgroundChanged)
    {
        this.api = api; this.startupStore = startupStore; this.startupExecutable = startupExecutable;
        this.onUserAgentChanged = onUserAgentChanged; this.onBackgroundChanged = onBackgroundChanged;
        InitializeComponent();
        SetResourceReference(TitleProperty, Localizer.Key("设置"));
        GeneralGroup.SetResourceReference(HeaderedContentControl.HeaderProperty, Localizer.Key("常规"));
        DownloadGroup.SetResourceReference(HeaderedContentControl.HeaderProperty, Localizer.Key("下载设置"));
        LanguageLabel.SetResourceReference(TextBlock.TextProperty, Localizer.Key("语言"));
        StartAtLogin.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("登录时启动"));
        AllowBackground.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("允许后台运行"));
        UseDefaultVideoDirectory.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("使用默认视频下载目录（关闭后每次询问）"));
        DownloadDirectoryButton.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("选择下载目录"));
        ConnectionsLabel.SetResourceReference(TextBlock.TextProperty, Localizer.Key("连接数"));
        CacheLabel.SetResourceReference(TextBlock.TextProperty, Localizer.Key("内存缓存（MiB）"));
        MaxTasksLabel.SetResourceReference(TextBlock.TextProperty, Localizer.Key("最大并发下载任务数"));
        UnlimitedTasks.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("不限"));
        UserAgentPresetLabel.SetResourceReference(TextBlock.TextProperty, Localizer.Key("平台预设"));
        ConfirmButton.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("确定"));
        ApplyButton.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("应用"));
        CancelButton.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("取消"));
        LanguageSelector.SelectedIndex = Localizer.Language switch { "zh-TW" => 1, "en" => 2, _ => 0 };
        try { AllowBackground.IsChecked = AppSettings.LoadAllowBackground(); } catch (Exception) { AllowBackground.IsChecked = false; }
        try { DownloadDirectoryBox.Text = AppSettings.LoadDownloadDirectory(); UseDefaultVideoDirectory.IsChecked = AppSettings.LoadUseDefaultVideoDirectory(); }
        catch (Exception) { DownloadDirectoryBox.Text = AppSettings.IndependentDownloadDirectory(); UseDefaultVideoDirectory.IsChecked = true; }
        UserAgentPresetBox.ItemsSource = UserAgentPresets.All; UserAgentBox.Text = api.UserAgent;
        UserAgentPresetBox.SelectedItem = UserAgentPresets.All.FirstOrDefault(x => x.Value == UserAgentBox.Text) ?? UserAgentPresets.All.Last();
        var options = MediaDownload.CurrentOptions;
        Connections.Text = options.Connections.ToString(); CacheSize.Text = options.CacheMiB.ToString(); MaxTasks.Text = (options.MaxConcurrentTasks == 0 ? 4 : options.MaxConcurrentTasks).ToString(); UnlimitedTasks.IsChecked = options.MaxConcurrentTasks == 0;
        try { RefreshStartup(); } catch (Exception ex) { StartAtLogin.IsEnabled = false; Status.Text = "开机启动不可用：" + ex.Message; }
    }
    private void RefreshStartup()
    {
        if (startupExecutable is null) { StartAtLogin.IsEnabled = false; return; }
        var state = StartupRegistration.GetState(startupStore, startupExecutable);
        StartAtLogin.IsChecked = state == StartupState.Enabled; StartAtLogin.IsEnabled = state != StartupState.Other;
        if (state == StartupState.Other) StartAtLogin.SetResourceReference(ToolTipProperty, Localizer.Key("启动项被其他版本占用。"));
        else if (state == StartupState.OtherVersion) StartAtLogin.SetResourceReference(ToolTipProperty, Localizer.Key("启动项指向旧版；勾选后切换到当前版本。"));
        else StartAtLogin.ClearValue(ToolTipProperty);
    }
    private void UserAgentPreset_Changed(object sender, SelectionChangedEventArgs e) { if (UserAgentBox is not null && UserAgentPresetBox.SelectedItem is UserAgentPreset { Value: string value }) UserAgentBox.Text = value; }
    private void UserAgentText_Changed(object sender, TextChangedEventArgs e) { if (UserAgentPresetBox?.SelectedItem is UserAgentPreset { Value: string value } && UserAgentBox.Text != value) UserAgentPresetBox.SelectedItem = UserAgentPresets.All.Last(); }
    private void DownloadDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Localizer.Translate("选择下载目录"), Multiselect = false };
        if (dialog.ShowDialog(this) == true) DownloadDirectoryBox.Text = dialog.FolderName;
    }
    private void UnlimitedTasks_Changed(object sender, RoutedEventArgs e) { if (MaxTasks is not null && UnlimitedTasks is not null) MaxTasks.IsEnabled = UnlimitedTasks.IsChecked != true; }
    private void UseDefaultDirectory_Changed(object sender, RoutedEventArgs e) { if (DownloadDirectoryBox is not null && DownloadDirectoryButton is not null && UseDefaultVideoDirectory is not null) DownloadDirectoryBox.IsEnabled = DownloadDirectoryButton.IsEnabled = UseDefaultVideoDirectory.IsChecked == true; }

    internal bool ApplySettings()
    {
        try
        {
            var ua = UserAgentBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(ua) || ua.Any(c => c < 32 || c > 126)) throw new ArgumentException("User-Agent 不能为空，且只能包含单行可打印 ASCII 字符。");
            if (!int.TryParse(Connections.Text, out var connections) || !int.TryParse(CacheSize.Text, out var cache)) throw new ArgumentException("请输入整数。");
            var maximum = 0;
            if (UnlimitedTasks.IsChecked != true && (!int.TryParse(MaxTasks.Text, out maximum) || maximum < 1)) throw new ArgumentException("请输入大于零的并发任务数。");
            if (connections is < 1 or > 64 || cache is < 0 or > 512) throw new ArgumentException("连接数或缓存大小超出范围。");
            var useDefault = UseDefaultVideoDirectory.IsChecked == true;
            var directory = Path.GetFullPath(DownloadDirectoryBox.Text);
            if (useDefault && !Directory.Exists(directory)) throw new DirectoryNotFoundException("下载目录不存在。");
            var options = new TransferOptions(connections, cache, maximum);
            var language = LanguageSelector.SelectedIndex switch { 1 => "zh-TW", 2 => "en", _ => "zh-CN" };
            var background = AllowBackground.IsChecked == true;
            var oldUa = api.UserAgent;
            var startupBefore = startupExecutable is not null && StartAtLogin.IsEnabled ? startupStore.Read() : null;
            var startupChanged = false;
            try
            {
                if (startupExecutable is not null && StartAtLogin.IsEnabled)
                {
                    startupChanged = true;
                    StartupRegistration.SetEnabled(startupStore, startupExecutable, StartAtLogin.IsChecked == true);
                }
                AppSettings.SaveAll(ua, options, language, directory, background, useDefault);
            }
            catch (Exception ex)
            {
                if (startupChanged) try { if (startupBefore is string original) startupStore.Write(original); else startupStore.Delete(); }
                    catch (Exception rollback) { throw new InvalidOperationException(ex.Message + "；恢复启动项失败：" + rollback.Message, ex); }
                throw;
            }
            api.SetUserAgent(ua); MediaDownload.Configure(options); onBackgroundChanged(background);
            Localizer.Apply(language); UserAgentPresetBox.Items.Refresh(); RefreshStartup();
            Status.Text = Localizer.Translate("设置已应用。");
            if (oldUa != ua) _ = ReparseAsync();
            return true;
        }
        catch (Exception ex) { Status.Text = Localizer.Translate("设置失败：") + ex.Message; return false; }
    }
    private async Task ReparseAsync() { try { await onUserAgentChanged(); } catch (Exception ex) { Status.Text = Localizer.Translate("解析失败：") + ex.Message; } }
    private void Apply_Click(object sender, RoutedEventArgs e) => ApplySettings();
    private void Confirm_Click(object sender, RoutedEventArgs e) { if (ApplySettings()) Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } }
}
