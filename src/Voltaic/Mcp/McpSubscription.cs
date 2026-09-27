namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Core;

    /// <summary>
    /// One active <c>subscriptions/listen</c> stream (2026-07-28): the notification types the server agreed to send, the
    /// channel of the request that opened it, and its subscription ID (the request's JSON-RPC ID). Thread-safe.
    /// </summary>
    internal sealed class McpSubscription
    {
        private readonly Func<JsonRpcRequest, CancellationToken, Task> _Notify;
        private readonly HashSet<string> _ResourceUris;

        internal McpSubscription(JsonElement subscriptionId, bool toolsListChanged, bool promptsListChanged, bool resourcesListChanged, IEnumerable<string> resourceUris, Func<JsonRpcRequest, CancellationToken, Task> notify)
        {
            SubscriptionId = subscriptionId;
            ToolsListChanged = toolsListChanged;
            PromptsListChanged = promptsListChanged;
            ResourcesListChanged = resourcesListChanged;
            _ResourceUris = new HashSet<string>(resourceUris, StringComparer.Ordinal);
            _Notify = notify ?? throw new ArgumentNullException(nameof(notify));
        }

        /// <summary>
        /// Gets the subscription ID: the JSON-RPC ID of the <c>subscriptions/listen</c> request.
        /// </summary>
        internal JsonElement SubscriptionId { get; }

        internal bool ToolsListChanged { get; }

        internal bool PromptsListChanged { get; }

        internal bool ResourcesListChanged { get; }

        internal IReadOnlyCollection<string> ResourceUris => _ResourceUris;

        /// <summary>
        /// Gets whether the subscription asked for this notification: a list change of the kind, or an update of a
        /// resource it names.
        /// </summary>
        internal bool Wants(string method, string? uri)
        {
            switch (method)
            {
                case "notifications/tools/list_changed":
                    return ToolsListChanged;
                case "notifications/prompts/list_changed":
                    return PromptsListChanged;
                case "notifications/resources/list_changed":
                    return ResourcesListChanged;
                case "notifications/resources/updated":
                    return uri != null && _ResourceUris.Contains(uri);
                default:
                    return false;
            }
        }

        /// <summary>
        /// The <c>notifications</c> filter the server agreed to honor, for the acknowledgment.
        /// </summary>
        internal Dictionary<string, object?> Filter()
        {
            Dictionary<string, object?> filter = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (ToolsListChanged) filter["toolsListChanged"] = true;
            if (PromptsListChanged) filter["promptsListChanged"] = true;
            if (ResourcesListChanged) filter["resourcesListChanged"] = true;
            if (_ResourceUris.Count > 0) filter["resourceSubscriptions"] = new List<string>(_ResourceUris);
            return filter;
        }

        /// <summary>
        /// Sends a notification on the subscription's stream, tagged with its subscription ID.
        /// </summary>
        internal async Task SendAsync(string method, Dictionary<string, object?>? parameters, CancellationToken token)
        {
            Dictionary<string, object?> tagged = parameters ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            tagged["_meta"] = new Dictionary<string, object?>(StringComparer.Ordinal) { { McpProtocol.MetaSubscriptionIdKey, SubscriptionId } };
            try
            {
                await _Notify(new JsonRpcRequest { Method = method, Params = tagged }, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is ObjectDisposedException || ex is InvalidOperationException || ex is OperationCanceledException)
            {
                // The stream closed; the subscription ends when its request is cancelled.
            }
        }
    }
}
