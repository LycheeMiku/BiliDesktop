namespace BiliDesktop;

public static class BatchScheduler
{
    public static async Task RunAsync<T>(IEnumerable<T> items, int limit, Func<T, Task> work, Action<T> skipped, CancellationToken token)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        using var semaphore = new SemaphoreSlim(limit);
        async Task Execute(T item)
        {
            try { await semaphore.WaitAsync(token); }
            catch (OperationCanceledException) { skipped(item); return; }
            try { if (token.IsCancellationRequested) skipped(item); else await work(item); }
            finally { semaphore.Release(); }
        }
        await Task.WhenAll(items.Select(Execute));
    }
}
