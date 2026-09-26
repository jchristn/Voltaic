namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Core;

    /// <summary>
    /// Processes MCP JSON-RPC messages the same way on every transport: envelope validation, JSON-RPC batches (only
    /// where the negotiated revision allows them), the initialization order, concurrent request handling, cancellation
    /// by <c>notifications/cancelled</c>, the stateless-era (<c>2026-07-28</c>) request rules, and serialization for the
    /// governing protocol version. Thread-safe.
    /// </summary>
    internal sealed class McpMessageProcessor
    {
        private static readonly HashSet<string> _AllowedBeforeInitialize = new HashSet<string>(StringComparer.Ordinal) { "initialize", "ping", "server/discover" };

        // Methods and notifications the stateless revision removed (or never had).
        private static readonly HashSet<string> _RemovedInStateless = new HashSet<string>(StringComparer.Ordinal)
        {
            "initialize", "ping", "logging/setLevel", "resources/subscribe", "resources/unsubscribe",
            "notifications/initialized", "notifications/roots/list_changed"
        };

        private readonly McpEndpoint _Endpoint;
        private readonly IDictionary<string, Func<RpcParameters?, CancellationToken, Task<object>>> _Methods;
        private readonly Action<string> _Log;

        internal McpMessageProcessor(McpEndpoint endpoint, IDictionary<string, Func<RpcParameters?, CancellationToken, Task<object>>> methods, Action<string> log)
        {
            _Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _Methods = methods ?? throw new ArgumentNullException(nameof(methods));
            _Log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// Gets or sets a callback raised for every well-formed request or notification before it is handled.
        /// </summary>
        internal Action<JsonRpcRequest, McpSessionState>? RequestReceived { get; set; }

        /// <summary>
        /// Gets or sets a callback raised for every response produced.
        /// </summary>
        internal Action<JsonRpcRequest?, JsonRpcResponse, McpSessionState>? ResponseProduced { get; set; }

        /// <summary>
        /// Processes one message received on a stream transport (stdio, TCP, WebSocket). Notifications are handled
        /// before this returns, in order; requests and batches run concurrently and write their responses through
        /// <paramref name="send"/>, which must serialize writes.
        /// </summary>
        internal async Task ProcessAsync(string raw, McpSessionState session, Func<string, CancellationToken, Task> send, CancellationToken token)
        {
            JsonElement root;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(raw))
                {
                    root = document.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                await SafeSendAsync(send, JsonSerializer.Serialize(new JsonRpcResponse { Error = JsonRpcError.ParseError(), Id = null }), token).ConfigureAwait(false);
                return;
            }

            Func<JsonRpcRequest, CancellationToken, Task> notify = (notification, ct) => SafeSendAsync(send, SerializeNotification(notification, session), ct);

            if (root.ValueKind == JsonValueKind.Array)
            {
                _ = Task.Run(() => ProcessBatchAsync(root, session, send, notify, token));
                return;
            }

            McpEnvelope envelope = McpEnvelope.Parse(root);
            if (envelope.Kind == McpEnvelopeKind.Request)
            {
                _ = Task.Run(async () =>
                {
                    string? json = await HandleAndSerializeAsync(envelope, session, null, false, notify, token, send).ConfigureAwait(false);
                    if (json != null) await SafeSendAsync(send, json, token).ConfigureAwait(false);
                });
                return;
            }

            string? immediate = await HandleAndSerializeAsync(envelope, session, null, false, notify, token, send).ConfigureAwait(false);
            if (immediate != null) await SafeSendAsync(send, immediate, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Handles one validated message and returns its serialized response, or null when there is nothing to send
        /// (a notification, a client response, or a request the client cancelled).
        /// </summary>
        /// <param name="envelope">The message.</param>
        /// <param name="session">The connection or session state.</param>
        /// <param name="statelessVersion">The stateless-era version already resolved by the transport, or null to resolve it from <c>_meta</c>.</param>
        /// <param name="statelessResolved">True when the transport resolved the era itself (HTTP), so <c>_meta</c> is not consulted again.</param>
        /// <param name="notify">The channel for notifications related to this request, or null.</param>
        /// <param name="token">The transport's cancellation token.</param>
        /// <param name="rawSend">The stream transport's writer, used unchanged for a stateless request's notifications, or null.</param>
        internal async Task<string?> HandleAndSerializeAsync(
            McpEnvelope envelope,
            McpSessionState session,
            string? statelessVersion,
            bool statelessResolved,
            Func<JsonRpcRequest, CancellationToken, Task>? notify,
            CancellationToken token,
            Func<string, CancellationToken, Task>? rawSend = null)
        {
            McpHandledResponse? handled = await HandleAsync(envelope, session, statelessVersion, statelessResolved, notify, token, rawSend).ConfigureAwait(false);
            if (handled == null) return null;
            return SerializeResponse(handled.Response, handled.StatelessVersion, envelope.Method, session);
        }

        /// <summary>
        /// Handles one validated message and returns the response and the era it was served under, or null when
        /// nothing is sent.
        /// </summary>
        internal async Task<McpHandledResponse?> HandleAsync(
            McpEnvelope envelope,
            McpSessionState session,
            string? statelessVersion,
            bool statelessResolved,
            Func<JsonRpcRequest, CancellationToken, Task>? notify,
            CancellationToken token,
            Func<string, CancellationToken, Task>? rawSend = null)
        {
            switch (envelope.Kind)
            {
                case McpEnvelopeKind.Response:
                    if (envelope.IdKey != null && session.TryCompletePing(envelope.IdKey)) return null;
                    _Log($"Ignoring a JSON-RPC response from the client (id {envelope.IdKey}) that answers no request this server sent.");
                    return null;
                case McpEnvelopeKind.Invalid:
                    if (envelope.Method != null && envelope.Id == null && envelope.Error != null && envelope.Error.Code == -32602)
                    {
                        // An invalid notification is dropped; notifications are never answered.
                        return null;
                    }

                    return Respond(null, new JsonRpcResponse { Id = envelope.ResponseId, Error = envelope.Error }, session, null);
            }

            string method = envelope.Method!;
            JsonRpcRequest request = envelope.ToRequest();
            RaiseRequestReceived(request, session);
            bool isRequest = envelope.Kind == McpEnvelopeKind.Request;

            // Era: a request whose _meta names a stateless-era revision is served statelessly on every transport.
            JsonElement? statelessCapabilities = null;
            string? requestLogLevel = null;
            if (!statelessResolved)
            {
                try
                {
                    statelessVersion = McpStatelessDispatcher.ResolveStatelessVersion(envelope.Message.GetRawText(), method);
                }
                catch (McpProtocolException versionError)
                {
                    McpProtocolException reported = versionError.Code == -32022
                        ? McpProtocolException.UnsupportedProtocolVersion(envelope.GetMeta(McpProtocol.MetaProtocolVersionKey)?.GetString() ?? String.Empty, _Endpoint.SupportedVersions())
                        : versionError;
                    return isRequest ? Respond(request, McpStatelessDispatcher.ErrorResponse(envelope.ResponseId, reported), session, null) : null;
                }
            }

            if (statelessVersion != null)
            {
                // A stateless request's notifications follow its own revision, never a version negotiated earlier on
                // the same connection.
                if (rawSend != null) notify = (notification, ct) => SafeSendAsync(rawSend, JsonSerializer.Serialize(notification), ct);

                McpProtocolException? metaError = ValidateStatelessMeta(envelope, out statelessCapabilities, out requestLogLevel);
                if (metaError != null)
                {
                    return isRequest ? Respond(request, McpStatelessDispatcher.ErrorResponse(envelope.ResponseId, metaError), session, statelessVersion) : null;
                }

                if (_RemovedInStateless.Contains(method))
                {
                    return isRequest ? Respond(request, MethodNotFound(envelope, $"Method '{method}' does not exist in protocol version {statelessVersion}."), session, statelessVersion) : null;
                }
            }
            else if (method == "server/discover")
            {
                // server/discover exists only in the stateless revision, whose requests must carry the protocol version.
                if (!isRequest) return null;
                return Respond(request, Error(envelope, -32602, $"server/discover requires params._meta[\"{McpProtocol.MetaProtocolVersionKey}\"] naming a supported protocol version."), session, null);
            }
            else if (session.RequireInitialize)
            {
                if (method == "initialize" && session.IsInitialized)
                {
                    return isRequest ? Respond(request, Error(envelope, -32600, "The session is already initialized; initialize may be sent only once."), session, null) : null;
                }

                if (!session.IsInitialized && !_AllowedBeforeInitialize.Contains(method) && method != "notifications/cancelled")
                {
                    if (!isRequest) return null;
                    return Respond(request, Error(envelope, -32600, $"Received '{method}' before initialization; send initialize first."), session, null);
                }
            }

            if (!isRequest)
            {
                await HandleNotificationAsync(envelope, request, session, token).ConfigureAwait(false);
                return null;
            }

            // Notification methods are never requests; one sent with an id is answered as an unknown method.
            if (method.StartsWith("notifications/", StringComparison.Ordinal))
            {
                return Respond(request, MethodNotFound(envelope, $"'{method}' is a notification and cannot be sent as a request."), session, statelessVersion);
            }

            // Only one initialize may run: a concurrent second one is refused like one sent after the first.
            bool claimedInitialize = false;
            if (method == "initialize" && statelessVersion == null && session.RequireInitialize)
            {
                if (!session.TryClaimInitialize())
                {
                    return Respond(request, Error(envelope, -32600, "The session is already initializing or initialized; initialize may be sent only once."), session, null);
                }

                claimedInitialize = true;
            }

            JsonElement? progressToken = envelope.GetMeta("progressToken");
            if (progressToken.HasValue && progressToken.Value.ValueKind != JsonValueKind.String && !McpEnvelope.IsInteger(progressToken.Value))
            {
                progressToken = null;
            }

            McpInFlightRequest? inFlight = session.TryBeginRequest(envelope.IdKey!, method, progressToken, token);
            if (inFlight == null)
            {
                return Respond(request, Error(envelope, -32600, $"A request with ID {envelope.IdKey} is already in progress; request IDs must be unique."), session, statelessVersion);
            }

            inFlight.Notify = notify;
            try
            {
                if (inFlight.IsCancelled)
                {
                    _Log($"Request {envelope.IdKey} ({method}) was cancelled before it started; no response is sent.");
                    return null;
                }

                JsonRpcResponse response;
                McpRequestScope scope = new McpRequestScope(session, inFlight, statelessVersion, requestLogLevel, statelessCapabilities, notify);
                using (McpRequestScope.Push(scope))
                {
                    McpRequestProtocol.Set(statelessVersion);
                    response = await InvokeAsync(envelope, request, method, inFlight.TokenSource.Token, scope).ConfigureAwait(false);
                }

                if (inFlight.IsCancelled)
                {
                    _Log($"Request {envelope.IdKey} ({method}) was cancelled by the client; no response is sent.");
                    return null;
                }

                if (claimedInitialize && response.Error != null) session.ReleaseInitializeClaim();

                if (method == "initialize" && response.Error == null)
                {
                    string negotiated = ReadString(response.Result, "protocolVersion") ?? _Endpoint.MaximumHandshakeProtocolVersion;
                    JsonElement? capabilities = envelope.Params.HasValue && envelope.Params.Value.TryGetProperty("capabilities", out JsonElement declared) ? declared.Clone() : (JsonElement?)null;
                    if (session.TryCompleteInitialize(negotiated, capabilities))
                    {
                        // Check the connection's health periodically (MCP ping utility); stream transports only.
                        session.StartPinging(_Endpoint.PingIntervalMs, _Endpoint.PingTimeoutMs, _Log);
                    }
                }

                McpHandledResponse handled = Respond(request, response, session, statelessVersion);
                handled.InsufficientScope = scope.InsufficientScope;
                return handled;
            }
            finally
            {
                session.EndRequest(inFlight);
            }
        }

        /// <summary>
        /// Serializes a response for the protocol version that governs it: stateless-era results get
        /// <c>resultType</c>, cache fields, and <c>serverInfo</c>; handshake-era results are reduced to what the
        /// negotiated revision defines.
        /// </summary>
        internal string SerializeResponse(JsonRpcResponse response, string? statelessVersion, string? method, McpSessionState session)
        {
            if (statelessVersion != null)
            {
                string stamped = McpStatelessDispatcher.SerializeResponse(response, statelessVersion);
                return FinishStatelessResponse(stamped);
            }

            string json = JsonSerializer.Serialize(response);
            if (response.Error == null && !IsObjectResult(response.Result))
            {
                // Every MCP result is a JSON object (a Result); a handler that returned something else is a server fault.
                return JsonSerializer.Serialize(new JsonRpcResponse
                {
                    Id = response.Id,
                    Error = new JsonRpcError { Code = -32603, Message = $"The handler for '{method}' returned a result that is not a JSON object." }
                });
            }

            string? version = session.NegotiatedVersion ?? (method == "initialize" ? ReadString(response.Result, "protocolVersion") : null);
            if (version == null || response.Error != null || method == null) return json;

            JsonNode? node = JsonNode.Parse(json);
            if (node is JsonObject envelope && envelope["result"] is JsonNode result)
            {
                McpVersionCompatibility.DowngradeResult(result, method, version);
                return envelope.ToJsonString();
            }

            return json;
        }

        private static bool IsObjectResult(object? result)
        {
            if (result == null) return false;
            if (result is McpResult || result is System.Collections.IDictionary) return true;
            if (result is JsonElement element) return element.ValueKind == JsonValueKind.Object;
            if (result is string || result is bool || result.GetType().IsPrimitive || result is decimal || result is System.Collections.IEnumerable && !(result is System.Collections.IDictionary))
            {
                return false;
            }

            return JsonSerializer.SerializeToElement(result).ValueKind == JsonValueKind.Object;
        }

        /// <summary>
        /// Serializes a server notification for a session, reduced to what its negotiated revision defines.
        /// </summary>
        internal static string SerializeNotification(JsonRpcRequest notification, McpSessionState session)
        {
            string json = JsonSerializer.Serialize(notification);
            string? version = session.NegotiatedVersion;
            if (version == null) return json;

            JsonNode? node = JsonNode.Parse(json);
            McpVersionCompatibility.DowngradeNotification(node, version);
            return node?.ToJsonString() ?? json;
        }

        private async Task ProcessBatchAsync(
            JsonElement batch,
            McpSessionState session,
            Func<string, CancellationToken, Task> send,
            Func<JsonRpcRequest, CancellationToken, Task> notify,
            CancellationToken token)
        {
            List<JsonElement> elements = batch.EnumerateArray().ToList();
            if (elements.Count == 0)
            {
                await SafeSendAsync(send, JsonSerializer.Serialize(new JsonRpcResponse { Id = null, Error = InvalidRequest("An empty JSON-RPC batch is not allowed.") }), token).ConfigureAwait(false);
                return;
            }

            string? version = session.NegotiatedVersion;
            McpProtocolVersionInfo? info = version == null ? null : McpProtocol.GetVersionInfo(version);
            if (info == null || !info.SupportsBatching)
            {
                string reason = version == null
                    ? "JSON-RPC batches are allowed only after initialize negotiates protocol version 2025-03-26."
                    : $"JSON-RPC batching is not supported in protocol version '{version}'.";
                await SafeSendAsync(send, JsonSerializer.Serialize(new JsonRpcResponse { Id = null, Error = InvalidRequest(reason) }), token).ConfigureAwait(false);
                return;
            }

            List<Task<string?>> pending = new List<Task<string?>>();
            foreach (JsonElement element in elements)
            {
                McpEnvelope envelope = McpEnvelope.Parse(element);
                if (envelope.Method == "initialize")
                {
                    // initialize must not be part of a JSON-RPC batch (2025-03-26).
                    pending.Add(Task.FromResult<string?>(JsonSerializer.Serialize(new JsonRpcResponse { Id = envelope.ResponseId, Error = InvalidRequest("initialize must not be part of a JSON-RPC batch.") })));
                    continue;
                }

                pending.Add(HandleAndSerializeAsync(envelope, session, null, false, notify, token, send));
            }

            string?[] results = await Task.WhenAll(pending).ConfigureAwait(false);
            List<string> responses = results.Where(result => result != null).Select(result => result!).ToList();
            if (responses.Count > 0)
            {
                await SafeSendAsync(send, "[" + String.Join(",", responses) + "]", token).ConfigureAwait(false);
            }
        }

        private async Task HandleNotificationAsync(McpEnvelope envelope, JsonRpcRequest request, McpSessionState session, CancellationToken token)
        {
            string method = envelope.Method!;
            if (method == "notifications/cancelled")
            {
                JsonElement? requestId = envelope.Params.HasValue && envelope.Params.Value.TryGetProperty("requestId", out JsonElement id) ? id : (JsonElement?)null;
                if (requestId.HasValue && (requestId.Value.ValueKind == JsonValueKind.String || requestId.Value.ValueKind == JsonValueKind.Number))
                {
                    bool cancelled = session.Cancel(requestId.Value.GetRawText());
                    _Log(cancelled ? $"Cancelled request {requestId.Value.GetRawText()} at the client's request." : $"Ignored a cancellation for unknown or finished request {requestId.Value.GetRawText()}.");
                }
            }
            else if (method == "notifications/initialized")
            {
                session.ClientInitialized = true;
            }

            if (_Methods.TryGetValue(method, out Func<RpcParameters?, CancellationToken, Task<object>>? handler))
            {
                try
                {
                    RpcParameters? parameters = request.Params == null ? null : RpcParameters.FromObject(request.Params);
                    await handler(parameters, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Log($"Error processing notification {method}: {ex.Message}");
                }
            }
        }

        private async Task<JsonRpcResponse> InvokeAsync(McpEnvelope envelope, JsonRpcRequest request, string method, CancellationToken token, McpRequestScope scope)
        {
            if (!_Methods.TryGetValue(method, out Func<RpcParameters?, CancellationToken, Task<object>>? handler))
            {
                return MethodNotFound(envelope, $"Method not found: {method}");
            }

            try
            {
                RpcParameters? parameters = request.Params == null ? null : RpcParameters.FromObject(request.Params);
                object result = await handler(parameters, token).ConfigureAwait(false);

                // A result response must carry a result member; a handler that returns null answers {}.
                return new JsonRpcResponse { Id = envelope.ResponseId, Result = result ?? new McpEmptyResult() };
            }
            catch (McpInsufficientScopeException scopeError)
            {
                scope.InsufficientScope = scopeError.RequiredScope;
                return new JsonRpcResponse { Id = envelope.ResponseId, Error = scopeError.ToJsonRpcError() };
            }
            catch (Exception ex) when (ex is IJsonRpcErrorProvider provider)
            {
                return new JsonRpcResponse { Id = envelope.ResponseId, Error = provider.ToJsonRpcError() };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                JsonRpcError cancelled = JsonRpcError.InternalError();
                cancelled.Message = "The request was cancelled.";
                return new JsonRpcResponse { Id = envelope.ResponseId, Error = cancelled };
            }
            catch (Exception ex)
            {
                // Internal details are logged, never sent: messages can carry connection strings, paths, or hosts.
                _Log($"Handler for {method} threw {ex.GetType().FullName}: {ex.Message}");
                return new JsonRpcResponse { Id = envelope.ResponseId, Error = JsonRpcError.InternalError() };
            }
        }

        /// <summary>
        /// Checks the per-request <c>_meta</c> fields of a stateless-era request body: <c>clientCapabilities</c> must be an
        /// object and <c>logLevel</c>, when present, a known level. Returns the error, or null when valid.
        /// </summary>
        internal static McpProtocolException? CheckStatelessMeta(string requestBody)
        {
            try
            {
                using (JsonDocument document = JsonDocument.Parse(requestBody))
                {
                    McpEnvelope envelope = McpEnvelope.Parse(document.RootElement);
                    return ValidateStatelessMeta(envelope, out JsonElement? _, out string? _);
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Validates the per-request _meta fields of a stateless-era request (2026-07-28): clientCapabilities is
        // required and must be an object; logLevel, when present, must be a known level.
        private static McpProtocolException? ValidateStatelessMeta(McpEnvelope envelope, out JsonElement? capabilities, out string? logLevel)
        {
            capabilities = null;
            logLevel = null;
            if (envelope.Kind != McpEnvelopeKind.Request) return null;

            JsonElement? declared = envelope.GetMeta(McpProtocol.MetaClientCapabilitiesKey);
            if (!declared.HasValue || declared.Value.ValueKind != JsonValueKind.Object)
            {
                return McpProtocolException.InvalidParams($"params._meta[\"{McpProtocol.MetaClientCapabilitiesKey}\"] is required and must be an object.");
            }

            capabilities = declared.Value.Clone();
            JsonElement? level = envelope.GetMeta(McpProtocol.MetaLogLevelKey);
            if (level.HasValue)
            {
                string? value = level.Value.ValueKind == JsonValueKind.String ? level.Value.GetString() : null;
                if (!McpLogLevels.IsValid(value))
                {
                    return McpProtocolException.InvalidParams($"params._meta[\"{McpProtocol.MetaLogLevelKey}\"] is not a recognized log level.");
                }

                logLevel = value;
            }

            return null;
        }

        // Stateless-era results must be JSON objects with resultType, and SHOULD carry serverInfo in _meta. This also
        // covers results of application methods that are not McpResult subclasses.
        private string FinishStatelessResponse(string json)
        {
            JsonNode? node = JsonNode.Parse(json);
            if (node is not JsonObject envelope) return json;
            if (!envelope.ContainsKey("result")) return json;

            if (envelope["result"] is not JsonObject result)
            {
                _Log("A method returned a non-object result, which protocol version 2026-07-28 does not allow; answering with an internal error.");
                JsonObject replaced = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["error"] = new JsonObject { ["code"] = -32603, ["message"] = "Internal error: the method returned a result that is not a JSON object." },
                    ["id"] = envelope["id"]?.DeepClone()
                };
                return replaced.ToJsonString();
            }

            if (!result.ContainsKey("resultType")) result["resultType"] = McpResult.ResultTypeComplete;

            if (result["_meta"] is not JsonObject meta)
            {
                meta = new JsonObject();
                result["_meta"] = meta;
            }

            if (!meta.ContainsKey(McpProtocol.MetaServerInfoKey))
            {
                meta[McpProtocol.MetaServerInfoKey] = new JsonObject { ["name"] = _Endpoint.ServerName, ["version"] = _Endpoint.ServerVersion };
            }

            // ttlMs must be a non-negative integer and cacheScope public or private.
            if (result["ttlMs"] is JsonValue ttl && (!ttl.TryGetValue(out long ttlValue) || ttlValue < 0)) result["ttlMs"] = 0;
            if (result["cacheScope"] is JsonValue scope && (!scope.TryGetValue(out string? scopeValue) || (scopeValue != "public" && scopeValue != "private"))) result["cacheScope"] = "private";

            return envelope.ToJsonString();
        }

        private McpHandledResponse Respond(JsonRpcRequest? request, JsonRpcResponse response, McpSessionState session, string? statelessVersion)
        {
            Action<JsonRpcRequest?, JsonRpcResponse, McpSessionState>? callback = ResponseProduced;
            if (callback != null)
            {
                try
                {
                    callback(request, response, session);
                }
                catch
                {
                }
            }

            return new McpHandledResponse(response, statelessVersion);
        }

        private void RaiseRequestReceived(JsonRpcRequest request, McpSessionState session)
        {
            Action<JsonRpcRequest, McpSessionState>? callback = RequestReceived;
            if (callback == null) return;
            try
            {
                callback(request, session);
            }
            catch
            {
            }
        }

        private async Task SafeSendAsync(Func<string, CancellationToken, Task> send, string json, CancellationToken token)
        {
            try
            {
                await send(json, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Log($"Could not send a message: {ex.Message}");
            }
        }

        private static JsonRpcResponse Error(McpEnvelope envelope, int code, string message)
        {
            return new JsonRpcResponse { Id = envelope.ResponseId, Error = new JsonRpcError { Code = code, Message = message } };
        }

        private static JsonRpcResponse MethodNotFound(McpEnvelope envelope, string message)
        {
            JsonRpcError error = JsonRpcError.MethodNotFound();
            error.Message = message;
            return new JsonRpcResponse { Id = envelope.ResponseId, Error = error };
        }

        private static JsonRpcError InvalidRequest(string message)
        {
            JsonRpcError error = JsonRpcError.InvalidRequest();
            error.Message = "Invalid Request: " + message;
            return error;
        }

        private static string? ReadString(object? result, string property)
        {
            if (result == null) return null;
            JsonElement element = result is JsonElement json ? json : JsonSerializer.SerializeToElement(result);
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
    }
}
