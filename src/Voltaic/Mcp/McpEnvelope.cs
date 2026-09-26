namespace Voltaic.Mcp
{
    using System;
    using System.Text.Json;
    using Voltaic.Core;

    /// <summary>
    /// One JSON-RPC message received by an MCP server, validated against the JSON-RPC 2.0 and MCP envelope rules:
    /// <c>jsonrpc</c> must be <c>"2.0"</c>, <c>method</c> a string, an ID a string or number (never null), and
    /// <c>params</c> an object.
    /// </summary>
    internal sealed class McpEnvelope
    {
        private McpEnvelope(McpEnvelopeKind kind, JsonElement message, string? method, JsonElement? id, JsonElement? parameters, JsonRpcError? error)
        {
            Kind = kind;
            Message = message;
            Method = method;
            Id = id;
            Params = parameters;
            Error = error;
        }

        internal McpEnvelopeKind Kind { get; }

        /// <summary>
        /// Gets the whole message.
        /// </summary>
        internal JsonElement Message { get; }

        internal string? Method { get; }

        /// <summary>
        /// Gets the request ID, or null for notifications and for invalid messages whose ID could not be read.
        /// </summary>
        internal JsonElement? Id { get; }

        internal JsonElement? Params { get; }

        /// <summary>
        /// Gets the error to answer an invalid message with.
        /// </summary>
        internal JsonRpcError? Error { get; }

        /// <summary>
        /// Gets the ID as raw JSON, the key for in-flight tracking and cancellation.
        /// </summary>
        internal string? IdKey => Id?.GetRawText();

        /// <summary>
        /// Gets the ID as a response ID value (a <see cref="JsonElement"/>), or null.
        /// </summary>
        internal object? ResponseId => Id.HasValue ? (object)Id.Value : null;

        /// <summary>
        /// Validates one message. The element is cloned, so it outlives its document.
        /// </summary>
        internal static McpEnvelope Parse(JsonElement element)
        {
            JsonElement message = element.Clone();
            if (message.ValueKind != JsonValueKind.Object)
            {
                return Invalid(message, null, "A JSON-RPC message must be a JSON object.");
            }

            JsonElement? id = null;
            bool hasId = message.TryGetProperty("id", out JsonElement idElement);
            bool validId = hasId && (idElement.ValueKind == JsonValueKind.String || idElement.ValueKind == JsonValueKind.Number);
            if (validId) id = idElement;

            if (!message.TryGetProperty("jsonrpc", out JsonElement version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
            {
                return Invalid(message, id, "The jsonrpc member must be exactly \"2.0\".");
            }

            if (!message.TryGetProperty("method", out JsonElement methodElement))
            {
                // A response the client sent (for example to a server ping). Voltaic servers accept and drop them.
                if (hasId && (message.TryGetProperty("result", out JsonElement _) || message.TryGetProperty("error", out JsonElement _)))
                {
                    return new McpEnvelope(McpEnvelopeKind.Response, message, null, id, null, null);
                }

                return Invalid(message, id, "A JSON-RPC request must have a method.");
            }

            if (methodElement.ValueKind != JsonValueKind.String || String.IsNullOrEmpty(methodElement.GetString()))
            {
                return Invalid(message, id, "The method member must be a non-empty string.");
            }

            string method = methodElement.GetString()!;
            if (hasId && !validId)
            {
                return Invalid(message, null, "A request ID must be a string or a number; null is not allowed.");
            }

            JsonElement? parameters = null;
            if (message.TryGetProperty("params", out JsonElement paramsElement))
            {
                if (paramsElement.ValueKind != JsonValueKind.Object)
                {
                    JsonRpcError invalidParams = JsonRpcError.InvalidParams();
                    invalidParams.Message = "MCP params must be a JSON object.";
                    return new McpEnvelope(McpEnvelopeKind.Invalid, message, method, id, null, invalidParams);
                }

                parameters = paramsElement;
            }

            return new McpEnvelope(hasId ? McpEnvelopeKind.Request : McpEnvelopeKind.Notification, message, method, id, parameters, null);
        }

        /// <summary>
        /// Returns the value of <c>params._meta[key]</c>, or null.
        /// </summary>
        internal JsonElement? GetMeta(string key)
        {
            if (!Params.HasValue) return null;
            if (!Params.Value.TryGetProperty("_meta", out JsonElement meta) || meta.ValueKind != JsonValueKind.Object) return null;
            return meta.TryGetProperty(key, out JsonElement value) ? value : (JsonElement?)null;
        }

        /// <summary>
        /// Builds the request object handlers receive.
        /// </summary>
        internal JsonRpcRequest ToRequest()
        {
            return new JsonRpcRequest
            {
                Method = Method ?? String.Empty,
                Params = Params.HasValue ? (object)Params.Value : null,
                Id = ResponseId
            };
        }

        private static McpEnvelope Invalid(JsonElement message, JsonElement? id, string reason)
        {
            JsonRpcError error = JsonRpcError.InvalidRequest();
            error.Message = "Invalid Request: " + reason;
            return new McpEnvelope(McpEnvelopeKind.Invalid, message, null, id, null, error);
        }
    }
}
