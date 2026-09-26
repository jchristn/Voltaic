namespace Voltaic.Core
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
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

        // Requests from the server that are running, by ID, so notifications/cancelled can stop them.
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _Running = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        // Cancellations that arrived before their request started, kept briefly.
        private readonly ConcurrentDictionary<string, DateTime> _EarlyCancels = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        // IDs of requests answered recently: a cancellation for one of them arrived too late and is ignored, so it can
        // never cancel a later request that reuses the ID.
        private readonly ConcurrentDictionary<string, DateTime> _Finished = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly TimeSpan _EntryLifetime = TimeSpan.FromSeconds(30);
        private const int _MaxFinished = 4096;

        /// <summary>
        /// Gets or sets whether MCP rules apply: <c>ping</c> is answered with an empty object (<c>{}</c>) and reserved,
        /// as the MCP specification requires of every party; a request with a null ID gets <c>-32600</c>; and a
        /// handler result that is not a JSON object gets <c>-32603</c>. Plain JSON-RPC clients leave it off.
        /// </summary>
        internal bool AnswersPing { get; set; }

        // Returns whether the governing protocol revision allows JSON-RPC batches, or null to accept them (plain JSON-RPC).
        internal Func<bool>? AcceptsBatches { get; set; }

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
            if (_Finished.ContainsKey(idKey) && !_Running.ContainsKey(idKey))
            {
                // Already answered: the cancellation crossed the response and is ignored.
                return true;
            }

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

        private static bool IsJsonObject(object value)
        {
            try
            {
                JsonElement element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value);
                return element.ValueKind == JsonValueKind.Object;
            }
            catch (Exception serializeError) when (serializeError is JsonException || serializeError is NotSupportedException)
            {
                return false;
            }
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
        /// Returns the message as a request when it is a JSON object with a string <c>method</c> and an <c>id</c>
        /// member; otherwise null (responses, notifications, and malformed input). A request whose <c>id</c> is null is
        /// returned with a null <see cref="JsonRpcRequest.Id"/>, so <see cref="DispatchAsync"/> can answer it.
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
                    if (!root.TryGetProperty("id", out JsonElement _)) return null;
                }

                return JsonSerializer.Deserialize<JsonRpcRequest>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Returns the error to send for a batch from the server when the governing revision allows no batches
        /// (MCP 2025-06-18 and later), or null when the batch is to be processed.
        /// </summary>
        internal JsonRpcResponse? RefuseBatch()
        {
            Func<bool>? accepts = AcceptsBatches;
            if (accepts == null || accepts()) return null;
            JsonRpcError error = JsonRpcError.InvalidRequest();
            error.Message = "Invalid Request: JSON-RPC batches are not supported in the negotiated protocol version.";
            return new JsonRpcResponse { Id = null, Error = error };
        }

        /// <summary>
        /// Runs the handler for a request and returns the response to send back. Unknown methods get
        /// <c>-32601</c>; a handler exception that implements <see cref="IJsonRpcErrorProvider"/> supplies its own
        /// error; any other exception becomes <c>-32603</c> without its details.
        /// </summary>
        internal async Task<JsonRpcResponse?> DispatchAsync(JsonRpcRequest request, CancellationToken token)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            string? idKey = IdKey(request.Id);
            if (idKey != null) _Finished.TryRemove(idKey, out DateTime _);
            try
            {
                return await DispatchCoreAsync(request, idKey, token).ConfigureAwait(false);
            }
            finally
            {
                if (idKey != null) RecordFinished(idKey);
            }
        }

        private void RecordFinished(string idKey)
        {
            if (_Running.ContainsKey(idKey)) return;
            _EarlyCancels.TryRemove(idKey, out DateTime _);
            DateTime now = DateTime.UtcNow;
            _Finished[idKey] = now;
            if (_Finished.Count < _MaxFinished) return;
            foreach (KeyValuePair<string, DateTime> entry in _Finished)
            {
                if (now - entry.Value > _EntryLifetime) _Finished.TryRemove(entry.Key, out DateTime _);
            }

            if (_Finished.Count >= _MaxFinished * 2)
            {
                foreach (KeyValuePair<string, DateTime> oldest in _Finished.OrderBy(entry => entry.Value).Take(_Finished.Count - _MaxFinished).ToList())
                {
                    _Finished.TryRemove(oldest.Key, out DateTime _);
                }
            }
        }

        private async Task<JsonRpcResponse?> DispatchCoreAsync(JsonRpcRequest request, string? idKey, CancellationToken token)
        {
            if (AnswersPing && request.Id == null)
            {
                // MCP request IDs must not be null.
                JsonRpcError nullId = JsonRpcError.InvalidRequest();
                nullId.Message = "Invalid Request: MCP request IDs must be a string or an integer, not null.";
                return new JsonRpcResponse { Id = null, Error = nullId };
            }

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
                if (AnswersPing && result != null && !IsJsonObject(result))
                {
                    // MCP results are always JSON objects.
                    Log?.Invoke($"The handler for {request.Method} returned a result that is not a JSON object; answering with an internal error.");
                    return new JsonRpcResponse { Id = request.Id, Error = JsonRpcError.InternalError() };
                }

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
