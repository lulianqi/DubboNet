using DubboNet.DubboService.DataModle;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DubboNet.DubboService.Native
{
    /// <summary>
    /// 维护一个可复用的 Dubbo TCP 长连接，按 request id 复用并发请求并分发响应。
    /// <para>EN: Maintains a reusable Dubbo TCP connection, multiplexing concurrent calls and dispatching responses by request id.</para>
    /// </summary>
    internal sealed class NativeDubboConnection : IDisposable
    {
        private readonly IPEndPoint _endPoint;
        private readonly int _requestTimeoutMilliseconds;
        private readonly int _maxPayloadLength;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<NativeDubboFrame>> _pending =
            new ConcurrentDictionary<long, TaskCompletionSource<NativeDubboFrame>>();
        private readonly SemaphoreSlim _connectGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly object _stateLock = new object();

        private TcpClient _client;
        private NetworkStream _stream;
        private long _nextRequestId;
        private volatile bool _disposed;

        /// <summary>
        /// 创建到一个 provider 节点的惰性连接。
        /// <para>EN: Creates a lazy connection to one provider endpoint.</para>
        /// </summary>
        public NativeDubboConnection(
            IPEndPoint endPoint,
            int requestTimeoutMilliseconds,
            int maxPayloadLength = NativeDubboCodec.DefaultMaxPayloadLength)
        {
            _endPoint = endPoint ?? throw new ArgumentNullException(nameof(endPoint));
            _requestTimeoutMilliseconds = requestTimeoutMilliseconds > 0
                ? requestTimeoutMilliseconds
                : 10_000;
            _maxPayloadLength = maxPayloadLength > 0
                ? maxPayloadLength
                : NativeDubboCodec.DefaultMaxPayloadLength;
        }

        /// <summary>
        /// 编码并发送一次泛化调用，然后等待相同 request id 的响应。
        /// <para>EN: Encodes and sends a generic invocation, then waits for the response with the same request id.</para>
        /// </summary>
        public async Task<NativeDubboResponse> InvokeAsync(
            DubboInvocation invocation,
            string serviceVersion,
            string group,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            using CancellationTokenSource requestCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            requestCancellation.CancelAfter(_requestTimeoutMilliseconds);

            await EnsureConnectedAsync(requestCancellation.Token).ConfigureAwait(false);
            (TcpClient client, NetworkStream stream) = GetConnectionSnapshot();

            long requestId = Interlocked.Increment(ref _nextRequestId);
            byte[] request = NativeDubboCodec.EncodeGenericRequest(
                requestId,
                invocation,
                serviceVersion,
                group,
                _requestTimeoutMilliseconds);

            TaskCompletionSource<NativeDubboFrame> completion =
                new TaskCompletionSource<NativeDubboFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(requestId, completion))
            {
                throw new InvalidOperationException($"Duplicate Dubbo request id {requestId}.");
            }

            using CancellationTokenRegistration registration = requestCancellation.Token.Register(() =>
            {
                if (_pending.TryRemove(requestId, out TaskCompletionSource<NativeDubboFrame> pending))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        pending.TrySetCanceled(cancellationToken);
                    }
                    else if (_lifetime.IsCancellationRequested)
                    {
                        pending.TrySetException(new ObjectDisposedException(nameof(NativeDubboConnection)));
                    }
                    else
                    {
                        pending.TrySetException(new TimeoutException(
                            $"Dubbo request {requestId} to {_endPoint} timed out after " +
                            $"{_requestTimeoutMilliseconds} ms."));
                    }
                }
            });

            bool writeAttempted = false;
            try
            {
                await _sendGate.WaitAsync(requestCancellation.Token).ConfigureAwait(false);
                try
                {
                    writeAttempted = true;
                    await stream.WriteAsync(request, requestCancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    _sendGate.Release();
                }
            }
            catch (Exception exception)
            {
                _pending.TryRemove(requestId, out _);
                if (writeAttempted)
                {
                    // Cancellation or failure during a frame write can leave a partial frame on the wire.
                    FaultConnection(client, exception);
                }
                throw;
            }

            NativeDubboFrame frame = await completion.Task.ConfigureAwait(false);
            return NativeDubboCodec.DecodeResponse(frame);
        }

        private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            if (HasConnection())
            {
                return;
            }

            await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (HasConnection())
                {
                    return;
                }

                TcpClient client = new TcpClient(_endPoint.AddressFamily)
                {
                    NoDelay = true
                };
                try
                {
                    await client.ConnectAsync(
                        _endPoint.Address,
                        _endPoint.Port,
                        cancellationToken).ConfigureAwait(false);
                    NetworkStream stream = client.GetStream();
                    lock (_stateLock)
                    {
                        ThrowIfDisposed();
                        _client = client;
                        _stream = stream;
                    }
                    _ = Task.Run(
                        () => ReceiveLoopAsync(client, stream, _lifetime.Token),
                        CancellationToken.None);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            }
            finally
            {
                _connectGate.Release();
            }
        }

        private async Task ReceiveLoopAsync(
            TcpClient client,
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    byte[] header = new byte[NativeDubboCodec.HeaderLength];
                    await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);

                    int payloadLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(12, 4));
                    if (payloadLength < 0 || payloadLength > _maxPayloadLength)
                    {
                        throw new InvalidDataException(
                            $"Dubbo payload length {payloadLength} is outside the allowed range " +
                            $"0..{_maxPayloadLength}.");
                    }

                    byte[] body = new byte[payloadLength];
                    await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false);
                    NativeDubboFrame frame = NativeDubboCodec.DecodeFrameHeader(header, body);

                    // Server initiated/event requests are not RPC responses and cannot complete a caller.
                    if (frame.IsRequest)
                    {
                        if (frame.IsEvent && frame.IsTwoWay)
                        {
                            byte[] heartbeatResponse = NativeDubboCodec.EncodeHeartbeatResponse(
                                frame.RequestId,
                                frame.SerializationId);
                            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                            try
                            {
                                await stream.WriteAsync(
                                    heartbeatResponse,
                                    cancellationToken).ConfigureAwait(false);
                            }
                            finally
                            {
                                _sendGate.Release();
                            }
                        }
                        continue;
                    }
                    if (_pending.TryRemove(
                        frame.RequestId,
                        out TaskCompletionSource<NativeDubboFrame> completion))
                    {
                        completion.TrySetResult(frame);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                FaultConnection(client, exception);
            }
        }

        private static async Task ReadExactlyAsync(
            Stream stream,
            byte[] buffer,
            CancellationToken cancellationToken)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int count = await stream.ReadAsync(
                    buffer.AsMemory(read, buffer.Length - read),
                    cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new EndOfStreamException("The Dubbo provider closed the TCP connection.");
                }
                read += count;
            }
        }

        private bool HasConnection()
        {
            lock (_stateLock)
            {
                return !_disposed && _client != null && _stream != null;
            }
        }

        private (TcpClient Client, NetworkStream Stream) GetConnectionSnapshot()
        {
            lock (_stateLock)
            {
                ThrowIfDisposed();
                if (_client == null || _stream == null)
                {
                    throw new IOException($"Dubbo connection to {_endPoint} is not available.");
                }
                return (_client, _stream);
            }
        }

        private void FaultConnection(TcpClient source, Exception exception)
        {
            bool ownsCurrentConnection;
            lock (_stateLock)
            {
                ownsCurrentConnection = ReferenceEquals(_client, source);
                if (ownsCurrentConnection)
                {
                    _stream = null;
                    _client = null;
                }
            }

            source?.Dispose();
            if (!ownsCurrentConnection)
            {
                return;
            }

            foreach (var item in _pending)
            {
                if (_pending.TryRemove(item.Key, out TaskCompletionSource<NativeDubboFrame> pending))
                {
                    pending.TrySetException(exception);
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NativeDubboConnection));
            }
        }

        /// <summary>
        /// 关闭连接并使所有等待中的请求失败。
        /// <para>EN: Closes the connection and faults all pending requests.</para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _lifetime.Cancel();

            TcpClient client;
            lock (_stateLock)
            {
                client = _client;
                _client = null;
                _stream = null;
            }
            client?.Dispose();

            ObjectDisposedException exception = new ObjectDisposedException(nameof(NativeDubboConnection));
            foreach (var item in _pending)
            {
                if (_pending.TryRemove(item.Key, out TaskCompletionSource<NativeDubboFrame> pending))
                {
                    pending.TrySetException(exception);
                }
            }
            _lifetime.Dispose();
        }
    }
}
