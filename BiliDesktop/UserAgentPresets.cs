namespace BiliDesktop;

public sealed record UserAgentPreset(string Label, string? Value)
{
    public string Name => Value is null ? Localizer.Translate("自定义") : Label;
}

public static class UserAgentPresets
{
    public static IReadOnlyList<UserAgentPreset> All { get; } = Array.AsReadOnly(new[]
    {
        new UserAgentPreset("Windows · Chrome", BiliApi.DefaultUserAgent),
        new UserAgentPreset("Windows · Firefox", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:132.0) Gecko/20100101 Firefox/132.0"),
        new UserAgentPreset("Windows · Edge", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0"),
        new UserAgentPreset("Android · Chrome", "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36"),
        new UserAgentPreset("Android · Firefox", "Mozilla/5.0 (Android 14; Mobile; rv:132.0) Gecko/132.0 Firefox/132.0"),
        new UserAgentPreset("iOS · iPhone Safari", "Mozilla/5.0 (iPhone; CPU iPhone OS 17_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Mobile/15E148 Safari/604.1"),
        new UserAgentPreset("iPadOS · Safari", "Mozilla/5.0 (iPad; CPU OS 17_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Mobile/15E148 Safari/604.1"),
        new UserAgentPreset("macOS · Safari", "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Safari/605.1.15"),
        new UserAgentPreset("macOS · Chrome", "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_6) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"),
        new UserAgentPreset("macOS · Apple Silicon Chrome", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"),
        new UserAgentPreset("Linux · Chrome", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"),
        new UserAgentPreset("Linux · Firefox", "Mozilla/5.0 (X11; Linux x86_64; rv:132.0) Gecko/20100101 Firefox/132.0"),
        new UserAgentPreset("自定义", null)
    });
}
