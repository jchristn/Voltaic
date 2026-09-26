namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers the GET SSE stream of <see cref="McpHttpServer"/>: a priming event (event ID and empty data) with the
    /// <c>retry</c> interval, per-stream event IDs, and replay of missed events to a client that reconnects with
    /// <c>Last-Event-ID</c>, limited to the session's own streams.
    /// </summary>
    public static class McpHttpSseResumabilitySuites
    {
        private static readonly TimeSpan _Wait = TimeSpan.FromSeconds(10);

        /// <summary>
        /// SSE priming and resumability cases.
        /// </summary>
        public static TestSuiteDescriptor Resumability()
        {
            const string suiteId = "McpHttp.SseResumability";
            return new TestSuiteDescriptor(
                suiteId,
                "MCP HTTP SSE priming, event IDs, and Last-Event-ID resumption",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "ReconnectReplaysMissedEvents", "A client that reconnects with Last-Event-ID receives the events after that ID on the same stream, with no new priming event", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        string sessionId = (await fixture.InitializeSessionAsync(ct).ConfigureAwait(false))!;

                        string primingId;
                        string firstEventId;
                        using (SseReader first = await SseReader.OpenAsync(fixture, sessionId, null, ct).ConfigureAwait(false))
                        {
                            primingId = (await first.ReadEventAsync(ct).ConfigureAwait(false)).Id!;
                            fixture.Server.SendNotificationToSession(sessionId, "notifications/one", new { n = 1 });
                            fixture.Server.SendNotificationToSession(sessionId, "notifications/two", new { n = 2 });
                            firstEventId = (await first.ReadEventAsync(ct).ConfigureAwait(false)).Id!;
                            await first.ReadEventAsync(ct).ConfigureAwait(false);
                        }

                        string streamId = primingId.Substring(0, primingId.LastIndexOf('-'));
                        TestAssert.True(primingId.EndsWith("-0", StringComparison.Ordinal), $"The priming event ID ends in -0: {primingId}");
                        TestAssert.Equal(streamId + "-1", firstEventId, "Events are numbered on the stream.");

                        using SseReader resumed = await SseReader.OpenAsync(fixture, sessionId, firstEventId, ct).ConfigureAwait(false);
                        SseTestEvent replayed = await resumed.ReadEventAsync(ct).ConfigureAwait(false);
                        TestAssert.Equal(streamId + "-2", replayed.Id, "Only the event after Last-Event-ID is replayed, on the same stream.");
                        TestAssert.True(replayed.Data.Contains("notifications/two"), $"The missed notification is replayed: {replayed.Data}");
                        TestAssert.Equal("1000", resumed.Retry, "The resumed stream restates the retry interval.");

                        fixture.Server.SendNotificationToSession(sessionId, "notifications/three", new { n = 3 });
                        SseTestEvent next = await resumed.ReadEventAsync(ct).ConfigureAwait(false);
                        TestAssert.Equal(streamId + "-3", next.Id, "New events continue the stream's numbering.");
                    }),

                    Case(suiteId, "UnknownOrForeignEventIdStartsNewStream", "An unknown Last-Event-ID, or one from another session, starts a new stream and replays nothing", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        string alice = (await fixture.InitializeSessionAsync(ct).ConfigureAwait(false))!;
                        string bob = (await fixture.InitializeSessionAsync(ct).ConfigureAwait(false))!;

                        string aliceEvent;
                        using (SseReader aliceStream = await SseReader.OpenAsync(fixture, alice, null, ct).ConfigureAwait(false))
                        {
                            await aliceStream.ReadEventAsync(ct).ConfigureAwait(false);
                            fixture.Server.SendNotificationToSession(alice, "notifications/private", new { secret = "alice-only" });
                            aliceEvent = (await aliceStream.ReadEventAsync(ct).ConfigureAwait(false)).Id!;
                        }

                        string aliceStart = aliceEvent.Substring(0, aliceEvent.LastIndexOf('-')) + "-0";
                        using SseReader bobStream = await SseReader.OpenAsync(fixture, bob, aliceStart, ct).ConfigureAwait(false);
                        SseTestEvent bobFirst = await bobStream.ReadEventAsync(ct).ConfigureAwait(false);
                        TestAssert.True(bobFirst.Id!.EndsWith("-0", StringComparison.Ordinal) && bobFirst.Data.Length == 0, "Another session's event ID starts a new stream with a priming event.");
                        TestAssert.False(bobFirst.Id.StartsWith(aliceEvent.Substring(0, aliceEvent.LastIndexOf('-')), StringComparison.Ordinal), "The new stream has its own ID.");

                        using SseReader bogus = await SseReader.OpenAsync(fixture, alice, "not-an-event-id", ct).ConfigureAwait(false);
                        SseTestEvent bogusFirst = await bogus.ReadEventAsync(ct).ConfigureAwait(false);
                        TestAssert.True(bogusFirst.Id!.EndsWith("-0", StringComparison.Ordinal) && bogusFirst.Data.Length == 0, "An unknown ID starts a new stream with a priming event.");
                    }),

                    Case(suiteId, "SseSettingsAreConfigurableAndValidated", "SseRetryIntervalMs appears in the priming event, a zero replay buffer replays nothing, and out-of-range values are rejected", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.SseRetryIntervalMs = 250;
                            server.SseReplayBufferSize = 0;
                        }).ConfigureAwait(false);
                        string sessionId = (await fixture.InitializeSessionAsync(ct).ConfigureAwait(false))!;

                        string eventId;
                        using (SseReader stream = await SseReader.OpenAsync(fixture, sessionId, null, ct).ConfigureAwait(false))
                        {
                            await stream.ReadEventAsync(ct).ConfigureAwait(false);
                            TestAssert.Equal("250", stream.Retry, "The configured retry interval is sent.");
                            fixture.Server.SendNotificationToSession(sessionId, "notifications/gone", new { });
                            eventId = (await stream.ReadEventAsync(ct).ConfigureAwait(false)).Id!;
                        }

                        string start = eventId.Substring(0, eventId.LastIndexOf('-')) + "-0";
                        using SseReader resumed = await SseReader.OpenAsync(fixture, sessionId, start, ct).ConfigureAwait(false);
                        fixture.Server.SendNotificationToSession(sessionId, "notifications/fresh", new { });
                        SseTestEvent first = await resumed.ReadEventAsync(ct).ConfigureAwait(false);
                        TestAssert.True(first.Data.Contains("notifications/fresh"), "With no replay buffer the missed event is not replayed.");

                        using McpHttpServer server = new McpHttpServer("localhost", TestPorts.GetFreePort());
                        TestAssert.Equal(100, server.SseReplayBufferSize, "The default replay buffer is 100 events.");
                        TestAssert.Equal(1000, server.SseRetryIntervalMs, "The default retry interval is 1000 ms.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => server.SseReplayBufferSize = -1, "A negative buffer is rejected.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => server.SseReplayBufferSize = 10001, "An oversized buffer is rejected.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => server.SseRetryIntervalMs = -1, "A negative retry is rejected.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => server.SseRetryIntervalMs = 600001, "An oversized retry is rejected.");
                    }),

                    Case(suiteId, "VoltaicClientResumesVoltaicServer", "McpHttpClient against McpHttpServer: a notification queued while the GET stream is down is delivered after the client reconnects", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server => server.SseRetryIntervalMs = 50).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();
                        List<string> received = new List<string>();
                        client.NotificationReceived += (sender, notification) =>
                        {
                            lock (received) received.Add(notification.Method);
                        };
                        TestAssert.True(await client.ConnectStreamableAsync(fixture.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The client connects.");
                        await client.StartSseAsync(ct).ConfigureAwait(false);

                        fixture.Server.SendNotificationToSession(client.SessionId!, "notifications/live", new { });
                        TestAssert.True(await WaitUntilAsync(() => Count(received) >= 1, ct).ConfigureAwait(false), "The live notification arrives.");
                        TestAssert.True(Count(received) == 1, "The priming event raises nothing.");
                    }),
                });
        }

        private static int Count(List<string> items)
        {
            lock (items) return items.Count;
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "http", "sse" });
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow + _Wait;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(20, token).ConfigureAwait(false);
            }

            return condition();
        }

        /// <summary>
        /// Reads events from a GET /mcp stream opened with an optional Last-Event-ID.
        /// </summary>
        private sealed class SseReader : IDisposable
        {
            private readonly HttpResponseMessage _Response;
            private readonly StreamReader _Reader;

            private SseReader(HttpResponseMessage response, StreamReader reader)
            {
                _Response = response;
                _Reader = reader;
            }

            public string? Retry { get; private set; }

            public static async Task<SseReader> OpenAsync(HttpMcpTestServerFixture fixture, string sessionId, string? lastEventId, CancellationToken token)
            {
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}/mcp/");
                request.Headers.Add(McpProtocol.SessionIdHeader, sessionId);
                request.Headers.Accept.ParseAdd("text/event-stream");
                if (lastEventId != null) request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
                HttpResponseMessage response = await fixture.SendRawAsync(request, token).ConfigureAwait(false);
                TestAssert.Equal(HttpStatusCode.OK, response.StatusCode, "The stream opens.");
                Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                return new SseReader(response, new StreamReader(stream));
            }

            public async Task<SseTestEvent> ReadEventAsync(CancellationToken token)
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(_Wait);
                string? id = null;
                string? data = null;
                while (true)
                {
                    string? line = await _Reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                    if (line == null) throw new IOException("The stream ended.");
                    if (line.Length == 0)
                    {
                        if (data != null) return new SseTestEvent(id, data);
                        continue;
                    }

                    if (line.StartsWith(":", StringComparison.Ordinal)) continue;
                    if (line.StartsWith("id:", StringComparison.Ordinal)) id = line.Substring(3).TrimStart();
                    else if (line.StartsWith("retry:", StringComparison.Ordinal)) Retry = line.Substring(6).TrimStart();
                    else if (line.StartsWith("data:", StringComparison.Ordinal)) data = line.Substring(5).TrimStart();
                }
            }

            public void Dispose()
            {
                _Reader.Dispose();
                _Response.Dispose();
            }
        }
    }
}
