using System.IO;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace BiliDesktop;

public enum StartupState { Disabled, Enabled, OtherVersion, Other }

public interface IStartupValueStore
{
    object? Read();
    void Write(string command);
    void Delete();
}

[SupportedOSPlatform("windows")]
public sealed class WindowsStartupStore(string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", string valueName = "BiliDesktop") : IStartupValueStore
{
    public object? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, false);
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }
    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, true) ?? throw new IOException("无法打开启动项。");
        key.SetValue(valueName, command, RegistryValueKind.String);
    }
    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, true);
        key?.DeleteValue(valueName, false);
    }
}

public static class StartupRegistration
{
    public static string CommandFor(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || executable.IndexOfAny(['"', '\r', '\n']) >= 0 || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("无法确定当前程序路径。");
        return "\"" + Path.GetFullPath(executable) + "\" --background";
    }

    public static StartupState GetState(IStartupValueStore store, string executable)
    {
        var expected = CommandFor(executable);
        var value = store.Read();
        if (value is null) return StartupState.Disabled;
        if (value is not string actual) return StartupState.Other;
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return StartupState.Enabled;
        const string suffix = "\" --background";
        if (!actual.StartsWith('"') || !actual.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return StartupState.Other;
        var previousPath = actual[1..^suffix.Length];
        try
        {
            if (previousPath.IndexOfAny(['"', '\r', '\n', '\0']) >= 0 || !Path.IsPathFullyQualified(previousPath) || !Path.GetFileName(previousPath).Equals("BiliDesktop.exe", StringComparison.OrdinalIgnoreCase)) return StartupState.Other;
            if (File.Exists(previousPath))
            {
                var info = FileVersionInfo.GetVersionInfo(previousPath);
                if (!string.Equals(info.ProductName, "BiliDesktop", StringComparison.OrdinalIgnoreCase) || !string.Equals(info.OriginalFilename, "BiliDesktop.dll", StringComparison.OrdinalIgnoreCase)) return StartupState.Other;
            }
            return StartupState.OtherVersion;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return StartupState.Other; }
    }

    public static void SetEnabled(IStartupValueStore store, string executable, bool enabled)
    {
        var state = GetState(store, executable);
        if (state == StartupState.Other) throw new InvalidOperationException("启动项被其他版本占用。");
        if (enabled && state is StartupState.Disabled or StartupState.OtherVersion) store.Write(CommandFor(executable));
        else if (!enabled && state == StartupState.Enabled) store.Delete();
    }
}
