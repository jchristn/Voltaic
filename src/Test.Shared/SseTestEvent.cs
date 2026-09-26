namespace Test.Shared
{
    /// <summary>
    /// One SSE event read by a test: its ID (null when none) and data (empty for a priming event).
    /// </summary>
    internal sealed class SseTestEvent
    {
        public SseTestEvent(string? id, string data)
        {
            Id = id;
            Data = data;
        }

        public string? Id { get; }

        public string Data { get; }
    }
}
