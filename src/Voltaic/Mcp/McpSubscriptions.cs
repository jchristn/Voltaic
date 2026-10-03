namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Core;

    /// <summary>
    /// The server's <c>subscriptions/listen</c> streams (2026-07-28 subscribe and notify). A stream starts with
    /// <c>notifications/subscriptions/acknowledged</c> carrying the notification types the server agreed to honor, then
    /// delivers only those, each tagged with the subscription ID. It ends without a response when the client cancels it
    /// or the transport closes, and with a completion result when the server stops (graceful closure). Thread-safe.
    /// </summary>
    internal sealed class McpSubscriptions
    {
        private readonly McpEndpoint _Endpoint;
        private readonly ConcurrentDictionary<McpSubscription, byte> _Active = new ConcurrentDictionary<McpSubscription, byte>();
        private CancellationTokenSource _Closing = new CancellationTokenSource();

        internal McpSubscriptions(McpEndpoint endpoint)
        {
            _Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        }

        /// <summary>
        /// Gets the number of open subscriptions.
        /// </summary>
        internal int Count => _Active.Count;

        /// <summary>
        /// Handles <c>subscriptions/listen</c>: acknowledges, then streams notifications until the request is cancelled
        /// (no response) or the server closes the subscriptions (a completion result).
        /// </summary>
        /// <exception cref="McpProtocolException">Thrown with <c>-32601</c> outside 2026-07-28, and <c>-32602</c> for an invalid filter.</exception>
        internal async Task<object> ListenAsync(RpcParameters? args, CancellationToken token)
        {
            McpRequestScope? scope = McpRequestScope.Current;
            if (scope == null || scope.StatelessVersion == null || scope.Request == null || scope.Notify == null)
            {
                throw McpProtocolException.MethodNotFound("Method 'subscriptions/listen' exists only in protocol version 2026-07-28.");
            }

            JsonElement subscriptionId;
            using (JsonDocument id = JsonDocument.Parse(scope.Request.IdKey, JsonLimits.Document))
            {
                subscriptionId = id.RootElement.Clone();
            }

            McpSubscription subscription = ParseFilter(args, subscriptionId, scope.Notify);

            // The acknowledgment is the first message of the subscription; notifications follow only after it. The
            // subscription is active before the acknowledgment is written, so a change made as soon as the client reads
            // the acknowledgment is never missed; notifications for it wait until the acknowledgment is out.
            _Active[subscription] = 0;
            VoltaicInstruments.Subscription(1);
            try
            {
                try
                {
                    await subscription.SendAsync("notifications/subscriptions/acknowledged", new Dictionary<string, object?>(StringComparer.Ordinal) { { "notifications", subscription.Filter() } }, token).ConfigureAwait(false);
                }
                finally
                {
                    subscription.MarkAcknowledged();
                }

                using (CancellationTokenSource ended = CancellationTokenSource.CreateLinkedTokenSource(token, _Closing.Token))
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, ended.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        // The server closed the subscription: answer the request so the client knows it ended cleanly.
                    }
                }

                token.ThrowIfCancellationRequested();
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    { "_meta", new Dictionary<string, object?>(StringComparer.Ordinal) { { McpProtocol.MetaSubscriptionIdKey, subscriptionId } } }
                };
            }
            finally
            {
                if (_Active.TryRemove(subscription, out byte _)) VoltaicInstruments.Subscription(-1);
            }
        }

        /// <summary>
        /// Sends a list-changed notification to the subscriptions that asked for it.
        /// </summary>
        internal Task ListChangedAsync(string method, CancellationToken token)
        {
            return SendAsync(method, null, null, token);
        }

        /// <summary>
        /// Sends <c>notifications/resources/updated</c> to the subscriptions that name the resource.
        /// </summary>
        internal Task ResourceUpdatedAsync(string uri, CancellationToken token)
        {
            return SendAsync("notifications/resources/updated", uri, new Dictionary<string, object?>(StringComparer.Ordinal) { { "uri", uri } }, token);
        }

        /// <summary>
        /// Ends every open subscription gracefully (each gets its completion result), for example when the server stops.
        /// Later subscriptions work normally.
        /// </summary>
        internal void CloseAll()
        {
            CancellationTokenSource closing = Interlocked.Exchange(ref _Closing, new CancellationTokenSource());
            try
            {
                closing.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Closes every subscription gracefully and waits up to <paramref name="timeout"/> for their completion results to
        /// be handed to the transport, so a server that is stopping sends them before it closes its connections.
        /// </summary>
        internal void CloseAllAndWait(TimeSpan timeout)
        {
            if (_Active.IsEmpty) return;
            CloseAll();
            DateTime deadline = DateTime.UtcNow + timeout;
            while (!_Active.IsEmpty && DateTime.UtcNow < deadline) Thread.Sleep(10);

            // The completion result is written just after the handler returns.
            Thread.Sleep(50);
        }

        private async Task SendAsync(string method, string? uri, Dictionary<string, object?>? parameters, CancellationToken token)
        {
            List<Task> sends = new List<Task>();
            foreach (McpSubscription subscription in _Active.Keys.Where(candidate => candidate.Wants(method, uri)))
            {
                // Each subscription gets its own parameters, since the tag differs.
                Dictionary<string, object?>? copy = parameters == null ? null : new Dictionary<string, object?>(parameters, StringComparer.Ordinal);
                sends.Add(SendAfterAcknowledgmentAsync(subscription, method, copy, token));
            }

            await Task.WhenAll(sends).ConfigureAwait(false);
        }

        private static async Task SendAfterAcknowledgmentAsync(McpSubscription subscription, string method, Dictionary<string, object?>? parameters, CancellationToken token)
        {
            await subscription.Acknowledged.ConfigureAwait(false);
            await subscription.SendAsync(method, parameters, token).ConfigureAwait(false);
        }

        // The filter the server honors: list changes when it sends them, resource updates when it supports resource
        // subscriptions; unsupported types are left out of the acknowledgment.
        private McpSubscription ParseFilter(RpcParameters? args, JsonElement subscriptionId, Func<JsonRpcRequest, CancellationToken, Task> notify)
        {
            JsonElement parameters;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(args?.RawJson ?? "{}"))
                {
                    parameters = document.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                throw McpProtocolException.InvalidParams("subscriptions/listen params must be a JSON object.");
            }

            if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("notifications", out JsonElement filter) || filter.ValueKind != JsonValueKind.Object)
            {
                throw McpProtocolException.InvalidParams("subscriptions/listen requires a notifications object.");
            }

            bool listChanged = _Endpoint.SupportsListChangedNotifications;
            List<string> uris = new List<string>();
            if (filter.TryGetProperty("resourceSubscriptions", out JsonElement resources))
            {
                if (resources.ValueKind != JsonValueKind.Array || resources.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                {
                    throw McpProtocolException.InvalidParams("notifications.resourceSubscriptions must be an array of strings.");
                }

                if (_Endpoint.SupportsResourceSubscriptions) uris.AddRange(resources.EnumerateArray().Select(item => item.GetString()!).Distinct(StringComparer.Ordinal));
            }

            return new McpSubscription(
                subscriptionId,
                listChanged && Flag(filter, "toolsListChanged"),
                listChanged && Flag(filter, "promptsListChanged"),
                listChanged && Flag(filter, "resourcesListChanged"),
                uris,
                notify);
        }

        private static bool Flag(JsonElement filter, string name)
        {
            if (!filter.TryGetProperty(name, out JsonElement value)) return false;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            throw McpProtocolException.InvalidParams($"notifications.{name} must be a boolean.");
        }
    }
}
