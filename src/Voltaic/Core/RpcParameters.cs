namespace Voltaic.Core
{
    using System;
    using System.Globalization;
    using System.Text;
    using System.Text.Json;

    /// <summary>
    /// A dependency-light, DOM-free wrapper around JSON-RPC request parameters. It carries the raw
    /// parameters JSON and exposes typed deserialization and scalar property access without exposing
    /// any <c>System.Text.Json</c> DOM type (<c>JsonElement</c>, <c>JsonDocument</c>, <c>JsonNode</c>).
    /// Scalar accessors use the streaming <see cref="Utf8JsonReader"/>. This replaces the previous
    /// <c>JsonElement?</c> handler parameter across JSON-RPC and MCP. Instances are immutable and
    /// therefore thread-safe.
    /// </summary>
    public sealed class RpcParameters
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
        /// Initializes a new instance of the <see cref="RpcParameters"/> class from raw JSON text.
        /// </summary>
        /// <param name="rawJson">The raw parameters JSON, or null when none were supplied.</param>
        public RpcParameters(string? rawJson)
        {
            _RawJson = rawJson;
        }

        /// <summary>
        /// Creates parameters from a CLR value by serializing it to JSON.
        /// </summary>
        /// <param name="value">The value to serialize, or null.</param>
        /// <returns>A new <see cref="RpcParameters"/> instance.</returns>
        public static RpcParameters FromObject(object? value)
        {
            return new RpcParameters(value == null ? null : JsonSerializer.Serialize(value));
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

        /// <summary>
        /// Determines whether the top-level parameters object contains the named property.
        /// </summary>
        /// <param name="propertyName">The property name. Must not be null.</param>
        /// <returns>True if the property is present; otherwise false.</returns>
        public bool ContainsProperty(string propertyName)
        {
            return GetRawProperty(propertyName) != null;
        }

        /// <summary>
        /// Gets the string value of a top-level property, or null when it is absent or not a string.
        /// </summary>
        /// <param name="propertyName">The property name. Must not be null.</param>
        /// <returns>The string value, or null.</returns>
        public string? GetString(string propertyName)
        {
            string? raw = GetRawProperty(propertyName);
            if (raw == null)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<string>(raw);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Gets the numeric value of a top-level property, or null when it is absent or not a number.
        /// </summary>
        /// <param name="propertyName">The property name. Must not be null.</param>
        /// <returns>The numeric value, or null.</returns>
        public double? GetDouble(string propertyName)
        {
            string? raw = GetRawProperty(propertyName);
            if (raw != null && Double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
            {
                return value;
            }

            return null;
        }

        /// <summary>
        /// Gets the 64-bit integer value of a top-level property, or null when it is absent or not an integer.
        /// </summary>
        /// <param name="propertyName">The property name. Must not be null.</param>
        /// <returns>The integer value, or null.</returns>
        public long? GetInt64(string propertyName)
        {
            string? raw = GetRawProperty(propertyName);
            if (raw != null && Int64.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out long value))
            {
                return value;
            }

            return null;
        }

        /// <summary>
        /// Gets the boolean value of a top-level property, or null when it is absent or not a boolean.
        /// </summary>
        /// <param name="propertyName">The property name. Must not be null.</param>
        /// <returns>The boolean value, or null.</returns>
        public bool? GetBoolean(string propertyName)
        {
            string? raw = GetRawProperty(propertyName);
            if (raw == "true")
            {
                return true;
            }

            if (raw == "false")
            {
                return false;
            }

            return null;
        }

        private string? GetRawProperty(string propertyName)
        {
            if (String.IsNullOrEmpty(propertyName) || !HasValue)
            {
                return null;
            }

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(_RawJson!);
                Utf8JsonReader reader = new Utf8JsonReader(bytes);
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                {
                    return null;
                }

                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        continue;
                    }

                    bool match = reader.ValueTextEquals(propertyName);
                    reader.Read();
                    long valueStart = reader.TokenStartIndex;
                    if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
                    {
                        reader.Skip();
                    }

                    if (match)
                    {
                        long valueEnd = reader.BytesConsumed;
                        return Encoding.UTF8.GetString(bytes, (int)valueStart, (int)(valueEnd - valueStart));
                    }
                }
            }
            catch (JsonException)
            {
                return null;
            }

            return null;
        }
    }
}
