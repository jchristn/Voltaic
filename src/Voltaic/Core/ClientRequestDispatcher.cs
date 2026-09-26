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
        internal bool AnswersPing { get; set; }

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
        internal async Task<JsonRpcResponse> DispatchAsync(JsonRpcRequest request, CancellationToken token)
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

            try
            {
                RpcParameters? parameters = request.Params == null ? null : RpcParameters.FromObject(request.Params);
                object? result = await handler(parameters, token).ConfigureAwait(false);
                return new JsonRpcResponse { Id = request.Id, Result = result ?? new Dictionary<string, object?>() };
            }
            catch (Exception error) when (error is IJsonRpcErrorProvider provider)
            {
                return new JsonRpcResponse { Id = request.Id, Error = provider.ToJsonRpcError() };
            }
            catch (Exception)
            {
                return new JsonRpcResponse { Id = request.Id, Error = JsonRpcError.InternalError() };
            }
        }
    }
}
