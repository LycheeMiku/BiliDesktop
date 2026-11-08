using System.Configuration;
using System.Data;
using System.Windows;

namespace BiliDesktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        try { Localizer.Apply(AppSettings.LoadLanguage()); }
        catch (Exception) { Localizer.Apply("zh-CN"); }
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, args) =>
        {
            if (sender is Window window && ReferenceEquals(sender, args.OriginalSource)) Localizer.AttachStatuses(window);
        }));
        base.OnStartup(e);
    }
}

