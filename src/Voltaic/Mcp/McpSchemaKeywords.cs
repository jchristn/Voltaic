namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text.Json;

    /// <summary>
    /// The JSON Schema keywords Voltaic recognizes, by dialect: which keywords hold subschemas (so references,
    /// anchors, and embedded resources are found only where a schema can be), and the structural rules each keyword's
    /// value must follow for a schema to be accepted at registration.
    /// </summary>
    internal static class McpSchemaKeywords
    {
        private static readonly HashSet<string> _SchemaMaps = new HashSet<string>(StringComparer.Ordinal)
        {
            "properties", "patternProperties", "$defs", "definitions"
        };

        private static readonly HashSet<string> _SchemaArrays = new HashSet<string>(StringComparer.Ordinal)
        {
            "allOf", "anyOf", "oneOf"
        };

        private static readonly HashSet<string> _SingleSchemas = new HashSet<string>(StringComparer.Ordinal)
        {
            "not", "if", "then", "else", "additionalProperties", "contains", "propertyNames"
        };

        private static readonly HashSet<string> _Draft202012Only = new HashSet<string>(StringComparer.Ordinal)
        {
            "prefixItems", "dependentSchemas", "dependentRequired", "unevaluatedProperties", "unevaluatedItems",
            "minContains", "maxContains", "$anchor", "$dynamicAnchor", "$dynamicRef"
        };

        private static readonly HashSet<string> _Draft07Only = new HashSet<string>(StringComparer.Ordinal)
        {
            "dependencies", "additionalItems"
        };

        private static readonly HashSet<string> _TypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "null", "boolean", "object", "array", "number", "string", "integer"
        };

        private static readonly string[] _NumberKeywords = { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum" };

        private static readonly string[] _CountKeywords =
        {
            "minLength", "maxLength", "minItems", "maxItems", "minProperties", "maxProperties", "minContains", "maxContains"
        };

        private static readonly string[] _StringKeywords = { "$ref", "$dynamicRef", "$id", "$anchor", "$dynamicAnchor", "$schema", "format" };

        /// <summary>
        /// Returns true when the keyword belongs to the dialect (keywords of the other dialect are unknown keywords there).
        /// </summary>
        internal static bool Applies(string keyword, bool draft07)
        {
            return draft07 ? !_Draft202012Only.Contains(keyword) : !_Draft07Only.Contains(keyword);
        }

        /// <summary>
        /// Returns the subschemas a schema object holds under the dialect's applicator keywords. Values of unknown
        /// keywords and of <c>enum</c>, <c>const</c>, <c>default</c>, and <c>examples</c> are data, not schemas.
        /// </summary>
        internal static List<JsonElement> Subschemas(JsonElement schema, bool draft07)
        {
            List<JsonElement> subschemas = new List<JsonElement>();
            if (schema.ValueKind != JsonValueKind.Object) return subschemas;

            foreach (JsonProperty member in schema.EnumerateObject())
            {
                if (!Applies(member.Name, draft07)) continue;
                JsonElement value = member.Value;
                if (_SchemaMaps.Contains(member.Name) || member.Name == "dependentSchemas" || member.Name == "dependencies")
                {
                    if (value.ValueKind != JsonValueKind.Object) continue;
                    subschemas.AddRange(value.EnumerateObject().Select(entry => entry.Value).Where(IsSchemaShape));
                }
                else if (_SchemaArrays.Contains(member.Name) || member.Name == "prefixItems")
                {
                    if (value.ValueKind == JsonValueKind.Array) subschemas.AddRange(value.EnumerateArray().Where(IsSchemaShape));
                }
                else if (member.Name == "items")
                {
                    if (value.ValueKind == JsonValueKind.Array) subschemas.AddRange(value.EnumerateArray().Where(IsSchemaShape));
                    else if (IsSchemaShape(value)) subschemas.Add(value);
                }
                else if (_SingleSchemas.Contains(member.Name) || member.Name == "additionalItems" || member.Name == "unevaluatedProperties" || member.Name == "unevaluatedItems")
                {
                    if (IsSchemaShape(value)) subschemas.Add(value);
                }
            }

            return subschemas;
        }

        /// <summary>
        /// Describes the first structural problem in a schema (a keyword value of the wrong type, an unknown type
        /// name, a pattern that is not a regular expression, a subschema that is not an object or boolean), or
        /// returns null when the schema is well formed. Unknown keywords are ignored.
        /// </summary>
        internal static string? FindMalformed(JsonElement schema, string location, bool draft07, Func<string, bool> isValidPattern, int depth)
        {
            if (depth > 256) return null;
            if (schema.ValueKind == JsonValueKind.True || schema.ValueKind == JsonValueKind.False) return null;
            if (schema.ValueKind != JsonValueKind.Object) return $"{location} must be a schema (an object or a boolean).";

            if (draft07 && schema.TryGetProperty("$ref", out JsonElement reference))
            {
                // draft-07 ignores every keyword beside $ref, so only the reference is checked, plus the subschemas in
                // its sibling containers, which a JSON pointer can still reach.
                if (reference.ValueKind != JsonValueKind.String) return $"{location}/$ref must be a string.";
                foreach (JsonProperty member in schema.EnumerateObject())
                {
                    if (!_SchemaMaps.Contains(member.Name) || member.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (JsonProperty entry in member.Value.EnumerateObject())
                    {
                        string? problem = FindMalformed(entry.Value, $"{location}/{member.Name}/{entry.Name}", draft07, isValidPattern, depth + 1);
                        if (problem != null) return problem;
                    }
                }

                return null;
            }

            foreach (JsonProperty member in schema.EnumerateObject())
            {
                if (!Applies(member.Name, draft07)) continue;
                string at = $"{location}/{member.Name}";
                string? problem = CheckKeyword(member.Name, member.Value, at, draft07, isValidPattern, depth);
                if (problem != null) return problem;
            }

            return null;
        }

        private static string? CheckKeyword(string keyword, JsonElement value, string at, bool draft07, Func<string, bool> isValidPattern, int depth)
        {
            // Each dialect has one definitions container; the other dialect's name is an unknown keyword there.
            if ((keyword == "definitions" && !draft07) || (keyword == "$defs" && draft07)) return null;

            if (keyword == "type")
            {
                if (value.ValueKind == JsonValueKind.String) return _TypeNames.Contains(value.GetString()!) ? null : $"{at}: '{value.GetString()}' is not a JSON Schema type.";
                if (value.ValueKind != JsonValueKind.Array) return $"{at} must be a type name or an array of type names.";
                List<string> names = new List<string>();
                foreach (JsonElement name in value.EnumerateArray())
                {
                    if (name.ValueKind != JsonValueKind.String || !_TypeNames.Contains(name.GetString()!)) return $"{at}: {name.GetRawText()} is not a JSON Schema type.";
                    names.Add(name.GetString()!);
                }

                return names.Distinct(StringComparer.Ordinal).Count() == names.Count ? null : $"{at} must not repeat a type.";
            }

            if (_NumberKeywords.Contains(keyword))
            {
                return value.ValueKind == JsonValueKind.Number ? null : $"{at} must be a number.";
            }

            if (keyword == "multipleOf")
            {
                return value.ValueKind == JsonValueKind.Number && value.GetDouble() > 0 ? null : $"{at} must be a number greater than 0.";
            }

            if (_CountKeywords.Contains(keyword))
            {
                return IsCount(value) ? null : $"{at} must be a non-negative integer.";
            }

            if (_StringKeywords.Contains(keyword))
            {
                return value.ValueKind == JsonValueKind.String ? null : $"{at} must be a string.";
            }

            if (keyword == "pattern")
            {
                if (value.ValueKind != JsonValueKind.String) return $"{at} must be a string.";
                return isValidPattern(value.GetString()!) ? null : $"{at}: '{value.GetString()}' is not a valid regular expression.";
            }

            if (keyword == "uniqueItems")
            {
                return value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False ? null : $"{at} must be a boolean.";
            }

            if (keyword == "required")
            {
                return IsUniqueStrings(value) ? null : $"{at} must be an array of unique strings.";
            }

            if (keyword == "enum")
            {
                return value.ValueKind == JsonValueKind.Array ? null : $"{at} must be an array.";
            }

            if (keyword == "dependentRequired")
            {
                if (value.ValueKind != JsonValueKind.Object) return $"{at} must be an object.";
                foreach (JsonProperty entry in value.EnumerateObject())
                {
                    if (!IsUniqueStrings(entry.Value)) return $"{at}/{entry.Name} must be an array of unique strings.";
                }

                return null;
            }

            if (keyword == "dependencies")
            {
                if (value.ValueKind != JsonValueKind.Object) return $"{at} must be an object.";
                foreach (JsonProperty entry in value.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.Array)
                    {
                        if (!IsUniqueStrings(entry.Value)) return $"{at}/{entry.Name} must be a schema or an array of unique strings.";
                        continue;
                    }

                    string? problem = FindMalformed(entry.Value, $"{at}/{entry.Name}", draft07, isValidPattern, depth + 1);
                    if (problem != null) return problem;
                }

                return null;
            }

            if (_SchemaMaps.Contains(keyword) || keyword == "dependentSchemas")
            {
                if (value.ValueKind != JsonValueKind.Object) return $"{at} must be an object whose values are schemas.";
                foreach (JsonProperty entry in value.EnumerateObject())
                {
                    if (keyword == "patternProperties" && !isValidPattern(entry.Name)) return $"{at}: '{entry.Name}' is not a valid regular expression.";
                    string? problem = FindMalformed(entry.Value, $"{at}/{entry.Name}", draft07, isValidPattern, depth + 1);
                    if (problem != null) return problem;
                }

                return null;
            }

            if (_SchemaArrays.Contains(keyword) || keyword == "prefixItems" || (keyword == "items" && value.ValueKind == JsonValueKind.Array))
            {
                if (keyword == "items" && !draft07) return $"{at}: the array form of 'items' is not valid in JSON Schema 2020-12; use 'prefixItems' (or declare draft-07).";
                if (value.ValueKind != JsonValueKind.Array) return $"{at} must be an array of schemas.";
                if (value.GetArrayLength() == 0 && keyword != "items") return $"{at} must be a non-empty array of schemas.";
                int index = 0;
                foreach (JsonElement item in value.EnumerateArray())
                {
                    string? problem = FindMalformed(item, $"{at}/{index}", draft07, isValidPattern, depth + 1);
                    if (problem != null) return problem;
                    index++;
                }

                return null;
            }

            if (_SingleSchemas.Contains(keyword) || keyword == "items" || keyword == "additionalItems" || keyword == "unevaluatedProperties" || keyword == "unevaluatedItems")
            {
                return FindMalformed(value, at, draft07, isValidPattern, depth + 1);
            }

            return null;
        }

        private static bool IsSchemaShape(JsonElement value)
        {
            return value.ValueKind == JsonValueKind.Object || value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False;
        }

        private static bool IsCount(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Number) return false;
            if (value.TryGetInt64(out long whole)) return whole >= 0;

            // 1.0 is an integer in JSON Schema.
            return Double.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                && number >= 0 && Math.Floor(number) == number && !Double.IsInfinity(number);
        }

        private static bool IsUniqueStrings(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Array) return false;
            List<string> items = new List<string>();
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) return false;
                items.Add(item.GetString()!);
            }

            return items.Distinct(StringComparer.Ordinal).Count() == items.Count;
        }
    }
}
