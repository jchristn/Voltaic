namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Text.Json;

    /// <summary>
    /// The 2026-07-28 rules for <c>x-mcp-header</c> tool parameters and for the values of the <c>Mcp-Name</c> and
    /// <c>Mcp-Param-{Name}</c> headers: annotation validation, value encoding (with the <c>=?base64?...?=</c>
    /// sentinel), decoding, and header-to-body comparison. Used by both servers and clients. Thread-safe.
    /// </summary>
    internal static class McpHeaderParameters
    {
        private const string Base64Prefix = "=?base64?";
        private const string Base64Suffix = "?=";
        private const long MaxSafeInteger = 9007199254740991;
        private const string TokenSymbols = "!#$%&'*+-.^_`|~";

        /// <summary>
        /// Returns the header parameters a tool input schema declares. Throws <see cref="ArgumentException"/> when any
        /// <c>x-mcp-header</c> annotation violates the specification's constraints, which makes the tool definition
        /// invalid. A null or non-object schema declares none. With <paramref name="strictIntegerBounds"/> (used for the
        /// server's own tools), an annotated integer whose schema bounds exceed the JavaScript safe range is rejected too,
        /// which is stricter than the specification (it constrains values, not bounds).
        /// </summary>
        internal static List<McpHeaderParameter> Extract(object? inputSchema, bool strictIntegerBounds = true)
        {
            List<McpHeaderParameter> parameters = new List<McpHeaderParameter>();
            if (inputSchema == null) return parameters;

            JsonElement root = inputSchema is JsonElement element ? element : JsonSerializer.SerializeToElement(inputSchema);
            if (root.ValueKind != JsonValueKind.Object) return parameters;

            Walk(root, new List<string>(), true, parameters, strictIntegerBounds);

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (McpHeaderParameter parameter in parameters)
            {
                if (!seen.Add(parameter.Name))
                {
                    throw new ArgumentException($"x-mcp-header value '{parameter.Name}' is used more than once (compared case-insensitively).");
                }
            }

            return parameters;
        }

        /// <summary>
        /// Returns the header parameters of another server's tool definition, or null (and the reason) when the
        /// definition's annotations violate the specification's constraints. Only those constraints apply here.
        /// </summary>
        internal static List<McpHeaderParameter>? TryExtract(JsonElement inputSchema, out string? error)
        {
            try
            {
                error = null;
                return Extract(inputSchema, false);
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Encodes a header value: plain when it is visible ASCII or inner spaces without leading or trailing
        /// whitespace and does not look like the sentinel, otherwise <c>=?base64?{UTF-8 Base64}?=</c>.
        /// </summary>
        internal static string Encode(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (IsPlainSafe(value) && !LooksLikeSentinel(value)) return value;
            return Base64Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + Base64Suffix;
        }

        /// <summary>
        /// Decodes a received header value. Returns false when the value contains characters a header value may not
        /// carry, or when a sentinel value is not valid Base64 or UTF-8.
        /// </summary>
        internal static bool TryDecode(string? headerValue, out string decoded)
        {
            decoded = String.Empty;
            if (headerValue == null) return false;

            foreach (char character in headerValue)
            {
                if (character != '\t' && (character < 0x20 || character > 0x7E)) return false;
            }

            if (!LooksLikeSentinel(headerValue))
            {
                decoded = headerValue;
                return true;
            }

            string encoded = headerValue.Substring(Base64Prefix.Length, headerValue.Length - Base64Prefix.Length - Base64Suffix.Length);
            try
            {
                decoded = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded));
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// Returns the header text for a body value (string as-is, integer in decimal, boolean in lowercase), already
        /// encoded, or null when the value cannot be mirrored (an object or array, or an integer outside the safe range).
        /// A value of another JSON type than the parameter declares is mirrored as that type, since a client must send
        /// the header whenever the argument has a value.
        /// </summary>
        internal static string? FormatValue(JsonElement value, string type)
        {
            if (!HasDeclaredType(value, type))
            {
                string? actual = ActualType(value);
                if (actual == null) return null;
                if (actual == "number") return value.GetRawText();
                if (actual != type) return FormatValue(value, actual);
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.String when type == "string":
                    return Encode(value.GetString() ?? String.Empty);
                case JsonValueKind.Number when type == "integer":
                    // Values outside the JavaScript safe integer range cannot be mirrored (the specification requires
                    // annotated integers to stay within it), so no header is produced.
                    if (!IsSafeInteger(value)) return null;
                    return Math.Truncate(value.GetDecimal()).ToString(CultureInfo.InvariantCulture);
                case JsonValueKind.True when type == "boolean":
                    return "true";
                case JsonValueKind.False when type == "boolean":
                    return "false";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Returns true when a body value has the JSON type the parameter declares (an integral number for
        /// <c>integer</c>). Values of another type are left to input schema validation.
        /// </summary>
        internal static bool HasDeclaredType(JsonElement value, string type)
        {
            switch (type)
            {
                case "string":
                    return value.ValueKind == JsonValueKind.String;
                case "boolean":
                    return value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False;
                case "integer":
                    return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number) && number == Math.Truncate(number);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns the header parameter type that describes a body value (<c>string</c>, <c>boolean</c>, <c>integer</c>
        /// for integral numbers, <c>number</c> for others), or null for objects and arrays, which cannot be mirrored.
        /// </summary>
        internal static string? ActualType(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return "string";
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return "boolean";
                case JsonValueKind.Number:
                    return value.TryGetDecimal(out decimal number) && number == Math.Truncate(number) ? "integer" : "number";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Returns true when an integral number is within the JavaScript safe integer range (plus or minus 2^53 - 1).
        /// </summary>
        internal static bool IsSafeInteger(JsonElement value)
        {
            return value.ValueKind == JsonValueKind.Number
                && value.TryGetDecimal(out decimal number)
                && number == Math.Truncate(number)
                && Math.Abs(number) <= MaxSafeInteger;
        }

        /// <summary>
        /// Reads the value at a parameter's property path in the call arguments. Returns false when the value is
        /// absent or null, in which case the header must be omitted.
        /// </summary>
        internal static bool TryGetValue(JsonElement arguments, IReadOnlyList<string> path, out JsonElement value)
        {
            value = arguments;
            foreach (string key in path)
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out JsonElement next))
                {
                    return false;
                }

                value = next;
            }

            return value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined;
        }

        /// <summary>
        /// Returns true when a decoded header value equals the body value. Integers are compared numerically, so
        /// <c>42.0</c> matches <c>42</c>.
        /// </summary>
        internal static bool Matches(string decodedHeader, JsonElement bodyValue, string type)
        {
            switch (type)
            {
                case "string":
                    return bodyValue.ValueKind == JsonValueKind.String && StringComparer.Ordinal.Equals(decodedHeader, bodyValue.GetString());
                case "boolean":
                    return (bodyValue.ValueKind == JsonValueKind.True && decodedHeader == "true")
                        || (bodyValue.ValueKind == JsonValueKind.False && decodedHeader == "false");
                case "integer":
                case "number":
                    return bodyValue.ValueKind == JsonValueKind.Number
                        && Decimal.TryParse(decodedHeader, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal header)
                        && bodyValue.TryGetDecimal(out decimal body)
                        && header == body;
                default:
                    return false;
            }
        }

        private static void Walk(JsonElement schema, List<string> path, bool reachable, List<McpHeaderParameter> parameters, bool strictIntegerBounds)
        {
            if (schema.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in schema.EnumerateArray()) Walk(item, path, false, parameters, strictIntegerBounds);
                return;
            }

            if (schema.ValueKind != JsonValueKind.Object) return;

            if (schema.TryGetProperty(McpProtocol.HeaderAnnotationKeyword, out JsonElement annotation))
            {
                string where = path.Count == 0 ? "the schema root" : "'" + String.Join(".", path) + "'";
                if (!reachable || path.Count == 0)
                {
                    throw new ArgumentException($"x-mcp-header at {where} is not on a property reachable from the root through 'properties' keys alone.");
                }

                if (annotation.ValueKind != JsonValueKind.String || String.IsNullOrEmpty(annotation.GetString()))
                {
                    throw new ArgumentException($"x-mcp-header at {where} must be a non-empty string.");
                }

                string name = annotation.GetString()!;
                if (!IsToken(name))
                {
                    throw new ArgumentException($"x-mcp-header value '{name}' at {where} is not a valid HTTP field-name token.");
                }

                string? type = DeclaredPrimitiveType(schema);
                if (type != "string" && type != "integer" && type != "boolean")
                {
                    throw new ArgumentException($"x-mcp-header at {where} must annotate a property of type string, integer, or boolean.");
                }

                if (strictIntegerBounds && type == "integer" && !IntegerBoundsAreSafe(schema))
                {
                    throw new ArgumentException($"x-mcp-header at {where} annotates an integer whose bounds exceed the JavaScript safe integer range.");
                }

                parameters.Add(new McpHeaderParameter(name, path.ToList(), type!));
            }

            foreach (JsonProperty property in schema.EnumerateObject())
            {
                if (property.Name == "properties" && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty child in property.Value.EnumerateObject())
                    {
                        path.Add(child.Name);
                        Walk(child.Value, path, reachable, parameters, strictIntegerBounds);
                        path.RemoveAt(path.Count - 1);
                    }
                }
                else if (IsDataKeyword(property.Name))
                {
                    // Instance data (default, const, enum, examples), not subschemas.
                    continue;
                }
                else if (property.Value.ValueKind == JsonValueKind.Object || property.Value.ValueKind == JsonValueKind.Array)
                {
                    // items, oneOf/anyOf/allOf/not, if/then/else, $defs, patternProperties, and every other keyword
                    // leave the statically reachable chain.
                    Walk(property.Value, path, false, parameters, strictIntegerBounds);
                }
            }
        }

        // The property's type: a single "string", "integer", or "boolean", or one of those together with "null" (a
        // nullable parameter, whose header is omitted when the value is null). Without "type", a property whose enum or
        // const values are all of one primitive type has that type. Anything else returns null.
        private static string? DeclaredPrimitiveType(JsonElement schema)
        {
            if (!schema.TryGetProperty("type", out JsonElement typeElement)) return TypeOfValues(schema);
            if (typeElement.ValueKind == JsonValueKind.String) return typeElement.GetString();
            if (typeElement.ValueKind != JsonValueKind.Array) return null;
            if (!typeElement.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)) return null;

            List<string> types = typeElement.EnumerateArray()
                .Select(item => item.GetString()!)
                .Where(item => item != "null")
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return types.Count == 1 ? types[0] : null;
        }

        private static string? TypeOfValues(JsonElement schema)
        {
            List<JsonElement> values = new List<JsonElement>();
            if (schema.TryGetProperty("const", out JsonElement constant)) values.Add(constant);
            if (schema.TryGetProperty("enum", out JsonElement enumeration) && enumeration.ValueKind == JsonValueKind.Array) values.AddRange(enumeration.EnumerateArray());
            List<string?> types = values.Where(value => value.ValueKind != JsonValueKind.Null).Select(ActualType).Distinct(StringComparer.Ordinal).ToList();
            return types.Count == 1 ? types[0] : null;
        }

        private static bool IsDataKeyword(string keyword)
        {
            return keyword == "default" || keyword == "const" || keyword == "enum" || keyword == "examples";
        }

        private static bool IntegerBoundsAreSafe(JsonElement schema)
        {
            foreach (string keyword in new[] { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum" })
            {
                if (schema.TryGetProperty(keyword, out JsonElement bound) && bound.ValueKind == JsonValueKind.Number
                    && bound.TryGetDecimal(out decimal value) && Math.Abs(value) > MaxSafeInteger)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsToken(string value)
        {
            if (value.Length == 0) return false;
            foreach (char character in value)
            {
                bool valid = (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z')
                    || (character >= '0' && character <= '9') || TokenSymbols.IndexOf(character) >= 0;
                if (!valid) return false;
            }

            return true;
        }

        private static bool IsPlainSafe(string value)
        {
            if (value.Length == 0) return true;
            if (Char.IsWhiteSpace(value[0]) || Char.IsWhiteSpace(value[value.Length - 1])) return false;
            foreach (char character in value)
            {
                if (character < 0x20 || character > 0x7E) return false;
            }

            return true;
        }

        private static bool LooksLikeSentinel(string value)
        {
            return value.Length >= Base64Prefix.Length + Base64Suffix.Length
                && value.StartsWith(Base64Prefix, StringComparison.Ordinal)
                && value.EndsWith(Base64Suffix, StringComparison.Ordinal);
        }
    }
}
