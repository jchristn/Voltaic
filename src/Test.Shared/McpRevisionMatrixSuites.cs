namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// The revision matrix: one core conformance scenario per MCP revision on every transport (stdio, TCP, WebSocket,
    /// Streamable HTTP), against the same fixture. Each handshake revision checks negotiation, ping, the
    /// revision-specific shape of tools/list and tools/call results, invalid arguments, unknown methods, resources,
    /// prompts, log level filtering, progress, cancellation, and batching; 2026-07-28 checks discovery, result types,
    /// caching hints, the per-request <c>_meta</c> rule, removed methods, and cancellation. COMPATIBILITY.md cites
    /// these cases.
    /// </summary>
    public static class McpRevisionMatrixSuites
    {
        private static readonly TimeSpan _Wait = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan _Quiet = TimeSpan.FromMilliseconds(600);
        private static readonly string[] _Transports = { "stdio", "tcp", "websocket", "http" };
        private static readonly string[] _HandshakeRevisions = { "2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25" };

        /// <summary>
        /// Cases.
        /// </summary>
        public static TestSuiteDescriptor Cases()
        {
            const string suiteId = "Mcp.RevisionMatrix";
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();
            foreach (string transport in _Transports)
            {
                foreach (string revision in _HandshakeRevisions)
                {
                    cases.Add(Case(suiteId, $"{transport}-{revision}", $"{revision} core conformance over {transport}", ct => RunHandshakeAsync(transport, revision, ct)));
                }

                cases.Add(Case(suiteId, $"{transport}-2026-07-28", $"2026-07-28 core conformance over {transport}", ct => RunStatelessAsync(transport, ct)));
            }

            return new TestSuiteDescriptor(suiteId, "Core conformance per revision and transport", cases);
        }

        private static async Task RunHandshakeAsync(string transport, string revision, CancellationToken ct)
        {
            await using McpMatrixConnection connection = await McpMatrixConnection.OpenAsync(transport, ct).ConfigureAwait(false);
            IMcpRawChannel channel = connection.Channel;
            int ordinal = Array.IndexOf(_HandshakeRevisions, revision);
            bool from20250326 = ordinal >= 1;
            bool from20250618 = ordinal >= 2;
            string where = $"{revision} over {transport}";

            // Negotiation.
            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"init\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"" + revision + "\",\"capabilities\":{},\"clientInfo\":{\"name\":\"matrix\",\"version\":\"1\"}}}", ct).ConfigureAwait(false);
            JsonProbe initialize = Await(channel, "init", null).Get("result");
            TestAssert.Equal(revision, initialize.Get("protocolVersion").String(), $"initialize negotiates the requested revision ({where}).");
            TestAssert.Equal(from20250326, initialize.Get("capabilities").Has("completions"), $"completions is advertised only from 2025-03-26 ({where}).");

            // A real client sends MCP-Protocol-Version on HTTP only from 2025-06-18, the revision that defines it.
            if (from20250618) channel.ProtocolVersion = revision;
            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", ct).ConfigureAwait(false);

            // ping answers an empty result.
            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"ping\"}", ct).ConfigureAwait(false);
            JsonProbe ping = Await(channel, "1", null);
            TestAssert.True(ping.Has("result") && ping.Get("result").Length == 0, $"ping returns {{}} ({where}).");

            // tools/list has exactly the fields the revision defines.
            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"2\",\"method\":\"tools/list\"}", ct).ConfigureAwait(false);
            JsonProbe tools = Await(channel, "2", null).Get("result").Get("tools");
            JsonProbe? describe = null;
            for (int i = 0; i < tools.Length; i++)
            {
                if (tools[i].Get("name").String() == "describe") describe = tools[i];
            }

            TestAssert.True(describe != null, $"The fixture tool is listed ({where}).");
            TestAssert.Equal(from20250618, describe!.Has("title"), $"Tool.title exists only from 2025-06-18 ({where}).");
            TestAssert.Equal(from20250618, describe.Has("outputSchema"), $"Tool.outputSchema exists only from 2025-06-18 ({where}).");
            TestAssert.Equal(from20250326, describe.Has("annotations"), $"Tool.annotations exists only from 2025-03-26 ({where}).");
            if (revision == "2025-03-26") TestAssert.Equal("Describe", describe.Get("annotations").Get("title").String(), $"On 2025-03-26 the title travels as annotations.title ({where}).");

            // tools/call: structured content only from 2025-06-18; invalid arguments are a tool error.
            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"3\",\"method\":\"tools/call\",\"params\":{\"name\":\"describe\",\"arguments\":{\"n\":5}}}", ct).ConfigureAwait(false);
            JsonProbe called = Await(channel, "3", null).Get("result");
            TestAssert.False(called.Has("isError") && called.Get("isError").Bool(), $"The call succeeds ({where}).");
            TestAssert.Equal(from20250618, called.Has("structuredContent"), $"structuredContent exists only from 2025-06-18 ({where}).");
            if (from20250618) TestAssert.Equal(5, called.Get("structuredContent").Get("n").Int(), $"The structured result is sent ({where}).");
            TestAssert.True(called.Get("content").Length > 0, $"The text content is sent ({where}).");

            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"4\",\"method\":\"tools/call\",\"params\":{\"name\":\"describe\",\"arguments\":{}}}", ct).ConfigureAwait(false);
            JsonProbe invalid = Await(channel, "4", null).Get("result");
            TestAssert.True(invalid.Has("isError") && invalid.Get("isError").Bool(), $"Invalid arguments are a tool error ({where}).");

            // Unknown methods, resources, prompts.
            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"5\",\"method\":\"nope/nothing\"}", ct).ConfigureAwait(false);
            TestAssert.Equal(-32601, Await(channel, "5", null).Get("error").Get("code").Int(), $"An unknown method is -32601 ({where}).");
            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"6\",\"method\":\"resources/read\",\"params\":{\"uri\":\"" + McpMatrixFixture.ResourceUri + "\"}}", ct).ConfigureAwait(false);
            TestAssert.Equal("matrix resource", Await(channel, "6", null).Get("result").Get("contents")[0].Get("text").String(), $"resources/read ({where}).");
            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"7\",\"method\":\"prompts/get\",\"params\":{\"name\":\"greet\",\"arguments\":{\"topic\":\"x\"}}}", ct).ConfigureAwait(false);
            TestAssert.Equal("Hello x", Await(channel, "7", null).Get("result").Get("messages")[0].Get("content").Get("text").String(), $"prompts/get ({where}).");

            // Log level filtering and progress.
            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"8\",\"method\":\"logging/setLevel\",\"params\":{\"level\":\"error\"}}", ct).ConfigureAwait(false);
            TestAssert.True(Await(channel, "8", null).Has("result"), $"logging/setLevel succeeds ({where}).");
            List<JsonProbe> notifications = new List<JsonProbe>();
            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"9\",\"method\":\"tools/call\",\"params\":{\"name\":\"report\",\"arguments\":{},\"_meta\":{\"progressToken\":\"pt\"}}}", ct).ConfigureAwait(false);
            JsonProbe reported = Await(channel, "9", notifications);
            TestAssert.Equal("reported", reported.Get("result").Get("content")[0].Get("text").String(), $"The reporting tool answers ({where}).");
            List<JsonProbe> logs = notifications.Where(n => n.Get("method").String() == "notifications/message").ToList();
            TestAssert.Equal(1, logs.Count, $"Only the message at the set level is sent ({where}).");
            TestAssert.Equal("error", logs[0].Get("params").Get("level").String(), $"The error message is sent ({where}).");
            List<JsonProbe> progress = notifications.Where(n => n.Get("method").String() == "notifications/progress").ToList();
            TestAssert.Equal(1, progress.Count, $"The progress notification precedes the response ({where}).");
            TestAssert.Equal("pt", progress[0].Get("params").Get("progressToken").String(), $"Progress carries the request's token ({where}).");
            TestAssert.Equal(from20250326, progress[0].Get("params").Has("message"), $"Progress message exists only from 2025-03-26 ({where}).");

            // Cancellation: no response for the cancelled request.
            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"10\",\"method\":\"tools/call\",\"params\":{\"name\":\"slow\",\"arguments\":{}}}", ct).ConfigureAwait(false);
            await Task.Delay(300, ct).ConfigureAwait(false);
            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":\"10\",\"reason\":\"matrix\"}}", ct).ConfigureAwait(false);
            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"11\",\"method\":\"ping\"}", ct).ConfigureAwait(false);
            List<string> seen = new List<string>();
            Await(channel, "11", null, seen);
            AssertQuiet(channel, "10", seen, $"The cancelled request gets no response ({where}).");

            // Batches exist in 2025-03-26 (and are accepted on 2024-11-05).
            await channel.SendAsync("[{\"jsonrpc\":\"2.0\",\"id\":\"12\",\"method\":\"ping\"},{\"jsonrpc\":\"2.0\",\"id\":\"13\",\"method\":\"ping\"}]", ct).ConfigureAwait(false);
            JsonProbe batch = NextAnswer(channel);
            // JSON-RPC 2.0 allows batches and 2024-11-05 does not mention them, so they are accepted there too
            // (a disclosed relaxation).
            if (revision == "2024-11-05" || revision == "2025-03-26")
            {
                TestAssert.Equal(2, batch.Length, $"The batch is answered with an array of two responses ({where}).");
            }
            else
            {
                TestAssert.Equal(-32600, batch.Get("error").Get("code").Int(), $"A batch is an invalid request ({where}).");
            }
        }

        private static async Task RunStatelessAsync(string transport, CancellationToken ct)
        {
            await using McpMatrixConnection connection = await McpMatrixConnection.OpenAsync(transport, ct).ConfigureAwait(false);
            IMcpRawChannel channel = connection.Channel;
            channel.Stateless = true;
            channel.ProtocolVersion = McpProtocol.ProtocolVersion20260728;
            string meta = McpStrictSuites.Meta("{}");
            string where = $"2026-07-28 over {transport}";

            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"d\",\"method\":\"server/discover\",\"params\":{" + meta + "}}", ct).ConfigureAwait(false);
            JsonProbe discover = Await(channel, "d", null).Get("result");
            List<string?> versions = Enumerable.Range(0, discover.Get("supportedVersions").Length).Select(i => discover.Get("supportedVersions")[i].String()).ToList();
            TestAssert.True(versions.Contains("2026-07-28"), $"server/discover lists 2026-07-28 ({where}).");
            TestAssert.Equal("complete", discover.Get("resultType").String(), $"Results carry resultType ({where}).");

            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"l\",\"method\":\"tools/list\",\"params\":{" + meta + "}}", ct).ConfigureAwait(false);
            JsonProbe list = Await(channel, "l", null).Get("result");
            TestAssert.Equal("complete", list.Get("resultType").String(), $"tools/list carries resultType ({where}).");
            TestAssert.True(list.Has("ttlMs") && list.Has("cacheScope"), $"tools/list carries caching hints ({where}).");

            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"c\",\"method\":\"tools/call\",\"params\":{\"name\":\"describe\",\"arguments\":{\"n\":7}," + meta + "}}", ct).ConfigureAwait(false);
            JsonProbe called = Await(channel, "c", null).Get("result");
            TestAssert.Equal(7, called.Get("structuredContent").Get("n").Int(), $"tools/call returns structured content ({where}).");
            TestAssert.Equal("complete", called.Get("resultType").String(), $"tools/call carries resultType ({where}).");

            // _meta without the protocol version.
            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"m\",\"method\":\"tools/list\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/clientInfo\":{\"name\":\"t\",\"version\":\"1\"},\"io.modelcontextprotocol/clientCapabilities\":{}}}}", ct).ConfigureAwait(false);
            int missing = Await(channel, "m", null).Get("error").Get("code").Int();
            TestAssert.True(missing == -32602 || missing == -32020, $"A request without the _meta protocol version is rejected: {missing} ({where}).");

            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"p\",\"method\":\"ping\",\"params\":{" + meta + "}}", ct).ConfigureAwait(false);
            TestAssert.Equal(-32601, Await(channel, "p", null).Get("error").Get("code").Int(), $"ping does not exist in 2026-07-28 ({where}).");
            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"g\",\"method\":\"logging/setLevel\",\"params\":{\"level\":\"error\"," + meta + "}}", ct).ConfigureAwait(false);
            TestAssert.Equal(-32601, Await(channel, "g", null).Get("error").Get("code").Int(), $"logging/setLevel does not exist in 2026-07-28 ({where}).");

            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"s\",\"method\":\"tools/call\",\"params\":{\"name\":\"slow\",\"arguments\":{}," + meta + "}}", ct).ConfigureAwait(false);
            await Task.Delay(300, ct).ConfigureAwait(false);
            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":\"s\"}}", ct).ConfigureAwait(false);
            await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"after\",\"method\":\"tools/list\",\"params\":{" + meta + "}}", ct).ConfigureAwait(false);
            List<string> seen = new List<string>();
            Await(channel, "after", null, seen);
            AssertQuiet(channel, "s", seen, $"The cancelled request gets no response ({where}).");
        }

        // Reads messages until the response with the ID arrives; answers server pings, collects notifications, and
        // records the IDs of other responses.
        private static JsonProbe Await(IMcpRawChannel channel, string id, List<JsonProbe>? notifications, List<string>? otherIds = null)
        {
            DateTime deadline = DateTime.UtcNow + _Wait;
            while (DateTime.UtcNow < deadline)
            {
                string? line = channel.Receive(deadline - DateTime.UtcNow);
                if (line == null) break;
                JsonProbe message = TestJson.ParseRoot(line);
                if (!message.IsObject) continue;
                if (AnswerServerRequest(channel, line)) continue;
                if (message.Has("method"))
                {
                    notifications?.Add(message);
                    continue;
                }

                string? messageId = message.Has("id") ? message.Get("id").String() : null;
                if (messageId == id) return message;
                if (messageId != null) otherIds?.Add(messageId);
            }

            throw new TimeoutException($"No response with id {id} arrived over {channel.Transport}.");
        }

        // The next response or batch answer, skipping notifications and answering server pings.
        private static JsonProbe NextAnswer(IMcpRawChannel channel)
        {
            DateTime deadline = DateTime.UtcNow + _Wait;
            while (DateTime.UtcNow < deadline)
            {
                string? line = channel.Receive(deadline - DateTime.UtcNow);
                if (line == null) break;
                JsonProbe message = TestJson.ParseRoot(line);
                if (message.IsObject && (AnswerServerRequest(channel, line) || message.Has("method"))) continue;
                return message;
            }

            throw new TimeoutException($"No answer arrived over {channel.Transport}.");
        }

        private static void AssertQuiet(IMcpRawChannel channel, string id, List<string> seen, string message)
        {
            DateTime until = DateTime.UtcNow + _Quiet;
            while (DateTime.UtcNow < until)
            {
                string? line = channel.Receive(until - DateTime.UtcNow);
                if (line == null) break;
                JsonProbe probe = TestJson.ParseRoot(line);
                if (probe.IsObject && probe.Has("id") && !probe.Has("method")) seen.Add(probe.Get("id").String() ?? String.Empty);
            }

            TestAssert.False(seen.Contains(id), message);
        }

        private static bool AnswerServerRequest(IMcpRawChannel channel, string line)
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(line);
            System.Text.Json.JsonElement root = document.RootElement;
            if (!root.TryGetProperty("method", out System.Text.Json.JsonElement method) || !root.TryGetProperty("id", out System.Text.Json.JsonElement idElement)) return false;
            string id = idElement.GetRawText();
            string result = method.GetString() == "ping"
                ? "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{}}"
                : "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":-32601,\"message\":\"Method not found\"}}";
            channel.SendAsync(result, CancellationToken.None).GetAwaiter().GetResult();
            return true;
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "matrix", "revision", "conformance" });
        }
    }
}
