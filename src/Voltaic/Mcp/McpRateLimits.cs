namespace Voltaic.Mcp
{
    using System;

    /// <summary>
    /// The rate limits an MCP server applies per client (MCP: servers must rate-limit tool invocations, and should
    /// rate-limit completions and log messages). A client is a session, or, for HTTP requests without a
    /// session, the authenticated principal and remote address. Each limit allows that many operations per second, with
    /// a burst of the same size. A tool call over the limit gets a tool result with <c>isError: true</c> (so the model
    /// can back off), a completion request gets no suggestions, and a log message is not sent. Changes take effect
    /// immediately. Thread-safe.
    /// </summary>
    public sealed class McpRateLimits
    {
        private int _ToolCallsPerSecond = 100;
        private int _CompletionsPerSecond = 100;
        private int _LogMessagesPerSecond = 200;

        /// <summary>
        /// Gets or sets how many <c>tools/call</c> requests one client may make per second. Default is 100. 0 disables
        /// the limit. Maximum is 1000000.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set outside 0 to 1000000.</exception>
        public int ToolCallsPerSecond
        {
            get => _ToolCallsPerSecond;
            set => _ToolCallsPerSecond = Validate(value, nameof(ToolCallsPerSecond));
        }

        /// <summary>
        /// Gets or sets how many <c>completion/complete</c> requests one client may make per second. Default is 100. 0
        /// disables the limit. Maximum is 1000000.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set outside 0 to 1000000.</exception>
        public int CompletionsPerSecond
        {
            get => _CompletionsPerSecond;
            set => _CompletionsPerSecond = Validate(value, nameof(CompletionsPerSecond));
        }

        /// <summary>
        /// Gets or sets how many <c>notifications/message</c> log messages the server sends one client per second.
        /// Default is 200. 0 disables the limit. Maximum is 1000000.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set outside 0 to 1000000.</exception>
        public int LogMessagesPerSecond
        {
            get => _LogMessagesPerSecond;
            set => _LogMessagesPerSecond = Validate(value, nameof(LogMessagesPerSecond));
        }

        private static int Validate(int value, string name)
        {
            if (value < 0 || value > 1000000) throw new ArgumentOutOfRangeException(name, $"{name} must be between 0 and 1000000.");
            return value;
        }
    }
}
