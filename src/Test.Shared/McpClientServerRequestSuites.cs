namespace Test.Shared
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Net.WebSockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers requests a server sends to a client. Every Voltaic client answers them, as JSON-RPC and MCP require:
    /// <c>ping</c> with <c>{}</c>, registered methods through <c>RegisterRequestHandler</c>, and anything else with
    /// <c>-32601</c>. Also covers <see cref="McpHttpClient"/> SSE handling: event IDs, priming events,
    /// reconnection with <c>Last-Event-ID</c>, <c>retry</c>, and resuming an interrupted POST response stream.
    /// </summary>
    public static class McpClientServerRequestSuites
    {
        private static readonly TimeSpan _Wait = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Client-side server-request and SSE cases.
        /// </summary>
        public static TestSuiteDescriptor ServerRequests()
        {
            const string suiteId = "McpClients.ServerRequests";
            return new TestSuiteDescriptor(
                suiteId,
                "MCP clients answer server requests and resume SSE streams",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "StdioClientAnswersServerRequests", "McpClient (stdio) answers ping with {}, a registered method with its result, and an unknown method with -32601", async ct =>
                    {
                        using McpClient client = new McpClient();
                        ConcurrentQueue<string> echoes = new ConcurrentQueue<string>();
                        client.NotificationReceived += (sender, notification) =>
                        {
                            if (notification.Method == "notifications/echo") echoes.Enqueue(JsonSerializer.Serialize(notification.Params));
                        };
                        client.RegisterRequestHandler("roots/list", (args, token) => Task.FromResult<object?>(new { roots = new[] { new { uri = "file:///work", name = "work" } } }));

                        await McpStdioIntegrationSuites.LaunchTestServerAsync(client, ct, "--probe-client").ConfigureAwait(false);
                        await client.NotifyAsync("probe/start", null, ct).ConfigureAwait(false);
                        TestAssert.True(await WaitUntilAsync(() => echoes.Count >= 3, ct).ConfigureAwait(false), $"The client answered all three requests (got {echoes.Count}).");

                        Dictionary<string, JsonProbe> byId = echoes.Select(TestJson.ParseRoot).ToDictionary(probe => probe.Get("id").String()!, probe => probe);
                        TestAssert.True(byId["srv-ping"].Get("result").IsObject && byId["srv-ping"].Get("result").Length == 0, "ping is answered with {}.");
                        TestAssert.Equal("file:///work", byId["srv-roots"].Get("result").Get("roots")[0].Get("uri").String(), "roots/list is answered by the handler.");
                        TestAssert.Equal(-32601, byId["srv-unknown"].Get("error").Get("code").Int(), "An unknown method is answered with -32601.");
                        client.Shutdown();
                    }),

                    Case(suiteId, "RequestHandlerRegistrationValidates", "RegisterRequestHandler rejects ping, empty names, and null handlers; UnregisterRequestHandler reports whether a handler existed", ct =>
                    {
                        Func<RpcParameters?, CancellationToken, Task<object?>> handler = (args, token) => Task.FromResult<object?>(null);
                        using McpClient stdio = new McpClient();
                        using McpWebsocketsClient websocket = new McpWebsocketsClient();
                        using McpHttpClient http = new McpHttpClient();
                        using McpTcpClient tcp = new McpTcpClient();
                        using JsonRpcClient plain = new JsonRpcClient();

                        TestAssert.Throws<ArgumentException>(() => stdio.RegisterRequestHandler("ping", handler), "stdio reserves ping.");
                        TestAssert.Throws<ArgumentException>(() => websocket.RegisterRequestHandler("ping", handler), "WebSocket reserves ping.");
                        TestAssert.Throws<ArgumentException>(() => http.RegisterRequestHandler("ping", handler), "HTTP reserves ping.");
                        TestAssert.Throws<ArgumentException>(() => tcp.RegisterRequestHandler("ping", handler), "TCP (MCP) reserves ping.");
                        plain.RegisterRequestHandler("ping", handler);
                        TestAssert.Throws<ArgumentNullException>(() => stdio.RegisterRequestHandler(" ", handler), "An empty method is rejected.");
                        TestAssert.Throws<ArgumentNullException>(() => http.RegisterRequestHandler("roots/list", null!), "A null handler is rejected.");

                        stdio.RegisterRequestHandler("roots/list", handler);
                        TestAssert.True(stdio.UnregisterRequestHandler("roots/list"), "An existing handler is removed.");
                        TestAssert.False(stdio.UnregisterRequestHandler("roots/list"), "Removing again reports false.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "TcpClientAnswersServerRequests", "McpTcpClient answers ping, maps a handler's McpProtocolException to its error and other exceptions to -32603 without details; a plain JsonRpcClient has no built-in ping", async ct =>
                    {
                        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                        listener.Start();
                        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                        try
                        {
                            using McpTcpClient client = new McpTcpClient();
                            client.RegisterRequestHandler("sampling/createMessage", (args, token) => throw McpProtocolException.InvalidParams("bad sampling request"));
                            client.RegisterRequestHandler("boom", (args, token) => throw new InvalidOperationException("secret detail"));
                            Task<TcpClient> accepting = listener.AcceptTcpClientAsync(ct).AsTask();
                            TestAssert.True(await client.ConnectAsync("127.0.0.1", port, ct).ConfigureAwait(false), "The client connects.");
                            using TcpClient server = await accepting.ConfigureAwait(false);
                            NetworkStream stream = server.GetStream();

                            await WriteFramedAsync(stream, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", ct).ConfigureAwait(false);
                            await WriteFramedAsync(stream, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"sampling/createMessage\",\"params\":{}}", ct).ConfigureAwait(false);
                            await WriteFramedAsync(stream, "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"boom\"}", ct).ConfigureAwait(false);
                            await WriteFramedAsync(stream, "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"nope\"}", ct).ConfigureAwait(false);

                            Dictionary<int, JsonProbe> answers = new Dictionary<int, JsonProbe>();
                            List<string> rawAnswers = new List<string>();
                            for (int i = 0; i < 4; i++)
                            {
                                string raw = await ReadFramedAsync(stream, ct).ConfigureAwait(false);
                                rawAnswers.Add(raw);
                                JsonProbe answer = TestJson.ParseRoot(raw);
                                answers[answer.Get("id").Int()] = answer;
                            }

                            TestAssert.True(answers[1].Get("result").IsObject && answers[1].Get("result").Length == 0, "ping is answered with {}.");
                            TestAssert.Equal(-32602, answers[2].Get("error").Get("code").Int(), "The handler's protocol error is sent.");
                            TestAssert.Equal(-32603, answers[3].Get("error").Get("code").Int(), "Other exceptions become -32603.");
                            TestAssert.False(rawAnswers.Any(raw => raw.Contains("secret detail")), "Exception details are not sent.");
                            TestAssert.Equal(-32601, answers[4].Get("error").Get("code").Int(), "Unknown methods get -32601.");

                            using JsonRpcClient plain = new JsonRpcClient();
                            Task<TcpClient> acceptingPlain = listener.AcceptTcpClientAsync(ct).AsTask();
                            TestAssert.True(await plain.ConnectAsync("127.0.0.1", port, ct).ConfigureAwait(false), "The plain client connects.");
                            using TcpClient plainServer = await acceptingPlain.ConfigureAwait(false);
                            await WriteFramedAsync(plainServer.GetStream(), "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"ping\"}", ct).ConfigureAwait(false);
                            JsonProbe plainAnswer = TestJson.ParseRoot(await ReadFramedAsync(plainServer.GetStream(), ct).ConfigureAwait(false));
                            TestAssert.Equal(-32601, plainAnswer.Get("error").Get("code").Int(), "A plain JSON-RPC client answers, but has no built-in ping.");
                        }
                        finally
                        {
                            listener.Stop();
                        }
                    }),

                    Case(suiteId, "WebSocketClientAnswersServerRequests", "McpWebsocketsClient answers ping with {} and an unknown method with -32601", async ct =>
                    {
                        int port = TestPorts.GetFreePort();
                        using HttpListener listener = new HttpListener();
                        listener.Prefixes.Add($"http://localhost:{port}/");
                        listener.Start();

                        Task<HttpListenerContext> accepting = listener.GetContextAsync();
                        using McpWebsocketsClient client = new McpWebsocketsClient();
                        Task<bool> connecting = client.ConnectAsync($"ws://localhost:{port}/", ct);
                        HttpListenerContext context = await accepting.ConfigureAwait(false);
                        HttpListenerWebSocketContext socketContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                        TestAssert.True(await connecting.ConfigureAwait(false), "The client connects.");
                        WebSocket socket = socketContext.WebSocket;

                        await SendTextAsync(socket, "{\"jsonrpc\":\"2.0\",\"id\":\"w1\",\"method\":\"ping\"}", ct).ConfigureAwait(false);
                        await SendTextAsync(socket, "{\"jsonrpc\":\"2.0\",\"id\":\"w2\",\"method\":\"elicitation/create\",\"params\":{}}", ct).ConfigureAwait(false);
                        Dictionary<string, JsonProbe> answers = new Dictionary<string, JsonProbe>();
                        for (int i = 0; i < 2; i++)
                        {
                            JsonProbe answer = TestJson.ParseRoot(await ReceiveTextAsync(socket, ct).ConfigureAwait(false));
                            answers[answer.Get("id").String()!] = answer;
                        }

                        TestAssert.True(answers["w1"].Get("result").IsObject && answers["w1"].Get("result").Length == 0, "ping is answered with {}.");
                        TestAssert.Equal(-32601, answers["w2"].Get("error").Get("code").Int(), "An unhandled method gets -32601.");
                        client.Disconnect();
                    }),

                    Case(suiteId, "HttpClientAnswersRequestsOnGetStream", "McpHttpClient answers server requests from the GET stream by POSTing responses on its session, parses data: without a space, and declares capabilities for registered handlers", async ct =>
                    {
                        using FakeStreamableHttpServer server = new FakeStreamableHttpServer();
                        server.GetHandler = async (index, response, token) =>
                        {
                            FakeStreamableHttpServer.BeginSse(response);
                            await FakeStreamableHttpServer.WriteSseAsync(response, "id: g-1\ndata: {\"jsonrpc\":\"2.0\",\"id\":\"srv-ping\",\"method\":\"ping\"}\n\n", token).ConfigureAwait(false);
                            await FakeStreamableHttpServer.WriteSseAsync(response, "id: g-2\ndata:{\"jsonrpc\":\"2.0\",\"id\":\"srv-roots\",\"method\":\"roots/list\"}\n\n", token).ConfigureAwait(false);
                            await HoldAsync(token).ConfigureAwait(false);
                        };

                        using McpHttpClient client = new McpHttpClient();
                        client.RegisterRequestHandler("roots/list", (args, token) => Task.FromResult<object?>(new { roots = new[] { new { uri = "file:///repo" } } }));
                        TestAssert.True(await client.ConnectStreamableAsync(server.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The client connects.");
                        await client.StartSseAsync(ct).ConfigureAwait(false);

                        TestAssert.True(await WaitUntilAsync(() => server.ClientResponses.Count >= 2, ct).ConfigureAwait(false), $"Both requests were answered (got {server.ClientResponses.Count}).");
                        Dictionary<string, JsonProbe> answers = server.ClientResponses.Select(TestJson.ParseRoot).ToDictionary(probe => probe.Get("id").String()!, probe => probe);
                        TestAssert.True(answers["srv-ping"].Get("result").IsObject && answers["srv-ping"].Get("result").Length == 0, "ping is answered with {}.");
                        TestAssert.Equal("file:///repo", answers["srv-roots"].Get("result").Get("roots")[0].Get("uri").String(), "roots/list is answered by the handler.");

                        JsonProbe initialize = TestJson.ParseRoot(server.InitializeBodies.First());
                        JsonProbe capabilities = initialize.Get("params").Get("capabilities");
                        TestAssert.True(capabilities.Has("roots"), "The roots capability is declared because a roots/list handler is registered.");
                        TestAssert.False(capabilities.Has("sampling") || capabilities.Has("elicitation"), "Capabilities without handlers are not declared.");
                    }),

                    Case(suiteId, "HttpClientReconnectsWithLastEventId", "McpHttpClient skips the priming event, reopens a closed GET stream with Last-Event-ID after the server's retry interval, and stops on 405 or when AutoReconnectSse is false", async ct =>
                    {
                        using FakeStreamableHttpServer server = new FakeStreamableHttpServer();
                        server.GetHandler = async (index, response, token) =>
                        {
                            FakeStreamableHttpServer.BeginSse(response);
                            if (index == 0)
                            {
                                await FakeStreamableHttpServer.WriteSseAsync(response, "id: s-7\nretry: 50\ndata:\n\n", token).ConfigureAwait(false);
                                await FakeStreamableHttpServer.WriteSseAsync(response, ": comment\nid: s-8\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/first\"}\n\n", token).ConfigureAwait(false);
                                return;
                            }

                            await HoldAsync(token).ConfigureAwait(false);
                        };

                        ConcurrentQueue<string> notifications = new ConcurrentQueue<string>();
                        using McpHttpClient client = new McpHttpClient { SseReconnectDelayMs = 5000 };
                        client.NotificationReceived += (sender, notification) => notifications.Enqueue(notification.Method);
                        TestAssert.True(await client.ConnectStreamableAsync(server.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The client connects.");
                        await client.StartSseAsync(ct).ConfigureAwait(false);

                        TestAssert.True(await WaitUntilAsync(() => server.GetCount >= 2, ct, TimeSpan.FromSeconds(3)).ConfigureAwait(false), "The client reconnected within the server's 50 ms retry, not the 5 s default.");
                        string[] lastEventIds = server.GetLastEventIds.ToArray();
                        TestAssert.Equal(String.Empty, lastEventIds[0], "The first GET has no Last-Event-ID.");
                        TestAssert.Equal("s-8", lastEventIds[1], "The reconnect carries the last event ID.");
                        TestAssert.Equal(1, notifications.Count, "The priming event and the comment raise nothing.");

                        using FakeStreamableHttpServer noStream = new FakeStreamableHttpServer();
                        using McpHttpClient rejected = new McpHttpClient { SseReconnectDelayMs = 20 };
                        TestAssert.True(await rejected.ConnectStreamableAsync(noStream.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The client connects.");
                        await rejected.StartSseAsync(ct).ConfigureAwait(false);
                        await Task.Delay(400, ct).ConfigureAwait(false);
                        TestAssert.Equal(1, noStream.GetCount, "A 405 is not retried.");

                        using FakeStreamableHttpServer closing = new FakeStreamableHttpServer();
                        closing.GetHandler = (index, response, token) =>
                        {
                            FakeStreamableHttpServer.BeginSse(response);
                            return FakeStreamableHttpServer.WriteSseAsync(response, "id: c-1\nretry: 10\ndata:\n\n", token);
                        };
                        using McpHttpClient manual = new McpHttpClient { AutoReconnectSse = false };
                        TestAssert.True(await manual.ConnectStreamableAsync(closing.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The client connects.");
                        await manual.StartSseAsync(ct).ConfigureAwait(false);
                        await Task.Delay(400, ct).ConfigureAwait(false);
                        TestAssert.Equal(1, closing.GetCount, "With AutoReconnectSse false the stream is not reopened.");

                        TestAssert.Throws<ArgumentOutOfRangeException>(() => client.SseReconnectDelayMs = -1, "A negative delay is rejected.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => client.SseMaxReconnectAttempts = -1, "A negative attempt count is rejected.");
                    }),

                    Case(suiteId, "HttpClientResumesInterruptedPostStream", "When a POST response stream ends after an event ID but before the response, McpHttpClient resumes it with GET and Last-Event-ID; server requests on the POST stream are answered", async ct =>
                    {
                        using FakeStreamableHttpServer server = new FakeStreamableHttpServer();
                        string pendingId = String.Empty;
                        server.RequestHandler = async (method, idJson, body, response, token) =>
                        {
                            pendingId = idJson;
                            FakeStreamableHttpServer.BeginSse(response);
                            await FakeStreamableHttpServer.WriteSseAsync(response, "id: p-1\nretry: 20\ndata:\n\n", token).ConfigureAwait(false);
                            await FakeStreamableHttpServer.WriteSseAsync(response, "id: p-2\ndata: {\"jsonrpc\":\"2.0\",\"id\":\"srv-ping\",\"method\":\"ping\"}\n\n", token).ConfigureAwait(false);
                            response.Close();
                        };
                        server.GetHandler = async (index, response, token) =>
                        {
                            FakeStreamableHttpServer.BeginSse(response);
                            await FakeStreamableHttpServer.WriteSseAsync(response, "id: p-3\ndata: {\"jsonrpc\":\"2.0\",\"id\":" + pendingId + ",\"result\":{\"tools\":[]}}\n\n", token).ConfigureAwait(false);
                        };

                        using McpHttpClient client = new McpHttpClient();
                        TestAssert.True(await client.ConnectStreamableAsync(server.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The client connects.");
                        JsonRpcResponse listed = await client.CallAsync("tools/list", new { }, 0, ct).ConfigureAwait(false);

                        TestAssert.True(listed.Error == null && listed.Result != null, "The response arrives on the resumed stream.");
                        TestAssert.Equal("p-2", server.GetLastEventIds.First(), "The resume GET carries the last event ID of the POST stream.");
                        TestAssert.True(await WaitUntilAsync(() => server.ClientResponses.Count >= 1, ct).ConfigureAwait(false), "The ping on the POST stream was answered.");
                    }),

                    Case(suiteId, "HttpClientSendsVersionAndCapabilities", "Without a session, McpHttpClient still sends MCP-Protocol-Version after initialize; stateless requests declare the capabilities of registered handlers", async ct =>
                    {
                        using FakeStreamableHttpServer sessionless = new FakeStreamableHttpServer { IssueSession = false };
                        using McpHttpClient client = new McpHttpClient();
                        TestAssert.True(await client.ConnectStreamableAsync(sessionless.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The client connects without a session.");
                        await client.CallAsync("tools/list", new { }, 0, ct).ConfigureAwait(false);
                        WebHeaderCollectionSnapshot[] headers = sessionless.RequestHeaders.ToArray();
                        TestAssert.True(headers[0].Get("MCP-Protocol-Version") == null, "initialize carries no version header.");
                        TestAssert.Equal("2025-11-25", headers[headers.Length - 1].Get("MCP-Protocol-Version"), "Later requests carry the negotiated version.");

                        using FakeStreamableHttpServer stateless = new FakeStreamableHttpServer();
                        stateless.RequestHandler = (method, idJson, body, response, token) => FakeStreamableHttpServer.WriteJsonAsync(response,
                            "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":{\"supportedVersions\":[\"2026-07-28\"],\"capabilities\":{},\"serverInfo\":{\"name\":\"f\",\"version\":\"1\"},\"resultType\":\"complete\"}}", token);
                        using McpHttpClient statelessClient = new McpHttpClient();
                        statelessClient.RegisterRequestHandler("elicitation/create", (args, token) => Task.FromResult<object?>(null));
                        TestAssert.True(await statelessClient.ConnectStatelessAsync(stateless.BaseUrl, "/mcp", null, true, ct).ConfigureAwait(false), "The stateless client connects.");
                        JsonProbe meta = TestJson.ParseRoot(stateless.RequestBodies.Last()).Get("params").Get("_meta");
                        TestAssert.True(meta.Get("io.modelcontextprotocol/clientCapabilities").Has("elicitation"), "The elicitation capability is declared per request.");
                    }),

                    Case(suiteId, "StatelessClientIgnoresServerRequests", "In stateless 2026-07-28 mode McpHttpClient does not answer server requests on a response stream, which that revision forbids, and still reads the response", async ct =>
                    {
                        using FakeStreamableHttpServer server = new FakeStreamableHttpServer();
                        server.RequestHandler = async (method, idJson, body, response, token) =>
                        {
                            if (method == "server/discover")
                            {
                                await FakeStreamableHttpServer.WriteJsonAsync(response, "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":{\"supportedVersions\":[\"2026-07-28\"],\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1\"},\"resultType\":\"complete\"}}", token).ConfigureAwait(false);
                                return;
                            }

                            FakeStreamableHttpServer.BeginSse(response);
                            await FakeStreamableHttpServer.WriteSseAsync(response, "data: {\"jsonrpc\":\"2.0\",\"id\":\"srv-ping\",\"method\":\"ping\"}\n\n", token).ConfigureAwait(false);
                            await FakeStreamableHttpServer.WriteSseAsync(response, "data: {\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":{\"tools\":[],\"resultType\":\"complete\"}}\n\n", token).ConfigureAwait(false);
                            response.Close();
                        };

                        using McpHttpClient client = new McpHttpClient();
                        TestAssert.True(await client.ConnectStatelessAsync(server.BaseUrl, "/mcp", null, true, ct).ConfigureAwait(false), "The stateless client connects.");
                        JsonRpcResponse listed = await client.SendStatelessAsync("tools/list", null, null, ct).ConfigureAwait(false);
                        await Task.Delay(300, ct).ConfigureAwait(false);

                        TestAssert.True(listed.Error == null && listed.Result != null, "The response is read.");
                        TestAssert.Equal(0, server.ClientResponses.Count, "No response is POSTed in stateless mode.");
                    }),
                });
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "client", "server-requests" });
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, CancellationToken token, TimeSpan? timeout = null)
        {
            DateTime deadline = DateTime.UtcNow + (timeout ?? _Wait);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(20, token).ConfigureAwait(false);
            }

            return condition();
        }

        private static async Task HoldAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static async Task WriteFramedAsync(NetworkStream stream, string json, CancellationToken token)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            await stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        private static async Task<string> ReadFramedAsync(NetworkStream stream, CancellationToken token)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_Wait);
            List<byte> header = new List<byte>();
            byte[] one = new byte[1];
            while (true)
            {
                int read = await stream.ReadAsync(one, 0, 1, timeout.Token).ConfigureAwait(false);
                if (read == 0) throw new IOException("The client closed the connection.");
                header.Add(one[0]);
                if (header.Count >= 4 && header[header.Count - 4] == '\r' && header[header.Count - 3] == '\n' && header[header.Count - 2] == '\r' && header[header.Count - 1] == '\n') break;
            }

            string headerText = Encoding.ASCII.GetString(header.ToArray());
            string lengthLine = headerText.Split("\r\n").First(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            int length = Int32.Parse(lengthLine.Substring("Content-Length:".Length).Trim());
            byte[] body = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = await stream.ReadAsync(body, offset, length - offset, timeout.Token).ConfigureAwait(false);
                if (read == 0) throw new IOException("The client closed the connection.");
                offset += read;
            }

            return Encoding.UTF8.GetString(body);
        }

        private static Task SendTextAsync(WebSocket socket, string text, CancellationToken token)
        {
            return socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, token);
        }

        private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken token)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_Wait);
            byte[] buffer = new byte[65536];
            using MemoryStream message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).ConfigureAwait(false);
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(message.ToArray());
        }
    }
}
