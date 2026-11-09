using System.Windows;
using System.Windows.Threading;

namespace BiliDesktop;

internal static class ExclusiveWindow
{
    private static Window? active;
    private static readonly Dictionary<Window, bool> previous = [];
    private static DispatcherTimer? monitor;
    static ExclusiveWindow() => EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (active is not null && sender is Window window && ReferenceEquals(sender, e.OriginalSource) && !ReferenceEquals(window, active)) Disable(window);
    }
    private static void Disable(Window window)
    {
        if (!previous.ContainsKey(window)) previous.Add(window, window.IsEnabled);
        window.IsEnabled = false;
    }
    internal static bool ActivateExisting()
    {
        if (active is null) return false;
        if (!active.IsVisible) active.Show();
        if (active.WindowState == WindowState.Minimized) active.WindowState = WindowState.Normal;
        active.Activate(); return true;
    }
    internal static void Show(Window window)
    {
        if (active is not null) throw new InvalidOperationException("已有设置或关于窗口打开。");
        active = window; window.Topmost = true;
        try
        {
            if (Application.Current is not null)
                foreach (Window other in Application.Current.Windows) if (!ReferenceEquals(other, window)) Disable(other);
            monitor = new DispatcherTimer(TimeSpan.FromMilliseconds(25), DispatcherPriority.Send, (_, _) => DisableNewWindows(), window.Dispatcher);
            monitor.Start();
            window.Closed += (_, _) => Release(window);
            window.Show(); window.Activate();
        }
        catch { Release(window); throw; }
    }
    private static void DisableNewWindows()
    {
        if (active is null || Application.Current is null) return;
        foreach (Window other in Application.Current.Windows)
            if (!ReferenceEquals(other, active) && !previous.ContainsKey(other)) Disable(other);
    }
    private static void Release(Window window)
    {
        if (!ReferenceEquals(active, window)) return;
        monitor?.Stop(); monitor = null;
        active = null;
        foreach (var (other, enabled) in previous.ToArray())
            if (Application.Current?.Windows.Cast<Window>().Contains(other) == true) other.IsEnabled = enabled;
        previous.Clear();
    }
}
