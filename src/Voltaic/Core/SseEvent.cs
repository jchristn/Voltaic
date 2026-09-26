namespace Voltaic.Core
{
    /// <summary>
    /// One dispatched Server-Sent Events event: the joined <c>data</c> lines, the <c>event</c> type, and the event
    /// stream's last event ID at the time of dispatch.
    /// </summary>
    internal sealed class SseEvent
    {
        /// <summary>
        /// Gets the event data: the <c>data</c> field values joined with line feeds. Empty for an event with an empty
        /// <c>data</c> field, such as an MCP priming event; events with no <c>data</c> field are not dispatched.
        /// </summary>
        internal string Data { get; }

        /// <summary>
        /// Gets the event type, or null when the event had no <c>event</c> field (a <c>message</c> event).
        /// </summary>
        internal string? EventType { get; }

        /// <summary>
        /// Gets the last event ID in effect when the event was dispatched, or null when none has been set.
        /// </summary>
        internal string? LastEventId { get; }

        internal SseEvent(string data, string? eventType, string? lastEventId)
        {
            Data = data;
            EventType = eventType;
            LastEventId = lastEventId;
        }
    }
}
