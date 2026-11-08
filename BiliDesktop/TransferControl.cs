namespace BiliDesktop;

public record TransferProgress(string Stage, long Completed, long Total, long BytesPerSecond, int Connections);

public sealed class TransferControl : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly object sync = new();
    private TaskCompletionSource? resume;
    private bool canPause = true;
    private Func<Task>? waitAfterPause;
    public CancellationToken Token => cancellation.Token;
    public bool IsPaused { get { lock (sync) return resume is not null; } }
    public bool CanPause { get { lock (sync) return canPause; } }
    public IProgress<TransferProgress>? Progress { get; init; }
    public Action<string, string>? Identity { get; init; }
    public bool IsSkipped { get; private set; }
    public void MarkSkipped() => IsSkipped = true;
    public bool Pause()
    {
        lock (sync) { if (!canPause || cancellation.IsCancellationRequested || resume is not null) return false; resume = new(TaskCreationOptions.RunContinuationsAsynchronously); return true; }
    }
    public void Resume() { TaskCompletionSource? old; lock (sync) { old = resume; resume = null; } old?.TrySetResult(); }
    public void Cancel() { cancellation.Cancel(); Resume(); }
    public async Task WaitAsync() { Task? task; lock (sync) task = resume?.Task; if (task is not null) await task.WaitAsync(Token); Token.ThrowIfCancellationRequested(); }
    internal void SetPausedWait(Func<Task>? wait) => waitAfterPause = wait;
    internal Task WaitAfterPauseAsync() => waitAfterPause?.Invoke() ?? WaitAsync();
    internal async Task WaitIfPausedAsync() { if (IsPaused) await WaitAfterPauseAsync(); else Token.ThrowIfCancellationRequested(); }
    public void PrepareTransfer() { lock (sync) canPause = true; }
    public async Task BeginProcessingAsync()
    {
        while (true)
        {
            Task? task;
            lock (sync) { task = resume?.Task; if (task is null) { canPause = false; break; } }
            await WaitAfterPauseAsync();
        }
        Token.ThrowIfCancellationRequested();
    }
    public void Dispose() { cancellation.Dispose(); }
}
