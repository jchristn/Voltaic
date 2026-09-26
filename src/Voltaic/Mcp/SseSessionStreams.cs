namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The SSE streams of one Streamable HTTP session, so a client can resume a stream with <c>Last-Event-ID</c>. Only
    /// the most recent streams are kept. Streams are looked up within the session only, so an event ID from another
    /// session never replays anything. Thread-safe.
    /// </summary>
    internal sealed class SseSessionStreams
    {
        private readonly object _Lock = new object();
        private readonly Dictionary<string, SseStreamLog> _Streams = new Dictionary<string, SseStreamLog>(StringComparer.Ordinal);
        private readonly Queue<string> _Order = new Queue<string>();
        private readonly int _MaxStreams;

        internal SseSessionStreams(int maxStreams)
        {
            if (maxStreams < 1) throw new ArgumentOutOfRangeException(nameof(maxStreams));
            _MaxStreams = maxStreams;
        }

        /// <summary>
        /// Returns the stream to write to. When <paramref name="lastEventId"/> names a retained stream of this session,
        /// that stream is resumed and <paramref name="resumeAfter"/> is the sequence the client already has; otherwise
        /// a new stream is created and <paramref name="resumeAfter"/> is -1.
        /// </summary>
        internal SseStreamLog Open(string? lastEventId, int capacity, out long resumeAfter)
        {
            lock (_Lock)
            {
                if (SseStreamLog.TryParseEventId(lastEventId, out string streamId, out long sequence)
                    && _Streams.TryGetValue(streamId, out SseStreamLog? existing))
                {
                    resumeAfter = sequence;
                    return existing;
                }

                SseStreamLog created = Create(capacity);
                resumeAfter = -1;
                return created;
            }
        }

        // Opens the resumable SSE stream of one POST response.
        internal SseStreamLog OpenRequestStream(int capacity)
        {
            lock (_Lock)
            {
                SseStreamLog created = Create(capacity);
                created.IsRequestStream = true;
                return created;
            }
        }

        private SseStreamLog Create(int capacity)
        {
            SseStreamLog created = new SseStreamLog(Guid.NewGuid().ToString("N"), capacity);
            _Streams[created.StreamId] = created;
            _Order.Enqueue(created.StreamId);
            while (_Order.Count > _MaxStreams)
            {
                _Streams.Remove(_Order.Dequeue());
            }

            return created;
        }
    }
}
