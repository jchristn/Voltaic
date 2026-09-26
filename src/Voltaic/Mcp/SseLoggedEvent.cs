namespace Voltaic.Mcp
{
    /// <summary>
    /// One event written to an SSE stream, kept so it can be replayed to a client that reconnects with
    /// <c>Last-Event-ID</c>.
    /// </summary>
    internal sealed class SseLoggedEvent
    {
        /// <summary>
        /// Gets the event's position on its stream (1 for the first message event).
        /// </summary>
        internal long Sequence { get; }

        /// <summary>
        /// Gets the event ID sent to the client.
        /// </summary>
        internal string EventId { get; }

        /// <summary>
        /// Gets the event data (a serialized JSON-RPC message).
        /// </summary>
        internal string Data { get; }

        internal SseLoggedEvent(long sequence, string eventId, string data)
        {
            Sequence = sequence;
            EventId = eventId;
            Data = data;
        }
    }
}
