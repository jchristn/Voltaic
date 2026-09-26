namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Core;

    /// <summary>
    /// Sends server-initiated MCP notifications with the specification's targeting rules, shared by every transport:
    /// list changes go to sessions that completed the handshake (stateless-era clients receive them only through
    /// <c>subscriptions/listen</c>), resource updates only to sessions subscribed to the resource, log messages only
    /// at or above each session's level, and progress only to the request that carries the progress token.
    /// </summary>
    internal static class McpServerNotifications
    {
        /// <summary>
        /// Sends a list-changed notification to every initialized session.
        /// </summary>
        internal static Task ListChangedAsync(IEnumerable<McpSessionState> sessions, string method, CancellationToken token)
        {
            JsonRpcRequest notification = new JsonRpcRequest { Method = method };
            return SendAsync(sessions.Where(session => session.IsInitialized), notification, token);
        }

        /// <summary>
        /// Sends <c>notifications/resources/updated</c> to sessions subscribed to <paramref name="uri"/>.
        /// </summary>
        internal static Task ResourceUpdatedAsync(IEnumerable<McpSessionState> sessions, string uri, CancellationToken token)
        {
            if (String.IsNullOrEmpty(uri)) throw new ArgumentNullException(nameof(uri));
            JsonRpcRequest notification = new JsonRpcRequest { Method = "notifications/resources/updated", Params = new { uri } };
            return SendAsync(sessions.Where(session => session.IsInitialized && session.IsSubscribed(uri)), notification, token);
        }

        /// <summary>
        /// Sends <c>notifications/message</c> to initialized sessions whose level it meets.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when <paramref name="level"/> is not an MCP log level.</exception>
        internal static Task LogAsync(IEnumerable<McpSessionState> sessions, string level, object? data, string? logger, CancellationToken token)
        {
            if (!McpLogLevels.IsValid(level)) throw new ArgumentException($"'{level}' is not an MCP log level (debug, info, notice, warning, error, critical, alert, emergency).", nameof(level));
            JsonRpcRequest notification = new JsonRpcRequest
            {
                Method = "notifications/message",
                Params = new McpLogMessageNotification { Level = level, Logger = logger, Data = data }
            };

            return SendAsync(sessions.Where(session => session.IsInitialized && McpLogLevels.Passes(level, session.LogLevel)), notification, token);
        }

        /// <summary>
        /// Sends <c>notifications/progress</c> to the in-flight request that carries <paramref name="progressToken"/>.
        /// Returns false when no active request carries it (progress may reference only tokens of active requests).
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="progress"/> does not increase.</exception>
        internal static async Task<bool> ProgressAsync(IEnumerable<McpSessionState> sessions, object progressToken, double progress, double? total, string? message, CancellationToken token)
        {
            if (progressToken == null) throw new ArgumentNullException(nameof(progressToken));
            string tokenJson = progressToken is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(progressToken);

            foreach (McpSessionState session in sessions)
            {
                McpInFlightRequest? request = session.FindByProgressToken(tokenJson);
                if (request == null || request.Notify == null) continue;
                if (!request.TryRecordProgress(progress))
                {
                    throw new ArgumentOutOfRangeException(nameof(progress), "Progress must increase with each notification.");
                }

                JsonRpcRequest notification = new JsonRpcRequest
                {
                    Method = "notifications/progress",
                    Params = new McpProgressNotification { ProgressToken = request.ProgressToken!.Value, Progress = progress, Total = total, Message = message }
                };

                await request.Notify(notification, token).ConfigureAwait(false);
                return true;
            }

            return false;
        }

        private static async Task SendAsync(IEnumerable<McpSessionState> sessions, JsonRpcRequest notification, CancellationToken token)
        {
            List<Task> sends = new List<Task>();
            foreach (McpSessionState session in sessions)
            {
                Func<string, CancellationToken, Task>? push = session.Push;
                if (push == null) continue;
                sends.Add(SafePushAsync(push, McpMessageProcessor.SerializeNotification(notification, session), token));
            }

            await Task.WhenAll(sends).ConfigureAwait(false);
        }

        private static async Task SafePushAsync(Func<string, CancellationToken, Task> push, string json, CancellationToken token)
        {
            try
            {
                await push(json, token).ConfigureAwait(false);
            }
            catch
            {
                // The connection may have closed.
            }
        }
    }
}
