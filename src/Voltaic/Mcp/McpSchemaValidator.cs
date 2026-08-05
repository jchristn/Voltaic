namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json;

    /// <summary>
    /// A DOM-free, best-effort JSON Schema validator covering the subset Voltaic enforces
    /// (<c>type</c>, <c>required</c>, and nested object <c>properties</c>). It parses values with the
    /// streaming <see cref="Utf8JsonReader"/> rather than any System.Text.Json DOM type.
    /// </summary>
    internal static class McpSchemaValidator
    {
        /// <summary>
        /// Validates a value's raw JSON against a schema, throwing an
        /// <see cref="McpProtocolException"/> validation error on the first violation.
        /// </summary>
        /// <param name="schema">The schema object (or its JSON), or null to skip validation.</param>
        /// <param name="valueJson">The raw JSON of the value, or null when the value is absent.</param>
        /// <param name="context">A human-readable context prefix for error messages.</param>
        /// <exception cref="McpProtocolException">Thrown when validation fails.</exception>
        public static void Validate(object? schema, string? valueJson, string context)
        {
            if (schema == null)
            {
                return;
            }

            McpJsonSchema? parsed = DeserializeSchema(schema);
            if (parsed == null)
            {
                return;
            }

            JsonValueInfo value = JsonValueInfo.Parse(valueJson);
            ValidateNode(parsed, value, context);
        }

        private static McpJsonSchema? DeserializeSchema(object schema)
        {
            try
            {
                if (schema is McpJsonSchema typed)
                {
                    return typed;
                }

                string json = schema as string ?? JsonSerializer.Serialize(schema);
                return JsonSerializer.Deserialize<McpJsonSchema>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static void ValidateNode(McpJsonSchema schema, JsonValueInfo value, string context)
        {
            if (schema.Required != null && schema.Required.Count > 0)
            {
                foreach (string requiredName in schema.Required)
                {
                    if (String.IsNullOrEmpty(requiredName))
                    {
                        continue;
                    }

                    if (value.Kind != McpJsonValueKind.Object || value.Members == null || !value.Members.ContainsKey(requiredName))
                    {
                        throw McpProtocolException.ValidationError($"{context} is missing required property '{requiredName}'.");
                    }
                }
            }

            if (String.IsNullOrEmpty(schema.Type))
            {
                return;
            }

            string expectedType = schema.Type!;

            if (value.Kind == McpJsonValueKind.Absent)
            {
                if (expectedType == "object")
                {
                    return;
                }

                throw McpProtocolException.ValidationError($"{context} is required.");
            }

            if (!TypeMatches(expectedType, value.Kind))
            {
                throw McpProtocolException.ValidationError($"{context} must be a JSON {expectedType}.");
            }

            if (expectedType == "object" && value.Kind == McpJsonValueKind.Object && schema.Properties != null && value.Members != null)
            {
                foreach (KeyValuePair<string, McpJsonSchema> property in schema.Properties)
                {
                    if (value.Members.TryGetValue(property.Key, out JsonValueInfo? memberValue))
                    {
                        ValidateNode(property.Value, memberValue, $"{context}.{property.Key}");
                    }
                }
            }
        }

        private static bool TypeMatches(string expectedType, McpJsonValueKind kind)
        {
            return expectedType switch
            {
                "object" => kind == McpJsonValueKind.Object,
                "array" => kind == McpJsonValueKind.Array,
                "string" => kind == McpJsonValueKind.String,
                "number" => kind == McpJsonValueKind.Number || kind == McpJsonValueKind.Integer,
                "integer" => kind == McpJsonValueKind.Integer,
                "boolean" => kind == McpJsonValueKind.Boolean,
                "null" => kind == McpJsonValueKind.Null,
                _ => true
            };
        }
    }

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
