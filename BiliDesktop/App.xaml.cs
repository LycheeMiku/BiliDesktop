using System.Configuration;
using System.Data;
using System.Windows;

namespace BiliDesktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private SingleInstance? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        try { Localizer.Apply(AppSettings.LoadLanguage()); }
        catch (Exception) { Localizer.Apply("zh-CN"); }
        try
        {
            instance = new SingleInstance();
            if (!instance.IsPrimary)
            {
                if (!instance.NotifyPrimary(TimeSpan.FromSeconds(3)))
                    MessageBox.Show(Localizer.Translate("程序已在运行，但无法唤醒现有窗口。请稍后重试，或从托盘退出后重新启动。"), "BiliDesktop", MessageBoxButton.OK, MessageBoxImage.Information);
                instance.Dispose(); instance = null;
                Shutdown(); return;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Localizer.Translate("无法检查程序是否已运行：") + ex.Message, "BiliDesktop", MessageBoxButton.OK, MessageBoxImage.Error);
            instance?.Dispose(); instance = null; Shutdown(); return;
        }
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, args) =>
        {
            if (sender is Window window && ReferenceEquals(sender, args.OriginalSource)) Localizer.AttachStatuses(window);
        }));
        base.OnStartup(e);
        var main = new MainWindow(); MainWindow = main;
        instance.StartListening(() => Dispatcher.BeginInvoke(() => { if (MainWindow is MainWindow window) window.ActivateFromAnotherInstance(); }));
        main.Show();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); instance = null; base.OnExit(e); }
}

