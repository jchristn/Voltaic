namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading;

    /// <summary>
    /// The event history of one SSE stream in a Streamable HTTP session. Event IDs have the form
    /// <c>{streamId}-{sequence}</c>, so an ID names the stream it belongs to (as the MCP resumability rules require)
    /// and is unique across all streams of the session. The most recent <c>capacity</c> events are kept for replay.
    /// Thread-safe.
    /// </summary>
    internal sealed class SseStreamLog
    {
        private readonly object _Lock = new object();
        private readonly LinkedList<SseLoggedEvent> _Events = new LinkedList<SseLoggedEvent>();
        private readonly int _Capacity;
        private long _Sequence;
        private CancellationTokenSource? _Writer;
        private CancellationTokenSource? _Wake;

        /// <summary>
        /// Gets the stream identifier (32 hexadecimal characters).
        /// </summary>
        internal string StreamId { get; }

        internal SseStreamLog(string streamId, int capacity)
        {
            if (String.IsNullOrEmpty(streamId)) throw new ArgumentNullException(nameof(streamId));
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            StreamId = streamId;
            _Capacity = capacity;
        }

        /// <summary>
        /// Gets the ID of the most recent event, or the stream's initial ID (sequence 0) before any event.
        /// </summary>
        internal string CurrentEventId
        {
            get
            {
                lock (_Lock)
                {
                    return FormatEventId(StreamId, _Sequence);
                }
            }
        }

        /// <summary>
        /// Makes <paramref name="writer"/> the connection that writes this stream and cancels the previous one. A client
        /// that resumes a stream has abandoned the old connection, whose loop would otherwise keep taking messages
        /// from the session queue until a write to the dead connection failed.
        /// </summary>
        internal void Claim(CancellationTokenSource writer)
        {
            CancellationTokenSource? previous;
            lock (_Lock)
            {
                previous = _Writer;
                _Writer = writer;
            }

            if (previous != null && !ReferenceEquals(previous, writer))
            {
                try
                {
                    previous.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        /// <summary>
        /// Clears the writer when it is still <paramref name="writer"/>.
        /// </summary>
        internal void Release(CancellationTokenSource writer)
        {
            lock (_Lock)
            {
                if (ReferenceEquals(_Writer, writer)) _Writer = null;
            }
        }

        /// <summary>
        /// Records an event written by <paramref name="writer"/> and returns it. When the writer is no longer the
        /// stream's owner (its client resumed the stream on a new connection while it was taking the message), the
        /// event is still recorded and the owner is woken so it sends the event itself.
        /// </summary>
        internal SseLoggedEvent Append(string data, CancellationTokenSource writer)
        {
            CancellationTokenSource? wake = null;
            SseLoggedEvent logged;
            lock (_Lock)
            {
                _Sequence++;
                logged = new SseLoggedEvent(_Sequence, FormatEventId(StreamId, _Sequence), data);
                bool handoff = !ReferenceEquals(writer, _Writer);

                // With no replay buffer, only an event a former writer took is kept, so the owner can send it.
                if (_Capacity > 0 || handoff)
                {
                    _Events.AddLast(logged);
                    while (_Events.Count > Math.Max(_Capacity, 1)) _Events.RemoveFirst();
                }

                if (handoff) wake = _Wake;
            }

            try
            {
                wake?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            return logged;
        }

        /// <summary>
        /// Returns a token the owner waits on alongside the session queue. It is cancelled when a former writer records
        /// an event, and is already cancelled when retained events newer than <paramref name="lastSent"/> exist.
        /// </summary>
        internal CancellationToken BeginWait(long lastSent)
        {
            lock (_Lock)
            {
                _Wake?.Dispose();
                _Wake = new CancellationTokenSource();
                if (_Events.Last != null && _Events.Last.Value.Sequence > lastSent) _Wake.Cancel();
                return _Wake.Token;
            }
        }

        /// <summary>
        /// Returns the retained events after the given sequence number, oldest first.
        /// </summary>
        internal List<SseLoggedEvent> After(long sequence)
        {
            lock (_Lock)
            {
                return _Events.Where(logged => logged.Sequence > sequence).ToList();
            }
        }

        /// <summary>
        /// Splits an event ID into its stream ID and sequence number. Returns false when the value is not an ID this
        /// server issued.
        /// </summary>
        internal static bool TryParseEventId(string? eventId, out string streamId, out long sequence)
        {
            streamId = String.Empty;
            sequence = 0;
            if (String.IsNullOrWhiteSpace(eventId)) return false;

            int separator = eventId!.LastIndexOf('-');
            if (separator <= 0 || separator == eventId.Length - 1) return false;
            if (!Int64.TryParse(eventId.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out sequence)) return false;

            streamId = eventId.Substring(0, separator);
            return true;
        }

        private static string FormatEventId(string streamId, long sequence)
        {
            return streamId + "-" + sequence.ToString(CultureInfo.InvariantCulture);
        }
    }
}
