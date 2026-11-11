using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text;

namespace BiliDesktop;

// The mutex is held on the application's UI thread until shutdown completes.
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource stopping = new();
    private readonly object gate = new();
    private NamedPipeServerStream? server;
    private bool disposed;
    internal bool IsPrimary { get; }

    internal SingleInstance(string? identity = null)
    {
        identity ??= WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        pipeName = "BiliDesktop-activation-" + suffix;
        mutex = new Mutex(false, @"Global\BiliDesktop-instance-" + suffix);
        try { IsPrimary = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
        catch { mutex.Dispose(); stopping.Dispose(); throw; }
    }

    internal void StartListening(Action activate)
    {
        if (!IsPrimary || disposed) throw new InvalidOperationException("Only the active instance can listen for activation.");
        _ = ListenAsync(activate);
    }

    private async Task ListenAsync(Action activate)
    {
        while (!stopping.IsCancellationRequested)
        {
            NamedPipeServerStream? current = null;
            try
            {
                lock (gate)
                {
                    if (disposed) return;
                    using var identity = WindowsIdentity.GetCurrent();
                    var sid = identity.User ?? throw new InvalidOperationException("无法确定当前 Windows 用户。");
                    // Restrict activation to this account while allowing either UAC elevation level.
                    var security = new PipeSecurity();
                    security.SetOwner(sid);
                    security.SetAccessRuleProtection(true, false);
                    security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
                    server = current = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                }
                await current.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                var request = new byte[1];
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                if (await current.ReadAsync(request, timeout.Token).ConfigureAwait(false) == 1 && request[0] == 1)
                {
                    activate();
                    await current.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
                    await current.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) when (stopping.IsCancellationRequested) { return; }
            catch (Exception) when (stopping.IsCancellationRequested) { return; }
            catch (Exception) when (!stopping.IsCancellationRequested) { await Task.Delay(100).ConfigureAwait(false); }
            finally { lock (gate) { if (ReferenceEquals(server, current)) server = null; current?.Dispose(); } }
        }
    }

    internal bool NotifyPrimary(TimeSpan timeout)
    {
        if (IsPrimary || disposed) return false;
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cancellation = new CancellationTokenSource(timeout);
            client.ConnectAsync(cancellation.Token).GetAwaiter().GetResult();
            client.WriteAsync(new byte[] { 1 }, cancellation.Token).AsTask().GetAwaiter().GetResult();
            var reply = new byte[1];
            return client.ReadAsync(reply, cancellation.Token).AsTask().GetAwaiter().GetResult() == 1 && reply[0] == 1;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or TimeoutException) { return false; }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; stopping.Cancel(); server?.Dispose(); server = null;
        }
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
