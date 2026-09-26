namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Core;

    /// <summary>
    /// The client side of the MCP <c>initialize</c> handshake, shared by the stream-transport clients: sends
    /// <c>initialize</c> with the client's info and capabilities, checks the version the server chose, and sends
    /// <c>notifications/initialized</c>.
    /// </summary>
    internal static class McpClientHandshake
    {
        /// <summary>
        /// Performs the handshake and returns the negotiated protocol version and the server's result.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the server answers with an error, or chooses a version this client does not support or a stateless-era version.</exception>
        internal static async Task<McpInitializeOutcome> RunAsync(
            Func<object, CancellationToken, Task<JsonElement>> callInitialize,
            Func<CancellationToken, Task> notifyInitialized,
            string requestedVersion,
            string clientName,
            string clientVersion,
            Dictionary<string, object?> capabilities,
            CancellationToken token)
        {
            Dictionary<string, object?> parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                { "protocolVersion", requestedVersion },
                { "capabilities", capabilities },
                { "clientInfo", new Dictionary<string, object?>(StringComparer.Ordinal) { { "name", clientName }, { "version", clientVersion } } }
            };

            JsonElement result = await callInitialize(parameters, token).ConfigureAwait(false);
            string? negotiated = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("protocolVersion", out JsonElement version) && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;

            if (String.IsNullOrEmpty(negotiated))
            {
                throw new InvalidOperationException("The initialize result has no protocolVersion.");
            }

            // initialize can only agree to a handshake-era revision this client implements.
            if (!McpProtocol.IsHandshakeVersion(negotiated))
            {
                throw new InvalidOperationException($"The server chose protocol version '{negotiated}', which this client cannot use for a session.");
            }

            await notifyInitialized(token).ConfigureAwait(false);
            return new McpInitializeOutcome(negotiated!, result.Clone());
        }

        /// <summary>
        /// Builds the client capabilities for the request handlers a client has registered.
        /// </summary>
        internal static Dictionary<string, object?> CapabilitiesFor(ClientRequestDispatcher dispatcher, IReadOnlyDictionary<string, object?>? extra, string? protocolVersion = null)
        {
            Dictionary<string, object?> capabilities = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (extra != null)
            {
                foreach (KeyValuePair<string, object?> entry in extra) capabilities[entry.Key] = entry.Value;
            }

            if (dispatcher.HasHandler("roots/list") && !capabilities.ContainsKey("roots")) capabilities["roots"] = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (dispatcher.HasHandler("sampling/createMessage") && !capabilities.ContainsKey("sampling")) capabilities["sampling"] = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (dispatcher.HasHandler("elicitation/create") && !capabilities.ContainsKey("elicitation")) capabilities["elicitation"] = new Dictionary<string, object?>(StringComparer.Ordinal);

            // elicitation was added in 2025-06-18; an older revision's ClientCapabilities has no such member.
            if (protocolVersion != null && McpProtocol.IsHandshakeVersion(protocolVersion)
                && String.CompareOrdinal(protocolVersion, McpProtocol.ProtocolVersion20250618) < 0)
            {
                capabilities.Remove("elicitation");
            }

            return capabilities;
        }

        /// <summary>
        /// Converts a call result (whatever object the client deserialized) to a JSON element.
        /// </summary>
        // Whether a server may send this client a JSON-RPC batch: 2025-03-26 defines batching, and 2024-11-05 is plain
        // JSON-RPC 2.0 (the server accepts batches there too); 2025-06-18 and later removed it.
        internal static bool AllowsBatches(string protocolVersion)
        {
            return StringComparer.Ordinal.Equals(protocolVersion, McpProtocol.ProtocolVersion20241105)
                || McpProtocol.GetVersionInfo(protocolVersion)?.SupportsBatching == true;
        }

        internal static JsonElement ToElement(object? result)
        {
            if (result is JsonElement element) return element;
            return JsonSerializer.SerializeToElement(result);
        }
    }
}
