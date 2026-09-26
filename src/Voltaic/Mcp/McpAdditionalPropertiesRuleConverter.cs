namespace Voltaic.Mcp
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Reads and writes the JSON Schema <c>additionalProperties</c> keyword, which may be a boolean or a
    /// schema object, without any System.Text.Json DOM type.
    /// </summary>
    internal sealed class McpAdditionalPropertiesRuleConverter : JsonConverter<McpAdditionalPropertiesRule>
    {
        /// <summary>
        /// Reads the keyword. Unsupported token types are skipped and treated as allowing additional properties.
        /// </summary>
        /// <param name="reader">The reader positioned at the keyword's value.</param>
        /// <param name="typeToConvert">The target type.</param>
        /// <param name="options">Serializer options.</param>
        /// <returns>The parsed rule.</returns>
        public override McpAdditionalPropertiesRule? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.True:
                    return new McpAdditionalPropertiesRule { Allowed = true };
                case JsonTokenType.False:
                    return new McpAdditionalPropertiesRule { Allowed = false };
                case JsonTokenType.StartObject:
                    McpJsonSchema? schema = JsonSerializer.Deserialize<McpJsonSchema>(ref reader, options);
                    return new McpAdditionalPropertiesRule { Allowed = true, Schema = schema };
                case JsonTokenType.Null:
                    return null;
                default:
                    reader.Skip();
                    return new McpAdditionalPropertiesRule { Allowed = true };
            }
        }

        /// <summary>
        /// Writes the keyword as a boolean, or as the schema object when one is present.
        /// </summary>
        /// <param name="writer">The writer.</param>
        /// <param name="value">The rule to write.</param>
        /// <param name="options">Serializer options.</param>
        public override void Write(Utf8JsonWriter writer, McpAdditionalPropertiesRule value, JsonSerializerOptions options)
        {
            if (value.Schema != null)
            {
                JsonSerializer.Serialize(writer, value.Schema, options);
                return;
            }

            writer.WriteBooleanValue(value.Allowed);
        }
    }
}
