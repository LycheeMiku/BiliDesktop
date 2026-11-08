namespace BiliDesktop;

internal sealed class DownloadCloseGuard
{
    private bool approved;

    public bool CanClose(int activeDownloads, Func<bool> confirmInterruption)
    {
        if (activeDownloads < 0) throw new ArgumentOutOfRangeException(nameof(activeDownloads));
        if (approved || activeDownloads == 0) return true;
        if (!confirmInterruption()) return false;
        approved = true;
        return true;
    }

    public void Reset() => approved = false;
}
