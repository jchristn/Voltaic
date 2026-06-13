namespace Test.Shared
{
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Net.WebSockets;
    using System.Text;
    using System.Text.Json;
    using Voltaic;

    internal static class TestPorts
    {
        public static int GetFreePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    internal static class TestJson
    {
        public static JsonElement SerializeToElement(object value)
        {
            return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value));
        }

        public static JsonElement ParseRoot(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        public static bool ContainsProperty(object value, string propertyName)
        {
            JsonElement element = SerializeToElement(value);
            return element.TryGetProperty(propertyName, out _);
        }

        public static string Serialize(object value)
        {
            return JsonSerializer.Serialize(value);
        }
    }

    internal sealed class HttpMcpTestServerFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _TokenSource;
        private readonly Task _ServerTask;

        private HttpMcpTestServerFixture(McpHttpServer server, int port, CancellationTokenSource tokenSource, Task serverTask)
        {
            Server = server;
            Port = port;
            _TokenSource = tokenSource;
            _ServerTask = serverTask;
            Client = new HttpClient();
        }

        public McpHttpServer Server { get; }

        public int Port { get; }

        public HttpClient Client { get; }

        public string BaseUrl => $"http://localhost:{Port}";

        public static async Task<HttpMcpTestServerFixture> StartAsync(
            CancellationToken token,
            Action<McpHttpServer>? configure = null,
            bool includeDefaultMethods = true,
            string hostname = "localhost",
            string rpcPath = "/rpc",
            string eventsPath = "/events",
            string? mcpPath = "/mcp")
        {
            int port = TestPorts.GetFreePort();
            McpHttpServer server = new McpHttpServer(hostname, port, rpcPath, eventsPath, includeDefaultMethods, mcpPath);
            server.ServerName = "Voltaic.Test";
            server.ServerVersion = "0.3.0-test";
            configure?.Invoke(server);

            CancellationTokenSource tokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task serverTask = Task.Run(() => server.StartAsync(tokenSource.Token), CancellationToken.None);

            HttpMcpTestServerFixture fixture = new HttpMcpTestServerFixture(server, port, tokenSource, serverTask);
            await fixture.WaitUntilReadyAsync(token).ConfigureAwait(false);
            return fixture;
        }

        public async Task<RpcResult> PostMcpAsync(string method, object? parameters, object? id, string? sessionId, CancellationToken token)
        {
            return await PostJsonRpcAsync("/mcp/", method, parameters, id, sessionId, token).ConfigureAwait(false);
        }

        public async Task<RpcResult> PostRpcAsync(string method, object? parameters, object? id, string? sessionId, CancellationToken token)
        {
            return await PostJsonRpcAsync("/rpc/", method, parameters, id, sessionId, token).ConfigureAwait(false);
        }

        public async Task<RpcResult> PostJsonRpcAsync(
            string path,
            string method,
            object? parameters,
            object? id,
            string? sessionId,
            CancellationToken token,
            bool addMcpAcceptHeaders = true)
        {
            JsonRpcRequest request = new JsonRpcRequest
            {
                Method = method,
                Params = parameters,
                Id = id
            };

            using HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{path}")
            {
                Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json")
            };

            if (addMcpAcceptHeaders)
            {
                message.Headers.Accept.ParseAdd("application/json");
                message.Headers.Accept.ParseAdd("text/event-stream");
            }

            if (!String.IsNullOrEmpty(sessionId))
            {
                message.Headers.Add(McpProtocol.SessionIdHeader, sessionId);
            }

            using HttpResponseMessage response = await Client.SendAsync(message, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            string? returnedSessionId = response.Headers.TryGetValues(McpProtocol.SessionIdHeader, out IEnumerable<string>? values)
                ? values.FirstOrDefault()
                : null;

            return new RpcResult(response.StatusCode, body, returnedSessionId, response.Content.Headers.ContentType?.MediaType);
        }

        public async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken token)
        {
            return await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            Server.Stop();
            _TokenSource.Cancel();
            Client.Dispose();

            await Task.WhenAny(_ServerTask, Task.Delay(1000)).ConfigureAwait(false);
            _TokenSource.Dispose();
            Server.Dispose();
        }

        private async Task WaitUntilReadyAsync(CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using HttpResponseMessage response = await Client.GetAsync($"{BaseUrl}/", token).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return;
                    }
                }
                catch
                {
                }

                await Task.Delay(50, token).ConfigureAwait(false);
            }

            throw new TimeoutException("HTTP MCP test server did not become ready.");
        }
    }

    internal sealed record RpcResult(HttpStatusCode StatusCode, string Body, string? SessionId, string? ContentType)
    {
        public JsonElement Root => TestJson.ParseRoot(Body);

        public JsonElement Result => Root.GetProperty("result");

        public JsonElement Error => Root.GetProperty("error");
    }

    internal sealed class TcpJsonRpcFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _TokenSource;
        private readonly Task _ServerTask;

        private TcpJsonRpcFixture(JsonRpcServer server, int port, CancellationTokenSource tokenSource, Task serverTask)
        {
            Server = server;
            Port = port;
            _TokenSource = tokenSource;
            _ServerTask = serverTask;
        }

        public JsonRpcServer Server { get; }

        public int Port { get; }

        public static async Task<TcpJsonRpcFixture> StartAsync(
            CancellationToken token,
            Action<JsonRpcServer>? configure = null,
            bool includeDefaultMethods = true)
        {
            int port = TestPorts.GetFreePort();
            JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, port, includeDefaultMethods);
            configure?.Invoke(server);

            return await StartServerAsync(server, port, token).ConfigureAwait(false);
        }

        public static async Task<TcpJsonRpcFixture> StartMcpTcpAsync(
            CancellationToken token,
            Action<McpTcpServer>? configure = null,
            bool includeDefaultMethods = true)
        {
            int port = TestPorts.GetFreePort();
            McpTcpServer server = new McpTcpServer(IPAddress.Loopback, port, includeDefaultMethods);
            configure?.Invoke(server);

            return await StartServerAsync(server, port, token).ConfigureAwait(false);
        }

        private static async Task<TcpJsonRpcFixture> StartServerAsync(JsonRpcServer server, int port, CancellationToken token)
        {

            CancellationTokenSource tokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task serverTask = Task.Run(() => server.StartAsync(tokenSource.Token), CancellationToken.None);
            TcpJsonRpcFixture fixture = new TcpJsonRpcFixture(server, port, tokenSource, serverTask);
            await fixture.WaitUntilReadyAsync(token).ConfigureAwait(false);
            return fixture;
        }

        public async Task<JsonRpcClient> ConnectClientAsync(CancellationToken token)
        {
            JsonRpcClient client = new JsonRpcClient();
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (await client.ConnectAsync("127.0.0.1", Port, token).ConfigureAwait(false))
                {
                    return client;
                }

                await Task.Delay(50, token).ConfigureAwait(false);
            }

            client.Dispose();
            throw new TimeoutException("TCP JSON-RPC client could not connect to test server.");
        }

        public async ValueTask DisposeAsync()
        {
            Server.Stop();
            _TokenSource.Cancel();
            await Task.WhenAny(_ServerTask, Task.Delay(1000)).ConfigureAwait(false);
            _TokenSource.Dispose();
            Server.Dispose();
        }

        private async Task WaitUntilReadyAsync(CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                using TcpClient probe = new TcpClient();
                try
                {
                    await probe.ConnectAsync(IPAddress.Loopback, Port, token).ConfigureAwait(false);
                    probe.Close();
                    while (DateTime.UtcNow < deadline && Server.GetConnectedClients().Count > 0)
                    {
                        await Task.Delay(10, token).ConfigureAwait(false);
                    }

                    return;
                }
                catch
                {
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
            }

            throw new TimeoutException("TCP JSON-RPC test server did not become ready.");
        }
    }

    internal sealed class WebSocketMcpFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _TokenSource;
        private readonly Task _ServerTask;

        private WebSocketMcpFixture(McpWebsocketsServer server, int port, CancellationTokenSource tokenSource, Task serverTask)
        {
            Server = server;
            Port = port;
            _TokenSource = tokenSource;
            _ServerTask = serverTask;
        }

        public McpWebsocketsServer Server { get; }

        public int Port { get; }

        public string Url => $"ws://localhost:{Port}/mcp/";

        public static async Task<WebSocketMcpFixture> StartAsync(
            CancellationToken token,
            Action<McpWebsocketsServer>? configure = null,
            bool includeDefaultMethods = true)
        {
            int port = TestPorts.GetFreePort();
            McpWebsocketsServer server = new McpWebsocketsServer("localhost", port, "/mcp", includeDefaultMethods);
            configure?.Invoke(server);

            CancellationTokenSource tokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task serverTask = Task.Run(() => server.StartAsync(tokenSource.Token), CancellationToken.None);
            WebSocketMcpFixture fixture = new WebSocketMcpFixture(server, port, tokenSource, serverTask);
            await Task.Delay(100, token).ConfigureAwait(false);
            return fixture;
        }

        public async Task<McpWebsocketsClient> ConnectClientAsync(CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                McpWebsocketsClient client = new McpWebsocketsClient();
                if (await client.ConnectAsync(Url, token).ConfigureAwait(false))
                {
                    return client;
                }

                client.Dispose();
                await Task.Delay(50, token).ConfigureAwait(false);
            }

            throw new TimeoutException("WebSocket MCP client could not connect to test server.");
        }

        public async ValueTask DisposeAsync()
        {
            Server.Stop();
            _TokenSource.Cancel();
            await Task.WhenAny(_ServerTask, Task.Delay(1000)).ConfigureAwait(false);
            _TokenSource.Dispose();
            Server.Dispose();
        }
    }

    internal sealed class TestWebSocket : WebSocket
    {
        private WebSocketState _State = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => _State;

        public override string? SubProtocol => null;

        public bool Disposed { get; private set; }

        public override void Abort()
        {
            _State = WebSocketState.Aborted;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _State = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _State = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            Disposed = true;
            _State = WebSocketState.Closed;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
