namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers the caller-supplied <see cref="HttpMessageHandler"/> on <see cref="McpHttpClient"/> and
    /// <see cref="McpWebsocketsClient"/>: every request goes through it, redirects and the connect callback are the
    /// handler's choice (so a caller can block redirects and DNS rebinding to internal addresses), and ownership
    /// follows <c>disposeHandler</c>.
    /// </summary>
    public static class McpClientHandlerSuites
    {
        private static readonly IPAddress BlockedAddress = IPAddress.Parse("127.0.0.2");

        /// <summary>
        /// Caller-supplied HTTP handler cases.
        /// </summary>
        public static TestSuiteDescriptor ClientHandlers()
        {
            const string suiteId = "Security.ClientHandler";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP Client Caller-Supplied HTTP Handler",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "NullHandlerThrows", "Both handler constructors reject a null handler", ct =>
                    {
                        TestAssert.Throws<ArgumentNullException>(() => new McpHttpClient(null!), "McpHttpClient rejects a null handler.");
                        TestAssert.Throws<ArgumentNullException>(() => new McpHttpClient(null!, false), "McpHttpClient rejects a null handler with disposeHandler false.");
                        TestAssert.Throws<ArgumentNullException>(() => new McpWebsocketsClient(null!), "McpWebsocketsClient rejects a null handler.");
                        TestAssert.Throws<ArgumentNullException>(() => new McpWebsocketsClient(null!, false), "McpWebsocketsClient rejects a null handler with disposeHandler false.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "HttpHandlerSeesEveryStreamableRequest", "The handler carries the initialize handshake, calls, notifications, the SSE GET, and the session DELETE", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        RecordingHttpHandler handler = new RecordingHttpHandler();
                        using McpHttpClient client = new McpHttpClient(handler);

                        TestAssert.True(await client.ConnectStreamableAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false), "The client connects through the handler.");
                        JsonElement tools = await client.CallAsync<JsonElement>("tools/list", null, token: ct).ConfigureAwait(false);
                        await client.NotifyAsync("notifications/roots/list_changed", null, token: ct).ConfigureAwait(false);
                        TestAssert.True(await client.StartSseAsync(ct).ConfigureAwait(false), "The SSE GET starts through the handler.");
                        await WaitUntilAsync(() => client.IsSseConnected, ct).ConfigureAwait(false);
                        client.Disconnect();

                        List<string> requests = handler.Requests;
                        TestAssert.True(tools.TryGetProperty("tools", out _), "The call returns a result.");
                        TestAssert.True(requests.Count(request => request.StartsWith("POST /mcp", StringComparison.Ordinal)) >= 4, $"initialize, notifications/initialized, the call, and the notification are POSTs through the handler: {String.Join("; ", requests)}");
                        TestAssert.True(handler.Saw("GET /mcp"), $"The SSE GET goes through the handler: {String.Join("; ", requests)}");
                        TestAssert.True(handler.Saw("DELETE /mcp"), $"The session DELETE goes through the handler: {String.Join("; ", requests)}");
                    }),

                    Case(suiteId, "HttpHandlerSeesLegacyRequests", "The handler carries the legacy /rpc POSTs and the /events GET", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        RecordingHttpHandler handler = new RecordingHttpHandler();
                        using McpHttpClient client = new McpHttpClient(handler);

                        TestAssert.True(await client.ConnectAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false), "The legacy client connects through the handler.");
                        TestAssert.True(await client.StartSseAsync(ct).ConfigureAwait(false), "The /events GET starts through the handler.");
                        await WaitUntilAsync(() => client.IsSseConnected, ct).ConfigureAwait(false);
                        client.Disconnect();

                        TestAssert.True(handler.Saw("POST /rpc"), $"The /rpc POSTs go through the handler: {String.Join("; ", handler.Requests)}");
                        TestAssert.True(handler.Saw("GET /events"), $"The /events GET goes through the handler: {String.Join("; ", handler.Requests)}");
                    }),

                    Case(suiteId, "HttpHandlerSeesStatelessRequests", "The handler carries stateless (2026-07-28) discovery and tool calls", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                            server.RegisterTool("noop", "Does nothing", new { type = "object", properties = new { } }, _ => "ok")).ConfigureAwait(false);
                        RecordingHttpHandler handler = new RecordingHttpHandler();
                        using McpHttpClient client = new McpHttpClient(handler);

                        TestAssert.True(await client.ConnectStatelessAsync(fixture.BaseUrl, "/mcp", token: ct).ConfigureAwait(false), "The stateless client connects through the handler.");
                        int beforeCall = handler.Requests.Count;
                        JsonRpcResponse response = await client.CallToolStatelessAsync("noop", null, null, 3, ct).ConfigureAwait(false);

                        TestAssert.Null(response.Error, "The stateless call succeeds.");
                        TestAssert.True(beforeCall >= 1, "Discovery goes through the handler.");
                        TestAssert.True(handler.Requests.Count > beforeCall, "The stateless tool call goes through the handler.");
                        TestAssert.True(handler.Saw("POST /mcp 2026-07-28"), $"Stateless requests carry 2026-07-28 through the handler: {String.Join("; ", handler.Requests)}");
                    }),

                    Case(suiteId, "HttpRedirectIsNotFollowedWhenHandlerRefuses", "With AllowAutoRedirect false, a 302 to another address fails the connect and the target is never contacted", async ct =>
                    {
                        using CannedHttpPeer target = CannedHttpPeer.NotFound();
                        using CannedHttpPeer redirector = CannedHttpPeer.Redirect($"http://127.0.0.1:{target.Port}/latest/meta-data/");
                        using McpHttpClient client = new McpHttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });

                        bool connected = await client.ConnectStreamableAsync($"http://127.0.0.1:{redirector.Port}", token: ct).ConfigureAwait(false);
                        await Task.Delay(200, ct).ConfigureAwait(false);

                        TestAssert.False(connected, "A redirected handshake fails.");
                        TestAssert.True(redirector.Connections >= 1, "The client reached the redirecting server.");
                        TestAssert.Equal(0, target.Connections, "The redirect target is never contacted.");
                    }),

                    Case(suiteId, "DefaultClientStillFollowsRedirects", "The parameterless constructor keeps today's behavior and follows the redirect", async ct =>
                    {
                        using CannedHttpPeer target = CannedHttpPeer.NotFound();
                        using CannedHttpPeer redirector = CannedHttpPeer.Redirect($"http://127.0.0.1:{target.Port}/latest/meta-data/");
                        using McpHttpClient client = new McpHttpClient();

                        await client.ConnectStreamableAsync($"http://127.0.0.1:{redirector.Port}", token: ct).ConfigureAwait(false);

                        TestAssert.True(await target.WaitForConnectionAsync(2000, ct).ConfigureAwait(false), "The default client follows the redirect, unchanged from earlier releases.");
                    }),

                    Case(suiteId, "HttpConnectCallbackRefusesBlockedAddress", "A ConnectCallback that refuses an address fails the connect with the callback's exception, and nothing reaches the server", async ct =>
                    {
                        int received = 0;
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server => server.RequestReceived += (_, _) => Interlocked.Increment(ref received)).ConfigureAwait(false);
                        RecordingHttpHandler handler = new RecordingHttpHandler(PolicyHandler());
                        using McpHttpClient client = new McpHttpClient(handler);

                        bool connected = await client.ConnectStreamableAsync($"http://{BlockedAddress}:{fixture.Port}", token: ct).ConfigureAwait(false);

                        TestAssert.False(connected, "The connect to a blocked address fails.");
                        Exception? failure = handler.Failures.FirstOrDefault();
                        TestAssert.NotNull(failure, "The handler reported the failure.");
                        TestAssert.True(failure is HttpRequestException && failure.InnerException is UnauthorizedAccessException, $"The callback's exception is the inner exception: {failure}");
                        TestAssert.Equal(0, Volatile.Read(ref received), "No request reaches the server.");
                    }),

                    Case(suiteId, "HttpConnectCallbackAllowsPermittedAddress", "The same policy handler connects to an allowed address and the session works", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient(PolicyHandler());

                        TestAssert.True(await client.ConnectStreamableAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false), "The connect to an allowed address succeeds.");
                        JsonElement tools = await client.CallAsync<JsonElement>("tools/list", null, token: ct).ConfigureAwait(false);
                        TestAssert.True(tools.TryGetProperty("tools", out _), "Calls work through the policy handler.");
                    }),

                    Case(suiteId, "HttpHandlerOwnershipFollowsDisposeHandler", "disposeHandler false leaves the handler usable by another client; true disposes it", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        RecordingHttpHandler shared = new RecordingHttpHandler();
                        RecordingHttpHandler owned = new RecordingHttpHandler();

                        using (McpHttpClient first = new McpHttpClient(shared, false))
                        {
                            TestAssert.True(await first.ConnectStreamableAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false), "The first client connects.");
                        }

                        TestAssert.False(shared.IsDisposed, "disposeHandler false leaves the handler undisposed.");
                        using (McpHttpClient second = new McpHttpClient(shared, false))
                        {
                            TestAssert.True(await second.ConnectStreamableAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false), "A second client connects through the same handler.");
                        }

                        new McpHttpClient(owned).Dispose();
                        TestAssert.True(owned.IsDisposed, "disposeHandler true (the default) disposes the handler.");
                        shared.Dispose();
                    }),

                    Case(suiteId, "WebSocketHandlerCarriesUpgrade", "The WebSocket upgrade goes through the handler and the connection works", async ct =>
                    {
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct).ConfigureAwait(false);
                        RecordingHttpHandler handler = new RecordingHttpHandler(PolicyHandler());
                        using McpWebsocketsClient client = new McpWebsocketsClient(handler);

                        TestAssert.True(await client.ConnectAsync(fixture.Url, ct).ConfigureAwait(false), "The client connects through the handler.");
                        JsonElement tools = await client.CallAsync<JsonElement>("tools/list", null, token: ct).ConfigureAwait(false);

                        TestAssert.True(tools.TryGetProperty("tools", out _), "Calls work over the upgraded connection.");
                        TestAssert.True(handler.Saw("GET /mcp/"), $"The upgrade request goes through the handler: {String.Join("; ", handler.Requests)}");
                    }),

                    Case(suiteId, "WebSocketConnectCallbackRefusesBlockedAddress", "A ConnectCallback that refuses an address fails the WebSocket connect", async ct =>
                    {
                        int connections = 0;
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct, server => server.ClientConnected += (_, _) => Interlocked.Increment(ref connections)).ConfigureAwait(false);
                        RecordingHttpHandler handler = new RecordingHttpHandler(PolicyHandler());
                        using McpWebsocketsClient client = new McpWebsocketsClient(handler);

                        bool connected = await client.ConnectAsync($"ws://{BlockedAddress}:{fixture.Port}/mcp/", ct).ConfigureAwait(false);

                        TestAssert.False(connected, "The connect to a blocked address fails.");
                        Exception? failure = handler.Failures.FirstOrDefault();
                        TestAssert.True(failure?.InnerException is UnauthorizedAccessException, $"The callback refused the address: {failure}");
                        TestAssert.Equal(0, Volatile.Read(ref connections), "No connection reaches the server.");
                    }),

                    Case(suiteId, "WebSocketRedirectIsNotFollowed", "A 302 answer to the upgrade fails the connect and the target is never contacted", async ct =>
                    {
                        using CannedHttpPeer target = CannedHttpPeer.NotFound();
                        using CannedHttpPeer redirector = CannedHttpPeer.Redirect($"http://127.0.0.1:{target.Port}/latest/meta-data/");
                        using McpWebsocketsClient client = new McpWebsocketsClient(new SocketsHttpHandler { AllowAutoRedirect = false });

                        bool connected = await client.ConnectAsync($"ws://127.0.0.1:{redirector.Port}/mcp/", ct).ConfigureAwait(false);
                        await Task.Delay(200, ct).ConfigureAwait(false);

                        TestAssert.False(connected, "A redirected upgrade fails.");
                        TestAssert.True(redirector.Connections >= 1, "The client reached the redirecting server.");
                        TestAssert.Equal(0, target.Connections, "The redirect target is never contacted.");
                    }),

                    Case(suiteId, "WebSocketHandlerOwnershipFollowsDisposeHandler", "disposeHandler false leaves the handler usable across reconnects and clients; true disposes it", async ct =>
                    {
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct).ConfigureAwait(false);
                        RecordingHttpHandler shared = new RecordingHttpHandler();
                        RecordingHttpHandler owned = new RecordingHttpHandler();

                        using (McpWebsocketsClient first = new McpWebsocketsClient(shared, false))
                        {
                            TestAssert.True(await first.ConnectAsync(fixture.Url, ct).ConfigureAwait(false), "The first client connects.");
                            TestAssert.True(await first.ConnectAsync(fixture.Url, ct).ConfigureAwait(false), "The first client reconnects through the same handler.");
                        }

                        TestAssert.False(shared.IsDisposed, "disposeHandler false leaves the handler undisposed.");
                        using (McpWebsocketsClient second = new McpWebsocketsClient(shared, false))
                        {
                            TestAssert.True(await second.ConnectAsync(fixture.Url, ct).ConfigureAwait(false), "A second client connects through the same handler.");
                        }

                        new McpWebsocketsClient(owned).Dispose();
                        TestAssert.True(owned.IsDisposed, "disposeHandler true (the default) disposes the handler.");
                        TestAssert.Equal(3, shared.Requests.Count(request => request.StartsWith("GET /mcp/", StringComparison.Ordinal)), "Every upgrade went through the shared handler.");
                        shared.Dispose();
                    }),
                });
        }

        // A handler like the one an application would use against untrusted URLs: no redirects, and the connect callback
        // resolves the host itself and refuses blocked addresses, so the address checked is the address connected to.
        private static SocketsHttpHandler PolicyHandler()
        {
            return new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = async (context, token) =>
                {
                    IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
                    IPAddress? blocked = addresses.FirstOrDefault(address => address.Equals(BlockedAddress));
                    if (blocked != null) throw new UnauthorizedAccessException($"Address {blocked} is refused by the outbound address policy.");

                    SocketException? last = null;
                    foreach (IPAddress address in addresses)
                    {
                        Socket socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                        try
                        {
                            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                            return new NetworkStream(socket, true);
                        }
                        catch (SocketException ex)
                        {
                            socket.Dispose();
                            last = ex;
                        }
                    }

                    throw new HttpRequestException($"No address of {context.DnsEndPoint.Host} accepted the connection.", last);
                }
            };
        }

        private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20, token).ConfigureAwait(false);
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "security", "http", "websocket", "client-handler" });
        }
    }
}
