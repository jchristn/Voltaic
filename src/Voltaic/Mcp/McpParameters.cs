namespace Voltaic.Mcp
{
    using System;
    using System.Text.Json;

    /// <summary>
    /// A dependency-light, DOM-free wrapper around JSON-RPC request parameters. It carries the raw
    /// parameters JSON and exposes typed deserialization without exposing any
    /// <c>System.Text.Json</c> DOM type (<c>JsonElement</c>, <c>JsonDocument</c>, <c>JsonNode</c>).
    /// This replaces the previous <c>JsonElement?</c> handler parameter.
    /// Instances are immutable and therefore thread-safe.
    /// </summary>
    public sealed class McpParameters
    {
        private readonly string? _RawJson;

        /// <summary>
        /// Gets the raw JSON text of the parameters, or null when no parameters were supplied.
        /// </summary>
        public string? RawJson => _RawJson;

        /// <summary>
        /// Gets a value indicating whether any parameters were supplied.
        /// </summary>
        public bool HasValue => !String.IsNullOrWhiteSpace(_RawJson);

        /// <summary>
        /// Initializes a new instance of the <see cref="McpParameters"/> class from raw JSON text.
        /// </summary>
        /// <param name="rawJson">The raw parameters JSON, or null when none were supplied.</param>
        public McpParameters(string? rawJson)
        {
            _RawJson = rawJson;
        }

        /// <summary>
        /// Creates parameters from a CLR value by serializing it to JSON.
        /// </summary>
        /// <param name="value">The value to serialize, or null.</param>
        /// <returns>A new <see cref="McpParameters"/> instance.</returns>
        public static McpParameters FromObject(object? value)
        {
            return new McpParameters(value == null ? null : JsonSerializer.Serialize(value));
        }

        /// <summary>
        /// Deserializes the parameters into the specified type. Returns the type default when no
        /// parameters were supplied.
        /// </summary>
        /// <typeparam name="T">The target type.</typeparam>
        /// <returns>The deserialized value, or the type default when empty.</returns>
        /// <exception cref="JsonException">Thrown when the raw JSON cannot be deserialized into <typeparamref name="T"/>.</exception>
        public T? Deserialize<T>()
        {
            if (!HasValue)
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(_RawJson!);
        }
    }
}
