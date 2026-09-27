namespace Voltaic.Core
{
    using System;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Reads newline-delimited lines from a <see cref="TextReader"/> with a bound on the UTF-8 size of each line, so a
    /// peer that never sends a newline cannot grow memory without limit. A line over the bound is skipped up to its
    /// newline and reported through <see cref="LastLineTooLarge"/>. Not thread-safe; one reader per stream.
    /// </summary>
    internal sealed class BoundedLineReader
    {
        private readonly TextReader _Reader;
        private readonly Func<long> _MaxBytes;
        private readonly char[] _Buffer = new char[8192];
        private readonly StringBuilder _Line = new StringBuilder();
        private int _Position;
        private int _Length;
        private long _LineBytes;
        private bool _Discarding;
        private readonly bool _CarriageReturnEndsLine;
        private bool _SkipLineFeed;

        /// <summary>
        /// Creates a reader.
        /// </summary>
        /// <param name="reader">The source.</param>
        /// <param name="maxBytes">Returns the current limit in UTF-8 bytes per line; read for every line, so a changed setting applies to the next one.</param>
        /// <param name="carriageReturnEndsLine">True to end lines at CR, LF, or CRLF (the event stream rules); false to end them at LF only, dropping a CR before it (JSON lines).</param>
        internal BoundedLineReader(TextReader reader, Func<long> maxBytes, bool carriageReturnEndsLine = false)
        {
            _Reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _MaxBytes = maxBytes ?? throw new ArgumentNullException(nameof(maxBytes));
            _CarriageReturnEndsLine = carriageReturnEndsLine;
        }

        /// <summary>
        /// Gets whether the last line returned was over the limit: it was skipped and returned as an empty string.
        /// </summary>
        internal bool LastLineTooLarge { get; private set; }

        /// <summary>
        /// Reads the next line without its line terminator, or returns null at the end of the stream. A line over the
        /// limit is returned as an empty string with <see cref="LastLineTooLarge"/> set.
        /// </summary>
        internal async Task<string?> ReadLineAsync(CancellationToken token)
        {
            LastLineTooLarge = false;
            while (true)
            {
                if (_Position >= _Length)
                {
                    _Length = await _Reader.ReadAsync(_Buffer.AsMemory(), token).ConfigureAwait(false);
                    _Position = 0;
                    if (_Length == 0)
                    {
                        // End of stream: a final line without a newline still counts.
                        if (_Discarding) return Finish(true);
                        if (_Line.Length > 0) return Finish(false);
                        return null;
                    }
                }

                // A CRLF split across reads: the LF completes the line the CR already ended.
                if (_SkipLineFeed)
                {
                    _SkipLineFeed = false;
                    if (_Buffer[_Position] == '\n')
                    {
                        _Position++;
                        continue;
                    }
                }

                int newline = _CarriageReturnEndsLine
                    ? Array.FindIndex(_Buffer, _Position, _Length - _Position, character => character == '\n' || character == '\r')
                    : Array.IndexOf(_Buffer, '\n', _Position, _Length - _Position);
                int end = newline < 0 ? _Length : newline;
                if (!_Discarding)
                {
                    _LineBytes += Encoding.UTF8.GetByteCount(_Buffer, _Position, end - _Position);
                    if (_LineBytes > _MaxBytes())
                    {
                        _Discarding = true;
                        _Line.Clear();
                    }
                    else
                    {
                        _Line.Append(_Buffer, _Position, end - _Position);
                    }
                }

                if (newline < 0)
                {
                    _Position = _Length;
                    continue;
                }

                _Position = newline + 1;
                if (_Buffer[newline] == '\r')
                {
                    if (_Position < _Length)
                    {
                        if (_Buffer[_Position] == '\n') _Position++;
                    }
                    else
                    {
                        _SkipLineFeed = true;
                    }
                }

                return Finish(_Discarding);
            }
        }

        private string Finish(bool tooLarge)
        {
            string line = tooLarge ? String.Empty : _Line.ToString();
            if (!_CarriageReturnEndsLine && line.EndsWith('\r')) line = line.Substring(0, line.Length - 1);
            _Line.Clear();
            _LineBytes = 0;
            _Discarding = false;
            LastLineTooLarge = tooLarge;
            return line;
        }
    }
}
