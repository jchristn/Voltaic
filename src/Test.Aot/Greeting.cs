namespace Test.Aot
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// A plain (non-<c>McpToolCallResult</c>) tool result, returned by the <c>greet</c> tool.
    /// </summary>
    public sealed class Greeting
    {
        /// <summary>
        /// Gets or sets the greeting text.
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }
}
