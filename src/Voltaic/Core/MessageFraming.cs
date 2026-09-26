namespace Voltaic.Core
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Provides LSP-style message framing for JSON-RPC 2.0 messages.
    /// Messages are formatted as: Content-Length: {size}\r\n\r\n{json_body}
    /// The reader accepts only the LSP header grammar: every header line must be <c>Content-Length</c>
    /// (exactly once, digits only) or <c>Content-Type</c>, and the complete header block may not exceed
    /// 1024 bytes. Anything else, including an HTTP request line or HTTP headers such as <c>Host</c> or
    /// <c>Origin</c>, raises <see cref="InvalidDataException"/> and the connection is closed, so a web page
    /// cannot drive a TCP server with a cross-protocol <c>fetch()</c>.
    /// </summary>
    public static class MessageFraming
    {
        private const int InitialBufferSize = 4096;
        private const int MaxHeaderSize = 1024; // Reasonable max for "Content-Length: NNNN\r\n\r\n"
        private const string ContentLengthPrefix = "Content-Length:";
        private const string ContentTypePrefix = "Content-Type:";

        /// <summary>
        /// Reads a complete LSP-framed message from a stream.
        /// Handles partial reads, buffering, and multiple messages in the buffer.
        /// </summary>
        /// <param name="stream">The network stream to read from.</param>
        /// <param name="buffer">A reusable buffer for reading. Will be resized if needed.</param>
        /// <param name="bufferOffset">Current offset in the buffer containing unprocessed data.</param>
        /// <param name="bufferCount">Number of unprocessed bytes in the buffer.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A tuple containing the message string, updated buffer (may be resized), and buffer state (offset, count).</returns>
        public static async Task<(string? message, byte[] buffer, int newOffset, int newCount)> ReadMessageAsync(
            Stream stream,
            byte[] buffer,
            int bufferOffset,
            int bufferCount,
            CancellationToken token)
        {
            // Try to find a complete message in the current buffer first
            (bool success, string? message, int newOffset, int newCount) result = TryExtractMessage(buffer, bufferOffset, bufferCount);
            if (result.success)
            {
                return (result.message, buffer, result.newOffset, result.newCount);
            }

            // Need to read more data
            // First, consolidate buffer if needed (move unprocessed data to start)
            if (bufferOffset > 0 && bufferCount > 0)
            {
                Buffer.BlockCopy(buffer, bufferOffset, buffer, 0, bufferCount);
                bufferOffset = 0;
            }

            // Ensure buffer has space
            if (bufferCount >= buffer.Length)
            {
                // Buffer is full but no complete message - need larger buffer
                byte[] newBuffer = new byte[buffer.Length * 2];
                Buffer.BlockCopy(buffer, 0, newBuffer, 0, bufferCount);
                buffer = newBuffer;
            }

            // Read more data
            int bytesRead = await stream.ReadAsync(buffer, bufferCount, buffer.Length - bufferCount, token).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                // Stream closed
                return (null, buffer, 0, 0);
            }

            bufferCount += bytesRead;

            // Try to extract message again
            result = TryExtractMessage(buffer, 0, bufferCount);
            if (result.success)
            {
                return (result.message, buffer, result.newOffset, result.newCount);
            }

            // Still no complete message - caller should call again
            return (null, buffer, 0, bufferCount);
        }

        /// <summary>
        /// Attempts to extract a complete LSP-framed message from a buffer.
        /// </summary>
        private static (bool success, string? message, int newOffset, int newCount) TryExtractMessage(
            byte[] buffer,
            int offset,
            int count)
        {
            if (count < 4) // Minimum: "C\r\n\r\n" is not realistic, but at least need something
            {
                return (false, null, offset, count);
            }

            // Fail fast on anything that cannot be an LSP header, such as an HTTP request line.
            ValidateHeaderPrefix(buffer, offset, count);

            // Look for "\r\n\r\n" which ends the headers
            int headerEnd = FindHeaderEnd(buffer, offset, count);
            if (headerEnd == -1)
            {
                // No complete header yet
                if (count > MaxHeaderSize)
                {
                    // Header too large, this is probably invalid
                    throw new InvalidDataException("Message header exceeds maximum size");
                }
                return (false, null, offset, count);
            }

            // Parse Content-Length from header
            int headerLength = headerEnd - offset;
            if (headerLength + 4 > MaxHeaderSize)
            {
                throw new InvalidDataException("Message header exceeds maximum size");
            }

            string header = Encoding.ASCII.GetString(buffer, offset, headerLength);
            int contentLength = ParseContentLength(header);

            // Check if we have the complete body
            int bodyStart = headerEnd + 4; // Skip past "\r\n\r\n"
            int totalBytesNeeded = (bodyStart - offset) + contentLength;

            if (count < totalBytesNeeded)
            {
                // Don't have complete message yet
                return (false, null, offset, count);
            }

            // Extract the message body
            string message = Encoding.UTF8.GetString(buffer, bodyStart, contentLength);

            // Calculate new buffer position
            int newOffset = offset + totalBytesNeeded;
            int newCount = count - totalBytesNeeded;

            return (true, message, newOffset, newCount);
        }

        /// <summary>
        /// Finds the position of "\r\n\r\n" in the buffer.
        /// </summary>
        private static int FindHeaderEnd(byte[] buffer, int offset, int count)
        {
            int end = offset + count - 3; // Need at least 4 chars for "\r\n\r\n"
            for (int i = offset; i < end; i++)
            {
                if (buffer[i] == '\r' &&
                    buffer[i + 1] == '\n' &&
                    buffer[i + 2] == '\r' &&
                    buffer[i + 3] == '\n')
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Parses the Content-Length value from the header string. Only Content-Length (exactly once, digits
        /// only) and Content-Type lines are allowed.
        /// </summary>
        private static int ParseContentLength(string header)
        {
            string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            int? contentLength = null;

            foreach (string line in lines)
            {
                if (line.StartsWith(ContentLengthPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (contentLength != null)
                    {
                        throw new InvalidDataException("Duplicate Content-Length header");
                    }

                    string value = line.Substring(ContentLengthPrefix.Length).Trim();
                    if (value.Length == 0 || value.Length > 10 || !value.All(Char.IsDigit) || !int.TryParse(value, out int length))
                    {
                        throw new InvalidDataException("Content-Length header value is invalid");
                    }

                    contentLength = length;
                }
                else if (!line.StartsWith(ContentTypePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Unexpected message framing header; only Content-Length and Content-Type are allowed");
                }
            }

            if (contentLength == null)
            {
                throw new InvalidDataException("Content-Length header not found or invalid");
            }

            return contentLength.Value;
        }

        /// <summary>
        /// Rejects a buffer whose first bytes cannot begin a Content-Length or Content-Type header line.
        /// </summary>
        private static void ValidateHeaderPrefix(byte[] buffer, int offset, int count)
        {
            int length = Math.Min(count, ContentLengthPrefix.Length);
            bool matchesLength = true;
            bool matchesType = true;

            for (int i = 0; i < length; i++)
            {
                char actual = Char.ToLowerInvariant((char)buffer[offset + i]);
                if (matchesLength && i < ContentLengthPrefix.Length && Char.ToLowerInvariant(ContentLengthPrefix[i]) != actual) matchesLength = false;
                if (matchesType && i < ContentTypePrefix.Length && Char.ToLowerInvariant(ContentTypePrefix[i]) != actual) matchesType = false;
                if (!matchesLength && !matchesType)
                {
                    throw new InvalidDataException("Data does not begin with a Content-Length or Content-Type header; the connection does not speak LSP-style framing");
                }
            }
        }

        /// <summary>
        /// Writes a message with LSP-style framing to a stream.
        /// Supports multiple headers (Content-Length is required, Content-Type is optional).
        /// </summary>
        /// <param name="stream">The network stream to write to.</param>
        /// <param name="message">The message string (typically JSON) to send.</param>
        /// <param name="contentType">Optional Content-Type header value. If null, Content-Type header is omitted.</param>
        /// <param name="token">Cancellation token.</param>
        public static async Task WriteMessageAsync(Stream stream, string message, string? contentType = null, CancellationToken token = default)
        {
            // Convert message to UTF-8 bytes
            byte[] messageBytes = Encoding.UTF8.GetBytes(message);

            // Build headers (Content-Length is required, Content-Type is optional)
            string header = $"Content-Length: {messageBytes.Length}\r\n";
            if (!string.IsNullOrEmpty(contentType))
            {
                header += $"Content-Type: {contentType}\r\n";
            }
            header += "\r\n";

            byte[] headerBytes = Encoding.ASCII.GetBytes(header);

            // Write header
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, token).ConfigureAwait(false);

            // Write body
            await stream.WriteAsync(messageBytes, 0, messageBytes.Length, token).ConfigureAwait(false);

            // Flush
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a buffer for message reading operations.
        /// </summary>
        public static byte[] CreateBuffer()
        {
            return new byte[InitialBufferSize];
        }
    }
}
