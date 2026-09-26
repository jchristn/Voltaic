namespace Voltaic.Mcp
{
    using System.Text.Json;

    /// <summary>
    /// The result of a client's <c>initialize</c> handshake: the negotiated version and the server's full result.
    /// </summary>
    internal sealed class McpInitializeOutcome
    {
        internal McpInitializeOutcome(string protocolVersion, JsonElement result)
        {
            ProtocolVersion = protocolVersion;
            Result = result;
        }

        internal string ProtocolVersion { get; }

        internal JsonElement Result { get; }
    }
}
