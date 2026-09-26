namespace Voltaic.Mcp
{
    using System;
    using System.Text.Json;
    using Voltaic.Core;

    /// <summary>
    /// Decides, per request, whether a message on a stream transport (stdio, TCP, WebSocket) is a stateless-era
    /// (<c>2026-07-28</c>) request, and serializes its response with the fields that era requires. A dual-era
    /// server serves a request carrying a stateless-era <c>params._meta["io.modelcontextprotocol/protocolVersion"]</c>
    /// statelessly and everything else, including every <c>initialize</c>, under the handshake-era rules.
    /// </summary>
    internal static class McpStatelessDispatcher
    {
        /// <summary>
        /// Returns the stateless-era protocol version a request selects, or null when it belongs to the
        /// handshake era (no <c>_meta</c> protocol version, a handshake-era version, or <c>initialize</c>).
        /// </summary>
        /// <exception cref="McpProtocolException">Thrown with <c>-32022</c> (unsupported protocol version, listing the supported versions) when <c>_meta</c> names a version this server does not implement.</exception>
        internal static string? ResolveStatelessVersion(string requestJson, string? method)
        {
            // Records the era for the handler (McpToolCallContext.CanRequestInput). The caller is an async
            // request-processing method, so the value is scoped to that request.
            McpRequestProtocol.Set(null);

            // initialize is handshake-era by definition; stateless revisions define no initialize.
            if (StringComparer.Ordinal.Equals(method, "initialize")) return null;

            string? metaVersion = ExtractMetaProtocolVersion(requestJson);
            if (String.IsNullOrWhiteSpace(metaVersion)) return null;

            McpResolvedVersion resolved = McpVersionResolver.Resolve(null, metaVersion, method, false, false);
            string? statelessVersion = resolved.Era == McpProtocolEra.Stateless ? resolved.Version : null;
            McpRequestProtocol.Set(statelessVersion);
            return statelessVersion;
        }

        /// <summary>
        /// Serializes a response. For a stateless-era request the result receives <c>resultType</c> (and
        /// <c>ttlMs</c>/<c>cacheScope</c> for cacheable results) unless the handler set them; the additions are
        /// reverted after serialization so a result instance reused by a handler never carries them into a
        /// handshake-era response.
        /// </summary>
        internal static string SerializeResponse(JsonRpcResponse response, string? statelessVersion)
        {
            McpStatelessResultStamp? stamp = statelessVersion == null ? null : McpStatelessResultStamper.ApplyForVersion(response, statelessVersion);
            try
            {
                return JsonSerializer.Serialize(response);
            }
            finally
            {
                stamp?.Revert();
            }
        }

        /// <summary>
        /// Builds the error response for a request rejected before dispatch.
        /// </summary>
        internal static JsonRpcResponse ErrorResponse(object? id, McpProtocolException error)
        {
            return new JsonRpcResponse { Id = id, Error = error.ToJsonRpcError() };
        }

        private static string? ExtractMetaProtocolVersion(string requestJson)
        {
            if (String.IsNullOrWhiteSpace(requestJson)) return null;

            try
            {
                using (JsonDocument document = JsonDocument.Parse(requestJson))
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return null;
                    if (!root.TryGetProperty("params", out JsonElement parameters) || parameters.ValueKind != JsonValueKind.Object) return null;
                    if (!parameters.TryGetProperty("_meta", out JsonElement meta) || meta.ValueKind != JsonValueKind.Object) return null;
                    if (!meta.TryGetProperty(McpProtocol.MetaProtocolVersionKey, out JsonElement version) || version.ValueKind != JsonValueKind.String) return null;
                    return version.GetString();
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
