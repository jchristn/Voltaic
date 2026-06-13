namespace Voltaic
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Connection lifecycle state used by MCP sessions.
    /// </summary>
    public enum McpSessionLifecycleState
    {
        /// <summary>
        /// The session object has been created but initialization has not started.
        /// </summary>
        Created,

        /// <summary>
        /// The initialize request has been accepted and the server is waiting for notifications/initialized.
        /// </summary>
        Initializing,

        /// <summary>
        /// The client has sent notifications/initialized and normal protocol operation may proceed.
        /// </summary>
        Initialized,

        /// <summary>
        /// The session is closing.
        /// </summary>
        Closing,

        /// <summary>
        /// The session is closed.
        /// </summary>
        Closed
    }

    /// <summary>
    /// Parameters for notifications/cancelled.
    /// </summary>
    public class McpCancelledNotification
    {
        /// <summary>
        /// Gets or sets the request ID being cancelled.
        /// </summary>
        [JsonPropertyName("requestId")]
        public object RequestId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets an optional cancellation reason.
        /// </summary>
        [JsonPropertyName("reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; set; }
    }

    /// <summary>
    /// Parameters for notifications/progress.
    /// </summary>
    public class McpProgressNotification
    {
        /// <summary>
        /// Gets or sets the progress token supplied in request metadata.
        /// </summary>
        [JsonPropertyName("progressToken")]
        public object ProgressToken { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the current progress value.
        /// </summary>
        [JsonPropertyName("progress")]
        public double Progress { get; set; }

        /// <summary>
        /// Gets or sets the optional total progress value.
        /// </summary>
        [JsonPropertyName("total")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Total { get; set; }

        /// <summary>
        /// Gets or sets optional human-readable progress text.
        /// </summary>
        [JsonPropertyName("message")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Message { get; set; }
    }

    /// <summary>
    /// Parameters for logging/setLevel.
    /// </summary>
    public class McpSetLogLevelRequest
    {
        /// <summary>
        /// Gets or sets the minimum log level requested by the client.
        /// </summary>
        [JsonPropertyName("level")]
        public string Level { get; set; } = "info";
    }

    /// <summary>
    /// Parameters for notifications/message.
    /// </summary>
    public class McpLogMessageNotification
    {
        /// <summary>
        /// Gets or sets the syslog-style log level.
        /// </summary>
        [JsonPropertyName("level")]
        public string Level { get; set; } = "info";

        /// <summary>
        /// Gets or sets an optional logger name.
        /// </summary>
        [JsonPropertyName("logger")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Logger { get; set; }

        /// <summary>
        /// Gets or sets JSON-serializable log data.
        /// </summary>
        [JsonPropertyName("data")]
        public object? Data { get; set; }
    }
}
