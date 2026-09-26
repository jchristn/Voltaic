namespace Voltaic.Mcp
{
    /// <summary>
    /// The kind of a received JSON-RPC message.
    /// </summary>
    internal enum McpEnvelopeKind
    {
        /// <summary>
        /// A request (it has an ID and expects a response).
        /// </summary>
        Request,

        /// <summary>
        /// A notification (no ID, no response).
        /// </summary>
        Notification,

        /// <summary>
        /// A response or error sent by the client.
        /// </summary>
        Response,

        /// <summary>
        /// A message that violates the envelope rules.
        /// </summary>
        Invalid
    }
}
