namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json;

    /// <summary>
    /// A DOM-free parsed view of a JSON value: its <see cref="Kind"/> and, for objects, its members.
    /// Built with the streaming <see cref="Utf8JsonReader"/>.
    /// </summary>
    internal sealed class JsonValueInfo
    {
        /// <summary>Gets the value kind.</summary>
        public McpJsonValueKind Kind { get; private set; }

        /// <summary>Gets the object members (name to value), or null for non-object values.</summary>
        public Dictionary<string, JsonValueInfo>? Members { get; private set; }

        /// <summary>
        /// Parses raw JSON into a <see cref="JsonValueInfo"/>. Returns an absent value for null,
        /// blank, or malformed JSON.
        /// </summary>
        /// <param name="json">The raw JSON, or null.</param>
        /// <returns>The parsed value information.</returns>
        public static JsonValueInfo Parse(string? json)
        {
            if (String.IsNullOrWhiteSpace(json))
            {
                return new JsonValueInfo { Kind = McpJsonValueKind.Absent };
            }

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json!);
                Utf8JsonReader reader = new Utf8JsonReader(bytes);
                if (!reader.Read())
                {
                    return new JsonValueInfo { Kind = McpJsonValueKind.Absent };
                }

                return ReadValue(ref reader);
            }
            catch (JsonException)
            {
                return new JsonValueInfo { Kind = McpJsonValueKind.Absent };
            }
        }

        private static JsonValueInfo ReadValue(ref Utf8JsonReader reader)
        {
            JsonValueInfo info = new JsonValueInfo();

            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    info.Kind = McpJsonValueKind.Object;
                    info.Members = new Dictionary<string, JsonValueInfo>(StringComparer.Ordinal);
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        string name = reader.GetString() ?? String.Empty;
                        reader.Read();
                        info.Members[name] = ReadValue(ref reader);
                    }
                    break;
                case JsonTokenType.StartArray:
                    info.Kind = McpJsonValueKind.Array;
                    reader.Skip();
                    break;
                case JsonTokenType.String:
                    info.Kind = McpJsonValueKind.String;
                    break;
                case JsonTokenType.Number:
                    info.Kind = reader.TryGetInt64(out _) ? McpJsonValueKind.Integer : McpJsonValueKind.Number;
                    break;
                case JsonTokenType.True:
                case JsonTokenType.False:
                    info.Kind = McpJsonValueKind.Boolean;
                    break;
                case JsonTokenType.Null:
                    info.Kind = McpJsonValueKind.Null;
                    break;
                default:
                    info.Kind = McpJsonValueKind.Absent;
                    break;
            }

            return info;
        }
    }
}
