namespace Voltaic.Core
{
    using System;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Parses a Server-Sent Events stream following the WHATWG event stream interpretation rules: <c>data</c> lines
    /// are joined with line feeds, an optional single space after the colon is removed, comment lines (starting
    /// with a colon) are ignored, <c>id</c> updates the last event ID even for events without data (as an MCP
    /// priming event does), and <c>retry</c> records the reconnection time. Not thread-safe.
    /// </summary>
    internal sealed class SseEventReader
    {
        private readonly StreamReader _Reader;
        private readonly StringBuilder _Data = new StringBuilder();
        private string? _EventType;
        private string? _LastEventIdBuffer;

        /// <summary>
        /// Gets the stream's last event ID: the value of the most recent <c>id</c> field of a completed event, or the
        /// initial value supplied to the constructor. Null when none has been received.
        /// </summary>
        internal string? LastEventId { get; private set; }

        /// <summary>
        /// Gets the most recent <c>retry</c> value in milliseconds, or null when the server sent none.
        /// </summary>
        internal int? RetryMs { get; private set; }

        internal SseEventReader(StreamReader reader, string? lastEventId = null)
        {
            _Reader = reader ?? throw new ArgumentNullException(nameof(reader));
            LastEventId = lastEventId;
            _LastEventIdBuffer = lastEventId;
        }

        /// <summary>
        /// Reads the next dispatched event (one with at least one <c>data</c> field, possibly empty). Returns null when
        /// the stream ends; an incomplete event at the end of the stream is discarded, as the event stream rules require.
        /// </summary>
        internal async Task<SseEvent?> ReadEventAsync(CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                string? line = await _Reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line == null) return null;

                if (line.Length == 0)
                {
                    LastEventId = _LastEventIdBuffer;
                    if (_Data.Length == 0)
                    {
                        _EventType = null;
                        continue;
                    }

                    string data = _Data.ToString();
                    if (data.EndsWith("\n", StringComparison.Ordinal)) data = data.Substring(0, data.Length - 1);
                    SseEvent dispatched = new SseEvent(data, _EventType, LastEventId);
                    _Data.Clear();
                    _EventType = null;
                    return dispatched;
                }

                if (line[0] == ':') continue;

                int colon = line.IndexOf(':');
                string field = colon < 0 ? line : line.Substring(0, colon);
                string value = colon < 0 ? String.Empty : line.Substring(colon + 1);
                if (value.Length > 0 && value[0] == ' ') value = value.Substring(1);

                switch (field)
                {
                    case "data":
                        _Data.Append(value).Append('\n');
                        break;
                    case "event":
                        _EventType = value;
                        break;
                    case "id":
                        if (value.IndexOf('\0') < 0) _LastEventIdBuffer = value;
                        break;
                    case "retry":
                        if (value.Length > 0 && IsAllDigits(value) && Int32.TryParse(value, out int retry)) RetryMs = retry;
                        break;
                }
            }
        }

        private static bool IsAllDigits(string value)
        {
            foreach (char character in value)
            {
                if (character < '0' || character > '9') return false;
            }

            return true;
        }
    }
}
