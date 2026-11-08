using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BililiveRecorder.Core.Recording
{
    // Establish a usable FLV stream before creating a recording file.
    internal static class LiveStreamConnector
    {
        internal static async Task<(Stream Stream, string Url)> ConnectAsync(IReadOnlyList<string> urls, Func<string, Task<Stream>> connect, TimeSpan firstBytesTimeout, CancellationToken cancellation, Action<string, string>? onRetry = null)
        {
            Exception? last = null;
            for (var index = 0; index < urls.Count; index++)
            {
                var url = urls[index];
                cancellation.ThrowIfCancellationRequested();
                Stream? stream = null;
                var phase = "connect";
                try
                {
                    stream = await connect(url).ConfigureAwait(false);
                    phase = "data";
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    timeout.CancelAfter(firstBytesTimeout);
                    var prefix = new byte[13]; var count = 0;
                    while (count < prefix.Length)
                    {
                        var read = await stream.ReadAsync(prefix, count, prefix.Length - count, timeout.Token).ConfigureAwait(false);
                        if (read == 0) throw new InvalidDataException("直播线路在收到媒体数据前断开。");
                        count += read;
                    }
                    if (prefix[0] != (byte)'F' || prefix[1] != (byte)'L' || prefix[2] != (byte)'V' || prefix[3] != 1) throw new InvalidDataException("直播线路返回了无效的数据。");
                    return (new PrefixReplayStream(stream, prefix), url);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { stream?.Dispose(); throw; }
                catch (Exception ex) when (ex is OperationCanceledException || ex is HttpRequestException || ex is IOException || ex is InvalidDataException || ex is TimeoutException || ex is SocketException)
                {
                    stream?.Dispose(); last = ex;
                    if (index + 1 < urls.Count) onRetry?.Invoke(phase, ex.GetType().Name);
                }
            }
            throw new TimeoutException("所有可用直播线路均连接失败或未返回有效数据。", last);
        }
    }

    internal sealed class PrefixReplayStream : Stream
    {
        private readonly Stream inner;
        private readonly byte[] prefix;
        private readonly IDisposable? owner;
        private int offset;
        internal PrefixReplayStream(Stream inner, byte[] prefix, IDisposable? owner = null) { this.inner = inner; this.prefix = prefix; this.owner = owner; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int index, int count)
        {
            if (this.offset >= prefix.Length) return inner.Read(buffer, index, count);
            var length = Math.Min(count, prefix.Length - this.offset); Array.Copy(prefix, this.offset, buffer, index, length); this.offset += length; return length;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int index, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (this.offset >= prefix.Length) return await inner.ReadAsync(buffer, index, count, cancellationToken).ConfigureAwait(false);
            return this.Read(buffer, index, count);
        }
#if NET6_0_OR_GREATER
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (this.offset >= prefix.Length) return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            var length = Math.Min(buffer.Length, prefix.Length - this.offset); prefix.AsMemory(this.offset, length).CopyTo(buffer); this.offset += length; return length;
        }
#endif
        protected override void Dispose(bool disposing) { if (disposing) { try { inner.Dispose(); } finally { owner?.Dispose(); } } base.Dispose(disposing); }
    }

    internal sealed class LiveStreamResources : IDisposable
    {
        private readonly IDisposable response;
        private readonly IDisposable client;
        internal LiveStreamResources(IDisposable response, IDisposable client) { this.response = response; this.client = client; }
        public void Dispose() { try { response.Dispose(); } finally { client.Dispose(); } }
    }
}
