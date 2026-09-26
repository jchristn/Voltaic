namespace Voltaic.Mcp
{
    using System;

    /// <summary>
    /// An error a tool handler reports to the model. When a handler throws this exception, <c>tools/call</c> returns a
    /// tool result with <c>isError</c> set to true whose text is exactly <see cref="Exception.Message"/>, so the model
    /// can read it and react (for example "City 'Atlantis' was not found."). Other exceptions are reported with a
    /// generic message unless the server's <c>IncludeToolExceptionMessages</c> is true, because their messages may
    /// contain internal details. Throw <see cref="McpProtocolException"/> instead to send a JSON-RPC protocol error.
    /// </summary>
    public class McpToolException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="McpToolException"/> class.
        /// </summary>
        /// <param name="message">The message shown to the model. Must not be null or empty; it should contain no secrets.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="message"/> is null or empty.</exception>
        public McpToolException(string message)
            : this(message, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="McpToolException"/> class with the exception that caused it. The
        /// inner exception is not shown to the model.
        /// </summary>
        /// <param name="message">The message shown to the model. Must not be null or empty; it should contain no secrets.</param>
        /// <param name="innerException">The underlying exception, or null.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="message"/> is null or empty.</exception>
        public McpToolException(string message, Exception? innerException)
            : base(String.IsNullOrEmpty(message) ? throw new ArgumentNullException(nameof(message)) : message, innerException)
        {
        }
    }
}
