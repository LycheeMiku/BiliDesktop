using System.Windows;

namespace BiliDesktop;

internal sealed class BackgroundWindowController(Window window, Func<string, string> translate, Action? exit = null, Action? showAbout = null) : IDisposable
{
    private System.Windows.Forms.NotifyIcon? icon;
    private System.Windows.Forms.ContextMenuStrip? menu;
    private readonly List<Window> hiddenWindows = [];
    internal bool IsActive => icon?.Visible == true;

    public void Enter()
    {
        try
        {
            if (icon is null)
            {
                menu = new System.Windows.Forms.ContextMenuStrip();
                var open = new System.Windows.Forms.ToolStripMenuItem(translate("打开主窗口"));
                var exit = new System.Windows.Forms.ToolStripMenuItem(translate("退出程序"));
                open.Click += (_, _) => RequestOpen();
                exit.Click += (_, _) => RequestExit();
                menu.Items.Add(open);
                if (showAbout is not null) { var about = new System.Windows.Forms.ToolStripMenuItem(translate("关于")); about.Click += (_, _) => RequestAbout(); menu.Items.Add(about); }
                menu.Items.Add(exit);
                icon = new System.Windows.Forms.NotifyIcon();
                icon.Icon = System.Drawing.SystemIcons.Application; icon.Text = "BiliDesktop"; icon.ContextMenuStrip = menu;
                icon.DoubleClick += (_, _) => RequestOpen();
                icon.Visible = true;
            }
            hiddenWindows.Clear();
            if (Application.Current is not null)
                foreach (Window other in Application.Current.Windows)
                    if (!ReferenceEquals(other, window) && other.IsVisible) { hiddenWindows.Add(other); other.Hide(); }
            window.Hide();
        }
        catch { RestoreHiddenWindows(); Dispose(); throw; }
    }

    internal void RequestOpen() => window.Dispatcher.BeginInvoke(Restore);
    internal void RequestAbout() { if (showAbout is not null) window.Dispatcher.BeginInvoke(showAbout); }
    internal void RequestExit() => window.Dispatcher.BeginInvoke(() => { Restore(); if (exit is null) window.Close(); else exit(); });

    private void Restore()
    {
        window.Show();
        RestoreHiddenWindows();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate(); Dispose();
    }

    private void RestoreHiddenWindows()
    {
        var windows = hiddenWindows.ToArray(); hiddenWindows.Clear();
        foreach (var other in windows)
            if (Application.Current?.Windows.Cast<Window>().Contains(other) == true) other.Show();
    }

    public void Dispose()
    {
        if (icon is not null) { icon.Visible = false; icon.Dispose(); icon = null; }
        menu?.Dispose(); menu = null;
    }
}
