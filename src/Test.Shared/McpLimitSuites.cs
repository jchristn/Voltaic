namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers the message size limits added in v2.1.13: every transport bounds one received message
    /// (<c>MaxMessageSize</c>). Servers skip an oversized newline-delimited message with an Invalid Request error and
    /// keep serving, close a Content-Length framed connection, and answer an oversized HTTP body with 413; clients end the
    /// connection or fail the call.
    /// </summary>
    public static class McpLimitSuites
    {
        private static readonly TimeSpan _Wait = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Cases.
        /// </summary>
        public static TestSuiteDescriptor Cases()
        {
            const string suiteId = "Mcp.Limits";
            return new TestSuiteDescriptor(
                suiteId,
                "Message size limits on every transport",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "TcpServerSkipsOversizedLines", "A newline-delimited message over MaxMessageSize gets -32600, and the connection keeps working", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.MaxMessageSize = 4096).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        await client.SendAsync(Oversized(1, 10000)).ConfigureAwait(false);
                        JsonProbe error = McpStrictSuites.Next(client);
                        TestAssert.Equal(-32600, error.Get("error").Get("code").Int(), "The oversized message is an invalid request.");
                        TestAssert.False(error.Has("id"), "Its id cannot be read; a 2025-11-25 session's error omits it.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(client).Has("result"), "The connection still serves requests.");
                    }),

                    Case(suiteId, "TcpServerClosesOversizedContentLength", "A Content-Length framed message over MaxMessageSize closes the connection before its body is read", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.MaxMessageSize = 4096).ConfigureAwait(false);
                        using TcpClient tcp = new TcpClient();
                        await tcp.ConnectAsync(IPAddress.Loopback, fixture.Port, ct).ConfigureAwait(false);
                        NetworkStream stream = tcp.GetStream();
                        byte[] header = Encoding.ASCII.GetBytes("Content-Length: 100000\r\n\r\n");
                        await stream.WriteAsync(header, ct).ConfigureAwait(false);
                        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(_Wait);
                        int read;
                        try
                        {
                            read = await stream.ReadAsync(new byte[16], timeout.Token).ConfigureAwait(false);
                        }
                        catch (IOException)
                        {
                            read = 0;
                        }

                        TestAssert.Equal(0, read, "The server closed the connection.");
                    }),

                    Case(suiteId, "StdioServerSkipsOversizedLines", "The stdio server answers a message over MaxMessageSize with -32600 and keeps running", async ct =>
                    {
                        await using McpRawStreamChannel channel = McpRawStreamChannel.StartStdio(new Dictionary<string, string> { { "VOLTAIC_MAX_MESSAGE", "4096" } });
                        await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"init\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}", ct).ConfigureAwait(false);
                        TestAssert.True(NextWithId(channel, "\"init\"") != null, "The server initializes.");
                        await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", ct).ConfigureAwait(false);
                        await channel.SendAsync(Oversized(1, 10000), ct).ConfigureAwait(false);
                        JsonProbe? error = NextWithoutId(channel);
                        TestAssert.True(error != null && error.Get("error").Get("code").Int() == -32600, "The oversized message gets -32600 without an id (2025-11-25).");
                        await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}", ct).ConfigureAwait(false);
                        TestAssert.True(NextWithId(channel, "2") != null, "The server keeps serving.");
                    }),

                    Case(suiteId, "HttpServerRejectsOversizedBodies", "A request body over MaxMessageSize gets 413 with -32600, whether or not it declares its length", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s => s.MaxMessageSize = 4096).ConfigureAwait(false);
                        RpcResult declared = await McpHttpTestRequests.SendAsync(fixture, Oversized(1, 10000), new Dictionary<string, string>(), true, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode, $"A declared oversized body is rejected: {declared.Body}");
                        TestAssert.Equal(-32600, declared.Error.Get("code").Int(), "With an Invalid Request error.");
                        TestAssert.True(declared.Root.Has("id") && declared.Root.Get("id").IsNull, "A header-less (2025-03-26) request's error has id null, as JSON-RPC 2.0 requires.");

                        using System.Net.Http.HttpRequestMessage chunked = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, fixture.BaseUrl + "/mcp/")
                        {
                            Content = new System.Net.Http.StringContent(Oversized(1, 10000), Encoding.UTF8, "application/json")
                        };
                        chunked.Headers.Accept.ParseAdd("application/json");
                        chunked.Headers.Accept.ParseAdd("text/event-stream");
                        chunked.Headers.TransferEncodingChunked = true;
                        using System.Net.Http.HttpResponseMessage response = await fixture.Client.SendAsync(chunked, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode, "A chunked oversized body is rejected too.");

                        RpcResult small = await McpHttpTestRequests.InitializeAsync(fixture, "2025-11-25", null, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, small.StatusCode, "Smaller requests are served.");
                    }),

                    Case(suiteId, "TcpClientClosesOnOversizedMessages", "McpTcpClient ends the connection when a server message exceeds its MaxMessageSize, failing the waiting call", async ct =>
                    {
                        using RawTcpPeer peer = RawTcpPeer.Listen();
                        using McpTcpClient client = new McpTcpClient { AutoInitialize = false, PingIntervalMs = 0, MaxMessageSize = 4096 };
                        Task accepting = peer.AcceptAsync(ct);
                        TestAssert.True(await client.ConnectAsync("127.0.0.1", peer.Port, ct).ConfigureAwait(false), "The client connects.");
                        await accepting.ConfigureAwait(false);
                        Task<object?> call = client.CallAsync<object?>("custom/big", null, 5000, ct);
                        string? request = await peer.ReceiveAsync(_Wait, ct).ConfigureAwait(false);
                        TestAssert.True(request != null, "The request arrives.");
                        int id = TestJson.ParseRoot(request!).Get("id").Int();
                        await peer.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"pad\":\"" + new string('x', 10000) + "\"}}", ct).ConfigureAwait(false);
                        bool failed = false;
                        try
                        {
                            await call.ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is InvalidOperationException || ex is OperationCanceledException)
                        {
                            failed = true;
                        }

                        TestAssert.True(failed, "The call fails.");
                        TestAssert.False(client.IsConnected, "The client closed the connection.");
                    }),

                    Case(suiteId, "StdioClientEndsOnOversizedMessages", "McpClient shuts the connection down when a server message exceeds its MaxMessageSize, failing the waiting call", async ct =>
                    {
                        using McpClient client = new McpClient { MaxMessageSize = 4096, PingIntervalMs = 0 };
                        await McpStdioIntegrationSuites.LaunchTestServerAsync(client, ct).ConfigureAwait(false);
                        bool failed = false;
                        try
                        {
                            await client.CallAsync<object?>("tools/call", new { name = "echo", arguments = new { message = new string('x', 10000) } }, 10000, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is InvalidOperationException || ex is OperationCanceledException)
                        {
                            failed = true;
                        }

                        TestAssert.True(failed, "The oversized response is not accepted.");
                        TestAssert.False(client.IsConnected, "The client ended the connection.");
                    }),

                    Case(suiteId, "HttpClientRejectsOversizedResponses", "McpHttpClient fails a call whose response body exceeds its MaxMessageSize", async ct =>
                    {
                        using FakeStreamableHttpServer fake = new FakeStreamableHttpServer();
                        fake.RequestHandler = (method, id, body, response, token) =>
                            FakeStreamableHttpServer.WriteJsonAsync(response, "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"pad\":\"" + new string('x', 10000) + "\"}}", token);
                        using McpHttpClient client = new McpHttpClient { MaxMessageSize = 4096 };
                        TestAssert.True(await client.ConnectStreamableAsync(fake.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The client connects.");
                        bool failed = false;
                        try
                        {
                            await client.CallAsync<object?>("custom/big", null, 5000, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is McpProtocolException)
                        {
                            failed = true;
                        }

                        TestAssert.True(failed, "The oversized response fails the call.");
                    }),
                });
        }

        // A ping request padded to the given size.
        private static string Oversized(int id, int size)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"ping\",\"params\":{\"pad\":\"" + new string('x', size) + "\"}}";
        }

        // The next error response without an id, skipping notifications and server requests.
        private static JsonProbe? NextWithoutId(IMcpRawChannel channel)
        {
            DateTime deadline = DateTime.UtcNow + _Wait;
            while (DateTime.UtcNow < deadline)
            {
                string? line = channel.Receive(deadline - DateTime.UtcNow);
                if (line == null) return null;
                JsonProbe message = TestJson.ParseRoot(line);
                if (message.IsObject && message.Has("error") && !message.Has("id")) return message;
            }

            return null;
        }

        // The next response whose raw id is the given JSON text, skipping notifications and server requests.
        private static JsonProbe? NextWithId(IMcpRawChannel channel, string idJson)
        {
            DateTime deadline = DateTime.UtcNow + _Wait;
            while (DateTime.UtcNow < deadline)
            {
                string? line = channel.Receive(deadline - DateTime.UtcNow);
                if (line == null) return null;
                using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(line);
                System.Text.Json.JsonElement root = document.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object || root.TryGetProperty("method", out System.Text.Json.JsonElement _)) continue;
                if (root.TryGetProperty("id", out System.Text.Json.JsonElement id) && id.GetRawText() == idJson) return TestJson.ParseRoot(line);
            }

            return null;
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "limits", "conformance" });
        }
    }
}
