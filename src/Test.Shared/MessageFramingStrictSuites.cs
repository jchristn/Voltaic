namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers the strict LSP header grammar in <see cref="MessageFraming"/>: only <c>Content-Length</c> and
    /// <c>Content-Type</c> header lines are accepted, so an HTTP request (as a browser <c>fetch()</c> sends it)
    /// can never drive <see cref="JsonRpcServer"/> or <see cref="McpTcpServer"/>.
    /// </summary>
    public static class MessageFramingStrictSuites
    {
        /// <summary>
        /// Header grammar unit cases and TCP cross-protocol integration cases.
        /// </summary>
        public static TestSuiteDescriptor StrictFraming()
        {
            const string suiteId = "Security.Framing";

            return new TestSuiteDescriptor(
                suiteId,
                "Strict Message Framing and TCP Cross-Protocol Protection",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "HttpRequestIsRejected", "An HTTP POST with a valid Content-Length and JSON body is rejected by the reader", async ct =>
                    {
                        string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"marker\"}";
                        await ExpectRejectedAsync(RawSocketRequests.HttpPost("/", "127.0.0.1:1", body), ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "UnknownHeaderLineIsRejected", "A header block with a header other than Content-Length or Content-Type is rejected", async ct =>
                    {
                        await ExpectRejectedAsync("Content-Length: 2\r\nOrigin: https://evil.example\r\n\r\n{}", ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "GarbageIsRejected", "Unframed data is rejected", async ct =>
                    {
                        await ExpectRejectedAsync("hello\n", ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "OversizedCompleteHeaderIsRejected", "A complete header block over 1024 bytes is rejected", async ct =>
                    {
                        string wire = $"Content-Type: application/json; x={new string('a', 1100)}\r\nContent-Length: 2\r\n\r\n{{}}";
                        await ExpectRejectedAsync(wire, ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "DuplicateOrInvalidContentLengthIsRejected", "Duplicate, negative, or non-numeric Content-Length values are rejected", async ct =>
                    {
                        await ExpectRejectedAsync("Content-Length: 2\r\nContent-Length: 2\r\n\r\n{}", ct).ConfigureAwait(false);
                        await ExpectRejectedAsync("Content-Length: -2\r\n\r\n{}", ct).ConfigureAwait(false);
                        await ExpectRejectedAsync("Content-Length: 2x\r\n\r\n{}", ct).ConfigureAwait(false);
                        await ExpectRejectedAsync("Content-Type: application/json\r\n\r\n{}", ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "LspHeadersInEitherOrderAreAccepted", "Content-Length alone, or with Content-Type in either order, is accepted", async ct =>
                    {
                        string body = "{\"id\":1}";
                        int length = Encoding.UTF8.GetByteCount(body);
                        foreach (string wire in new[]
                        {
                            $"Content-Length: {length}\r\n\r\n{body}",
                            $"Content-Length: {length}\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n{body}",
                            $"content-type: application/json\r\ncontent-length: {length}\r\n\r\n{body}"
                        })
                        {
                            using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(wire));
                            (string? message, byte[] _, int _, int _) = await MessageFraming.ReadMessageAsync(stream, MessageFraming.CreateBuffer(), 0, 0, ct).ConfigureAwait(false);
                            TestAssert.Equal(body, message, $"Accepted: {wire.Split('\r')[0]}");
                        }
                    }),

                    Case(suiteId, "JsonRpcServerDropsHttpRequests", "An HTTP POST to JsonRpcServer is dropped and the method never runs", async ct =>
                    {
                        MarkerProbe probe = new MarkerProbe();
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct, server =>
                        {
                            server.RegisterMethod("marker", _ =>
                            {
                                Interlocked.Increment(ref probe.Invocations);
                                return "invoked";
                            });
                        }).ConfigureAwait(false);

                        string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"marker\"}";
                        string? response = await RawSocketRequests.SendAsync(IPAddress.Loopback, fixture.Port, RawSocketRequests.HttpPost("/", $"127.0.0.1:{fixture.Port}", body), false, ct).ConfigureAwait(false);

                        TestAssert.False(response != null && response.Contains("invoked"), "No result is returned.");
                        TestAssert.Equal(0, probe.Count, "The method never runs.");

                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);
                        string result = await client.CallAsync<string>("marker", token: ct).ConfigureAwait(false);
                        TestAssert.Equal("invoked", result, "Framed requests still work.");
                    }),

                    Case(suiteId, "McpTcpServerDropsHttpRequests", "An HTTP POST carrying tools/call to McpTcpServer is dropped and the tool never runs", async ct =>
                    {
                        MarkerProbe probe = new MarkerProbe();
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, server =>
                        {
                            server.RegisterTool("marker", "Counts its invocations", new { type = "object" }, _ =>
                            {
                                Interlocked.Increment(ref probe.Invocations);
                                return "invoked";
                            });
                        }).ConfigureAwait(false);

                        string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"marker\",\"arguments\":{}}}";
                        await RawSocketRequests.SendAsync(IPAddress.Loopback, fixture.Port, RawSocketRequests.HttpPost("/", $"127.0.0.1:{fixture.Port}", body), false, ct).ConfigureAwait(false);

                        TestAssert.Equal(0, probe.Count, "The tool never runs.");
                    }),
                });
        }

        private static async Task ExpectRejectedAsync(string wire, CancellationToken token)
        {
            using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(wire));
            await TestAssert.ThrowsAsync<InvalidDataException>(async () =>
            {
                byte[] buffer = MessageFraming.CreateBuffer();
                int offset = 0;
                int count = 0;
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    (string? message, byte[] newBuffer, int newOffset, int newCount) = await MessageFraming.ReadMessageAsync(stream, buffer, offset, count, token).ConfigureAwait(false);
                    if (message != null) return;
                    buffer = newBuffer;
                    offset = newOffset;
                    count = newCount;
                    if (count == 0) return;
                }
            }, $"Rejected: {wire.Split('\r')[0]}").ConfigureAwait(false);
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "security", "framing", "tcp" });
        }
    }
}
