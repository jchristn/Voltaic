namespace Test.Shared
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Net.WebSockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A raw channel over a WebSocket: one text message per JSON-RPC message or batch.
    /// </summary>
    internal sealed class McpRawWebSocketChannel : IMcpRawChannel
    {
        private readonly ClientWebSocket _Socket;
        private readonly BlockingCollection<string> _Received = new BlockingCollection<string>();
        private readonly SemaphoreSlim _SendLock = new SemaphoreSlim(1, 1);

        private McpRawWebSocketChannel(ClientWebSocket socket)
        {
            _Socket = socket;
            _ = Task.Run(async () =>
            {
                byte[] buffer = new byte[65536];
                try
                {
                    using MemoryStream message = new MemoryStream();
                    while (_Socket.State == WebSocketState.Open)
                    {
                        WebSocketReceiveResult result = await _Socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        message.Write(buffer, 0, result.Count);
                        if (!result.EndOfMessage) continue;
                        _Received.Add(Encoding.UTF8.GetString(message.ToArray()));
                        message.SetLength(0);
                    }
                }
                catch
                {
                }
                finally
                {
                    _Received.CompleteAdding();
                }
            });
        }

        public string Transport => "websocket";

        public string? ProtocolVersion { get; set; }

        public bool Stateless { get; set; }

        /// <summary>
        /// Connects to a WebSocket MCP server.
        /// </summary>
        public static async Task<McpRawWebSocketChannel> ConnectAsync(string url, CancellationToken token)
        {
            ClientWebSocket socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(url), token).ConfigureAwait(false);
            return new McpRawWebSocketChannel(socket);
        }

        public async Task SendAsync(string json, CancellationToken token)
        {
            await _SendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await _Socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
            finally
            {
                _SendLock.Release();
            }
        }

        public string? Receive(TimeSpan timeout)
        {
            try
            {
                return _Received.TryTake(out string? message, timeout) ? message : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using CancellationTokenSource close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", close.Token).ConfigureAwait(false);
            }
            catch
            {
            }

            _Socket.Dispose();
            _SendLock.Dispose();
        }
    }
}
