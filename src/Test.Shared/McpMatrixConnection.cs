namespace Test.Shared
{
    using System;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Mcp;

    /// <summary>
    /// A server serving <see cref="McpMatrixFixture"/> on one transport, with a raw channel connected to it.
    /// </summary>
    internal sealed class McpMatrixConnection : IAsyncDisposable
    {
        private readonly IAsyncDisposable? _Server;

        private McpMatrixConnection(IMcpRawChannel channel, IAsyncDisposable? server)
        {
            Channel = channel;
            _Server = server;
        }

        /// <summary>
        /// Gets the raw channel.
        /// </summary>
        public IMcpRawChannel Channel { get; }

        /// <summary>
        /// Starts a fixture server on the transport and connects a raw channel to it.
        /// </summary>
        public static async Task<McpMatrixConnection> OpenAsync(string transport, CancellationToken token)
        {
            switch (transport)
            {
                case "stdio":
                    return new McpMatrixConnection(McpRawStreamChannel.StartStdio("--matrix"), null);
                case "tcp":
                    TcpJsonRpcFixture tcp = await TcpJsonRpcFixture.StartMcpTcpAsync(token, s =>
                    {
                        s.PingIntervalMs = 0;
                        McpMatrixFixture.Configure(s.RegisterTool, s.RegisterResource, s.RegisterPrompt);
                    }).ConfigureAwait(false);
                    return new McpMatrixConnection(await McpRawStreamChannel.ConnectTcpAsync(tcp.Port, token).ConfigureAwait(false), tcp);
                case "websocket":
                    WebSocketMcpFixture ws = await WebSocketMcpFixture.StartAsync(token, s =>
                    {
                        s.PingIntervalMs = 0;
                        McpMatrixFixture.Configure(s.RegisterTool, s.RegisterResource, s.RegisterPrompt);
                    }).ConfigureAwait(false);
                    return new McpMatrixConnection(await McpRawWebSocketChannel.ConnectAsync(ws.Url, token).ConfigureAwait(false), ws);
                case "http":
                    HttpMcpTestServerFixture http = await HttpMcpTestServerFixture.StartAsync(token, s =>
                    {
                        McpMatrixFixture.Configure(s.RegisterTool, s.RegisterResource, s.RegisterPrompt);
                    }).ConfigureAwait(false);
                    return new McpMatrixConnection(new McpRawHttpChannel(http.BaseUrl + "/mcp"), http);
                default:
                    throw new ArgumentException($"Unknown transport '{transport}'.", nameof(transport));
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Channel.DisposeAsync().ConfigureAwait(false);
            if (_Server != null) await _Server.DisposeAsync().ConfigureAwait(false);
        }
    }
}
