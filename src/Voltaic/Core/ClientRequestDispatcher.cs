namespace Voltaic.Core
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Answers JSON-RPC requests that a server sends to a client (for example the MCP <c>ping</c>, which either
    /// party may send and the receiver must answer). Shared by every Voltaic client transport. Thread-safe.
    /// </summary>
    internal sealed class ClientRequestDispatcher
    {
        internal const string PingMethod = "ping";

        private readonly ConcurrentDictionary<string, Func<RpcParameters?, CancellationToken, Task<object?>>> _Handlers =
            new ConcurrentDictionary<string, Func<RpcParameters?, CancellationToken, Task<object?>>>(StringComparer.Ordinal);

        /// <summary>
        /// Gets or sets whether <c>ping</c> is answered with an empty object (<c>{}</c>) and reserved, as the MCP
        /// specification requires of every party. Plain JSON-RPC clients leave it off.
        /// </summary>
        // Requests from the server that are running, by ID, so notifications/cancelled can stop them.
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _Running = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        // Cancellations that arrived before their request started, kept briefly.
        private readonly ConcurrentDictionary<string, DateTime> _EarlyCancels = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        internal bool AnswersPing { get; set; }

        // Receives diagnostic messages, such as the reason the server gave for cancelling a request.
        internal Action<string>? Log { get; set; }

        /// <summary>
        /// Handles a <c>notifications/cancelled</c> from the server: the matching request's handler token is cancelled
        /// and no response is sent for it. Returns true when the notification was a cancellation.
        /// </summary>
        internal bool TryHandleCancellation(JsonRpcRequest notification)
        {
            if (notification == null || !StringComparer.Ordinal.Equals(notification.Method, "notifications/cancelled")) return false;
            string? idKey = null;
            string reason = "none given";
            try
            {
                JsonElement parameters = notification.Params is JsonElement element ? element : JsonSerializer.SerializeToElement(notification.Params);
                if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("requestId", out JsonElement requestId)) idKey = IdKey(requestId);
                if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("reason", out JsonElement reasonElement) && reasonElement.ValueKind == JsonValueKind.String)
                {
                    reason = reasonElement.GetString() ?? reason;
                }
            }
            catch (JsonException)
            {
            }
            catch (NotSupportedException)
            {
            }

            if (idKey == null) return true;
            Log?.Invoke($"The server cancelled request {idKey} (reason: {reason}).");
            if (_Running.TryGetValue(idKey, out CancellationTokenSource? running))
            {
                try
                {
                    running.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
            else
            {
                DateTime now = DateTime.UtcNow;
                foreach (KeyValuePair<string, DateTime> entry in _EarlyCancels)
                {
                    if (now - entry.Value > TimeSpan.FromSeconds(30)) _EarlyCancels.TryRemove(entry.Key, out DateTime _);
                }

                if (_EarlyCancels.Count < 256) _EarlyCancels[idKey] = now;
            }

            return true;
        }

        private static string? IdKey(object? id)
        {
            if (id == null) return null;
            JsonElement element = id is JsonElement json ? json : JsonSerializer.SerializeToElement(id);
            return element.ValueKind == JsonValueKind.String || element.ValueKind == JsonValueKind.Number ? element.GetRawText() : null;
        }

        /// <summary>
        /// Registers or replaces the handler for a method. Throws for a null or empty method, a null handler, or
        /// <c>ping</c> when <see cref="AnswersPing"/> is true.
        /// </summary>
        internal void Register(string method, Func<RpcParameters?, CancellationToken, Task<object?>> handler)
        {
            if (String.IsNullOrWhiteSpace(method)) throw new ArgumentNullException(nameof(method));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (AnswersPing && StringComparer.Ordinal.Equals(method, PingMethod))
            {
                throw new ArgumentException("ping is answered by the client itself and cannot be replaced.", nameof(method));
            }

            _Handlers[method] = handler;
        }

        /// <summary>
        /// Returns true when a handler is registered for the method.
        /// </summary>
        internal bool HasHandler(string method)
        {
            return !String.IsNullOrEmpty(method) && _Handlers.ContainsKey(method);
        }

        /// <summary>
        /// Removes the handler for a method. Returns true when one was registered.
        /// </summary>
        internal bool Unregister(string method)
        {
            if (String.IsNullOrWhiteSpace(method)) throw new ArgumentNullException(nameof(method));
            return _Handlers.TryRemove(method, out _);
        }

        /// <summary>
        /// Returns the message as a request when it is a JSON object with a string <c>method</c> and a non-null
        /// <c>id</c>; otherwise null (responses, notifications, and malformed input).
        /// </summary>
        internal static JsonRpcRequest? ParseRequest(string json)
        {
            if (String.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return null;
                    if (!root.TryGetProperty("method", out JsonElement method) || method.ValueKind != JsonValueKind.String) return null;
                    if (!root.TryGetProperty("id", out JsonElement id) || id.ValueKind == JsonValueKind.Null) return null;
                }

                return JsonSerializer.Deserialize<JsonRpcRequest>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Runs the handler for a request and returns the response to send back. Unknown methods get
        /// <c>-32601</c>; a handler exception that implements <see cref="IJsonRpcErrorProvider"/> supplies its own
        /// error; any other exception becomes <c>-32603</c> without its details.
        /// </summary>
        internal async Task<JsonRpcResponse?> DispatchAsync(JsonRpcRequest request, CancellationToken token)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (AnswersPing && StringComparer.Ordinal.Equals(request.Method, PingMethod))
            {
                return new JsonRpcResponse { Id = request.Id, Result = new Dictionary<string, object?>() };
            }

            if (!_Handlers.TryGetValue(request.Method, out Func<RpcParameters?, CancellationToken, Task<object?>>? handler))
            {
                JsonRpcError notFound = JsonRpcError.MethodNotFound();
                notFound.Message = $"Method not found: {request.Method}";
                return new JsonRpcResponse { Id = request.Id, Error = notFound };
            }

            string? idKey = IdKey(request.Id);
            using CancellationTokenSource running = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (idKey != null)
            {
                _Running[idKey] = running;
                if (_EarlyCancels.TryRemove(idKey, out DateTime _)) running.Cancel();
            }

            try
            {
                if (running.IsCancellationRequested && !token.IsCancellationRequested) return null;
                RpcParameters? parameters = request.Params == null ? null : RpcParameters.FromObject(request.Params);
                object? result = await handler(parameters, running.Token).ConfigureAwait(false);
                if (running.IsCancellationRequested && !token.IsCancellationRequested) return null;
                return new JsonRpcResponse { Id = request.Id, Result = result ?? new Dictionary<string, object?>() };
            }
            catch (OperationCanceledException) when (running.IsCancellationRequested && !token.IsCancellationRequested)
            {
                // The server cancelled the request: no response is sent.
                return null;
            }
            catch (Exception error) when (error is IJsonRpcErrorProvider provider)
            {
                if (running.IsCancellationRequested && !token.IsCancellationRequested) return null;
                return new JsonRpcResponse { Id = request.Id, Error = provider.ToJsonRpcError() };
            }
            catch (Exception)
            {
                if (running.IsCancellationRequested && !token.IsCancellationRequested) return null;
                return new JsonRpcResponse { Id = request.Id, Error = JsonRpcError.InternalError() };
            }
            finally
            {
                if (idKey != null) _Running.TryRemove(new KeyValuePair<string, CancellationTokenSource>(idKey, running));
            }
        }
    }
}
