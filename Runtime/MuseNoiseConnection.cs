// Adapted from wong2/muse-client and Meta Muse Gadget SDK; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Muse.Unity
{
    public sealed class MuseBodyQueue
    {
        private readonly object gate = new object();
        private readonly Queue<byte[]> chunks = new Queue<byte[]>();
        private TaskCompletionSource<bool> changed = NewSignal();
        private bool ended;
        private Exception error;
        private int size;
        private static TaskCompletionSource<bool> NewSignal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Push(byte[] data)
        {
            lock (gate)
            {
                if (ended || data.Length == 0) return;
                if (size + data.Length > 4 * 1024 * 1024) throw new MuseException("MUSE_RESPONSE_BUFFER_FULL");
                chunks.Enqueue(data); size += data.Length; changed.TrySetResult(true);
            }
        }
        public void End(Exception reason = null)
        { lock (gate) { ended = true; error = reason; changed.TrySetResult(true); } }
        // null marks EOF. A failed stream never silently returns success.
        public async Task<byte[]> ReadAsync(CancellationToken cancellation)
        {
            while (true)
            {
                Task wait;
                lock (gate)
                {
                    if (error != null) throw error;
                    if (chunks.Count > 0) { var data = chunks.Dequeue(); size -= data.Length; return data; }
                    if (ended) return null;
                    if (changed.Task.IsCompleted) changed = NewSignal(); wait = changed.Task;
                }
                var cancelled = NewSignal();
                using (cancellation.Register(() => cancelled.TrySetCanceled()))
                { await await Task.WhenAny(wait, cancelled.Task).ConfigureAwait(false); }
            }
        }
    }

    public sealed class MuseResponse : IDisposable
    {
        public readonly int Status;
        public readonly MuseBodyQueue Body;
        private readonly Action close;
        public MuseResponse(int status, MuseBodyQueue body, Action close) { Status = status; Body = body; this.close = close; }
        public void Dispose() => close();
    }

    public sealed class MuseNoiseConnection : IDisposable
    {
        private sealed class Pending
        {
            public readonly MuseBodyQueue Body = new MuseBodyQueue();
            public readonly TaskCompletionSource<int> Headers = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Status;
            public Timer Timer;
            public CancellationTokenRegistration Registration;
            public void Clean() { Timer?.Dispose(); Registration.Dispose(); }
        }
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private readonly SemaphoreSlim sender = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly object gate = new object();
        private readonly Dictionary<int, Pending> pending = new Dictionary<int, Pending>();
        private readonly MuseFrameAssembler assembler = new MuseFrameAssembler();
        private MuseCipher sendCipher, receiveCipher;
        private int nextId;
        private bool dead;
        public bool IsConnected { get { lock (gate) return !dead && socket.State == WebSocketState.Open; } }

        public static async Task<MuseNoiseConnection> ConnectAsync(Uri uri, string token, CancellationToken cancellation)
        {
            if (uri.Scheme != "wss" || !string.IsNullOrEmpty(uri.UserInfo)) throw new MuseException("MUSE_REQUIRES_TLS");
            return await ConnectCoreAsync(uri, token, cancellation).ConfigureAwait(false);
        }
#if UNITY_EDITOR
        // Editor-only independent protocol-fixture endpoint. Never compiled into a player.
        public static Task<MuseNoiseConnection> ConnectLoopbackFixtureAsync(Uri uri, CancellationToken cancellation)
        {
            if (!uri.IsLoopback || uri.Scheme != "ws") throw new MuseException("LOOPBACK_FIXTURE_ONLY");
            return ConnectCoreAsync(uri, "isolated-test-only", cancellation);
        }
#endif
        private static async Task<MuseNoiseConnection> ConnectCoreAsync(Uri uri, string token, CancellationToken cancellation)
        {
            var connection = new MuseNoiseConnection();
            try
            {
                connection.socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
                connection.socket.Options.SetRequestHeader("User-Agent", "muse-unity/0.1.0-preview.1");
                // ClientWebSocket manages WebSocket keepalives; no application data is fabricated.
                connection.socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(20000);
                await connection.socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
                using var handshake = new MuseNoiseHandshake();
                await connection.socket.SendAsync(new ArraySegment<byte>(handshake.Message1()), WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(false);
                handshake.ReceiveMessage2(await connection.ReceiveMessageAsync(timeout.Token).ConfigureAwait(false));
                var keys = handshake.Finish(); connection.sendCipher = keys.send; connection.receiveCipher = keys.receive;
                await connection.socket.SendAsync(new ArraySegment<byte>(keys.message), WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(false);
                _ = connection.ReadLoopAsync(); return connection;
            }
            catch { connection.Dispose(); throw new MuseException("MUSE_CONNECT_FAILED"); }
        }

        private async Task<byte[]> ReceiveMessageAsync(CancellationToken cancellation)
        {
            var buffer = new byte[65536]; using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation).ConfigureAwait(false);
                if (received.MessageType != WebSocketMessageType.Binary) throw new MuseException("MUSE_BINARY_FRAME_REQUIRED");
                if (message.Length + received.Count > 4 * 1024 * 1024) throw new MuseException("MUSE_FRAME_TOO_LARGE");
                message.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);
            return message.ToArray();
        }
        private async Task ReadLoopAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var encrypted = await ReceiveMessageAsync(lifetime.Token).ConfigureAwait(false);
                    var data = assembler.Decode(receiveCipher.Decrypt(encrypted)); if (data == null) continue;
                    var frame = MuseWire.DecodeResponse(data); Pending request;
                    lock (gate) pending.TryGetValue(frame.StreamId, out request);
                    if (request == null) continue;
                    if (frame.Kind == "reset") { Finish(frame.StreamId, new MuseException("MUSE_STREAM_RESET")); continue; }
                    if (frame.Kind == "response")
                    {
                        if (request.Status != 0) throw new MuseException("MUSE_DUPLICATE_HEADERS");
                        request.Status = frame.Status; request.Timer?.Dispose(); request.Headers.TrySetResult(frame.Status);
                    }
                    else if (request.Status == 0) throw new MuseException("MUSE_BODY_BEFORE_HEADERS");
                    request.Body.Push(frame.Body); if (frame.End) Finish(frame.StreamId);
                }
            }
            catch { Fail(new MuseException("MUSE_CONNECTION_INTERRUPTED")); }
        }
        private async Task TransmitAsync(byte[] data, CancellationToken cancellation)
        {
            await sender.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (!IsConnected || sendCipher == null) throw new MuseException("MUSE_NOT_CONNECTED");
                // Cipher nonce increments and WebSocket sends share this lock across all streams.
                foreach (var part in MuseWire.Chunks(data))
                {
                    var encrypted = sendCipher.Encrypt(part);
                    await socket.SendAsync(new ArraySegment<byte>(encrypted), WebSocketMessageType.Binary, true, lifetime.Token).ConfigureAwait(false);
                }
            }
            catch { Fail(new MuseException("MUSE_SEND_FAILED")); throw; }
            finally { sender.Release(); }
        }
        public async Task<MuseResponse> OpenAsync(string path, byte[] body, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (body.Length > 256 * 1024 - 1024) throw new MuseException("MUSE_REQUEST_TOO_LARGE");
            var request = new Pending(); int id;
            lock (gate)
            {
                if (dead) throw new MuseException("MUSE_NOT_CONNECTED");
                if (pending.Count >= 64 || nextId == int.MaxValue) throw new MuseException("MUSE_REQUEST_LIMIT");
                id = ++nextId; pending.Add(id, request);
            }
            request.Timer = new Timer(_ => CancelStream(id, new MuseException("MUSE_HEADERS_TIMEOUT")), null, 20000, Timeout.Infinite);
            request.Registration = cancellation.Register(() => CancelStream(id, new OperationCanceledException()));
            try
            {
                cancellation.ThrowIfCancellationRequested();
                await TransmitAsync(MuseWire.Request(id, path, body), cancellation).ConfigureAwait(false);
                int status = await request.Headers.Task.ConfigureAwait(false);
                return new MuseResponse(status, request.Body, () => CancelStream(id, new MuseException("MUSE_REQUEST_CANCELLED")));
            }
            catch { Finish(id, new MuseException("MUSE_REQUEST_FAILED")); request.Clean(); throw; }
        }
        private void CancelStream(int id, Exception error)
        {
            if (!Finish(id, error)) return;
            _ = ResetAsync(id);
        }
        private async Task ResetAsync(int id)
        { try { await TransmitAsync(MuseWire.Reset(id), lifetime.Token).ConfigureAwait(false); } catch { } }
        private bool Finish(int id, Exception error = null)
        {
            Pending request;
            lock (gate) { if (!pending.TryGetValue(id, out request)) return false; pending.Remove(id); }
            request.Clean();
            if (error != null) request.Headers.TrySetException(error);
            else if (request.Status == 0) request.Headers.TrySetException(new MuseException("MUSE_EOF_BEFORE_HEADERS"));
            request.Body.End(error); return true;
        }
        private void Fail(Exception error)
        {
            int[] ids;
            lock (gate) { if (dead) return; dead = true; ids = new int[pending.Count]; pending.Keys.CopyTo(ids, 0); }
            lifetime.Cancel(); socket.Abort(); foreach (int id in ids) Finish(id, error);
            // Read/send operations can still unwind: do not dispose their synchronization objects here.
        }
        public void Dispose() { Fail(new MuseException("MUSE_DISCONNECTED")); }
    }
}
