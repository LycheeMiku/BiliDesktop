using System.Windows;

namespace BiliDesktop;

internal static class WindowCoordinator
{
    private static void ShowOrActivate(Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
    internal static DownloadQueueWindow ShowDownloads(DownloadQueue queue)
    {
        var window = Application.Current.Windows.OfType<DownloadQueueWindow>().FirstOrDefault() ?? new DownloadQueueWindow(queue);
        ShowOrActivate(window);
        return window;
    }
    internal static LiveWindow ShowLive(LiveManager manager)
    {
        var window = Application.Current.Windows.OfType<LiveWindow>().FirstOrDefault() ?? new LiveWindow(manager);
        ShowOrActivate(window);
        return window;
    }
}
