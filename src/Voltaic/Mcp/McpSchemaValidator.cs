namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Validates JSON values (tool arguments and structured tool output) against a JSON Schema, operating
    /// directly on <see cref="JsonElement"/>, in the schema's declared dialect: JSON Schema 2020-12 (the default when
    /// <c>$schema</c> is absent) or draft-07. Both dialects: <c>type</c>, <c>enum</c>, <c>const</c>,
    /// <c>properties</c>, <c>required</c>, <c>additionalProperties</c>, <c>patternProperties</c>,
    /// <c>propertyNames</c>, <c>minProperties</c>, <c>maxProperties</c>, <c>items</c>, <c>contains</c>,
    /// <c>minItems</c>, <c>maxItems</c>, <c>uniqueItems</c>, <c>minLength</c>, <c>maxLength</c>, <c>pattern</c>
    /// (ECMA-262), <c>minimum</c>, <c>maximum</c>, <c>exclusiveMinimum</c>, <c>exclusiveMaximum</c>,
    /// <c>multipleOf</c>, <c>allOf</c>, <c>anyOf</c>, <c>oneOf</c>, <c>not</c>, <c>if</c>/<c>then</c>/<c>else</c>,
    /// <c>$ref</c>, and boolean schemas. 2020-12 only: <c>prefixItems</c>, <c>dependentRequired</c>,
    /// <c>dependentSchemas</c>, <c>minContains</c>, <c>maxContains</c>, <c>unevaluatedProperties</c>,
    /// <c>unevaluatedItems</c>, <c>$anchor</c>, <c>$dynamicAnchor</c>, and <c>$dynamicRef</c> (dynamic scope).
    /// draft-07 only: the array form of <c>items</c> with <c>additionalItems</c>, <c>dependencies</c>, fragment
    /// <c>$id</c> anchors, and <c>$ref</c> overriding its sibling keywords. References resolve within the resource
    /// they appear in (a subschema with its own <c>$id</c> starts a resource). Another dialect, or a reference that
    /// cannot be resolved within the schema, is reported as an error rather than validated permissively. Annotation
    /// keywords (such as <c>format</c>, <c>description</c>, or <c>x-mcp-header</c>) are ignored, and a malformed
    /// keyword is ignored on its own without disabling the rest of the schema.
    /// Thread safety: all members are safe for concurrent use.
    /// </summary>
    internal static class McpSchemaValidator
    {
        private static readonly int _MaximumNestingDepth = 512;
        private static readonly int _MaximumCompositionDepth = 64;
        private static readonly int _EvaluationBudget = 1000000;
        private static readonly int _MaximumCachedPatterns = 512;
        private static readonly int _MaximumMessageValueLength = 200;
        private static readonly TimeSpan _RegexTimeout = TimeSpan.FromSeconds(1);
        private static readonly JsonDocumentOptions _DocumentOptions = new JsonDocumentOptions { MaxDepth = 256 };
        private static readonly JsonElement _EmptyObject = CreateEmptyObject();
        private static readonly ConcurrentDictionary<string, Regex?> _RegexCache = new ConcurrentDictionary<string, Regex?>(StringComparer.Ordinal);
        private static readonly string[] _KnownTypes = new[] { "object", "array", "string", "number", "integer", "boolean", "null" };

        /// <summary>
        /// Validates a value's raw JSON against a schema, throwing an
        /// <see cref="McpProtocolException"/> validation error on the first violation. An absent value
        /// (null or blank JSON) is validated as an empty object when the schema allows objects, and is otherwise
        /// reported as required.
        /// </summary>
        /// <param name="schema">The schema: a <see cref="JsonElement"/>, a JSON string, or any serializable object; null skips validation.</param>
        /// <param name="valueJson">The raw JSON of the value, or null when the value is absent.</param>
        /// <param name="context">A human-readable context prefix for error messages, such as <c>Tool 'x' arguments</c>.</param>
        /// <exception cref="McpProtocolException">Thrown when validation fails; the message names the failing path.</exception>
        public static void Validate(object? schema, string? valueJson, string context)
        {
            if (schema == null)
            {
                return;
            }

            string path = String.IsNullOrEmpty(context) ? "Value" : context;

            if (!TryGetSchemaElement(schema, out JsonElement root))
            {
                return;
            }

            if (root.ValueKind != JsonValueKind.Object && root.ValueKind != JsonValueKind.True && root.ValueKind != JsonValueKind.False)
            {
                // A schema that is not an object or a boolean has no keywords to enforce.
                return;
            }

            McpSchemaDocument document = new McpSchemaDocument(root);
            string? schemaProblem = Check(document);
            if (schemaProblem != null)
            {
                throw McpProtocolException.ValidationError($"{path} cannot be validated: {schemaProblem}");
            }

            int budget = _EvaluationBudget;
            string? error;

            if (String.IsNullOrWhiteSpace(valueJson))
            {
                if (!AllowsObject(root))
                {
                    throw McpProtocolException.ValidationError($"{path} is required.");
                }

                error = Evaluate(root, _EmptyObject, path, document, 0, 0, ref budget);
            }
            else
            {
                JsonDocument parsed;
                try
                {
                    parsed = JsonDocument.Parse(valueJson!, _DocumentOptions);
                }
                catch (JsonException)
                {
                    throw McpProtocolException.ValidationError($"{path} is not valid JSON.");
                }

                using (parsed)
                {
                    error = Evaluate(root, parsed.RootElement, path, document, 0, 0, ref budget);
                }
            }

            if (budget < 0)
            {
                // Exhausting the budget or the depth bound inside a negating keyword (not, oneOf) must never turn into
                // a pass, so an aborted evaluation always rejects.
                throw McpProtocolException.ValidationError($"{path} is too complex or too deeply nested to validate against the schema.");
            }

            if (error != null)
            {
                throw McpProtocolException.ValidationError(error);
            }
        }

        /// <summary>
        /// Checks that a schema can be validated as written: its <c>$schema</c> dialect is supported (JSON Schema
        /// 2020-12, the default, or draft-07) and every <c>$ref</c> and <c>$dynamicRef</c> resolves within it.
        /// Returns a description of the first problem, or null when the schema is usable.
        /// </summary>
        /// <param name="schema">The schema: a <see cref="JsonElement"/>, a JSON string, or any serializable object.</param>
        public static string? CheckSchema(object? schema)
        {
            if (schema == null || !TryGetSchemaElement(schema, out JsonElement root)) return null;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return Check(new McpSchemaDocument(root));
        }

        private static string? Check(McpSchemaDocument document)
        {
            string? dialect = document.Dialect;
            if (!McpSchemaDocument.IsSupportedDialect(dialect))
            {
                return $"the JSON Schema dialect '{dialect}' is not supported (supported: {McpSchemaDocument.Draft202012}, the default, and draft-07).";
            }

            if (!document.IsDraft07 && HasArrayItems(document.Root, 0))
            {
                return "the array form of 'items' is not valid in JSON Schema 2020-12; use 'prefixItems' (or declare draft-07).";
            }

            string? unresolved = document.FindUnresolvedReference();
            if (unresolved != null)
            {
                return $"the reference '{unresolved}' cannot be resolved within the schema; external references are not supported.";
            }

            return null;
        }

        private static bool HasArrayItems(JsonElement element, int depth)
        {
            if (depth > 256) return false;
            if (element.ValueKind == JsonValueKind.Array) return element.EnumerateArray().Any(item => HasArrayItems(item, depth + 1));
            if (element.ValueKind != JsonValueKind.Object) return false;
            foreach (JsonProperty member in element.EnumerateObject())
            {
                if (member.Name == "enum" || member.Name == "const" || member.Name == "default" || member.Name == "examples") continue;
                if (member.Name == "items" && member.Value.ValueKind == JsonValueKind.Array) return true;
                if (HasArrayItems(member.Value, depth + 1)) return true;
            }

            return false;
        }

        private static bool TryGetSchemaElement(object schema, out JsonElement root)
        {
            root = default;
            try
            {
                if (schema is JsonElement element)
                {
                    root = element;
                    return true;
                }

                if (schema is string text)
                {
                    using (JsonDocument parsed = JsonDocument.Parse(text, _DocumentOptions))
                    {
                        root = parsed.RootElement.Clone();
                    }

                    return true;
                }

                root = JsonSerializer.SerializeToElement(schema);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static JsonElement CreateEmptyObject()
        {
            using (JsonDocument document = JsonDocument.Parse("{}"))
            {
                return document.RootElement.Clone();
            }
        }

        // An absent value is treated as an empty object unless the root schema names types that exclude objects.
        private static bool AllowsObject(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement type))
            {
                return true;
            }

            List<string> names = ReadTypeNames(type);
            return names.Count == 0 || names.Contains("object");
        }

        private static string? Evaluate(JsonElement schema, JsonElement instance, string path, McpSchemaDocument root, int depth, int chain, ref int budget, McpSchemaEvaluated? evaluated = null)
        {
            if (budget < 0)
            {
                return $"{path} is too complex to validate against the schema.";
            }

            budget--;

            if (depth > _MaximumNestingDepth || chain > _MaximumCompositionDepth)
            {
                budget = -1;
                return $"{path} is nested too deeply to validate against the schema.";
            }

            if (schema.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            if (schema.ValueKind == JsonValueKind.False)
            {
                return $"{path} is not allowed by the schema.";
            }

            if (schema.ValueKind != JsonValueKind.Object)
            {
                // A malformed subschema (for example a number) has nothing to enforce.
                return null;
            }

            // A subschema with its own $id is a resource: it becomes the base for references and joins the dynamic scope.
            bool entered = root.Enter(schema);
            try
            {
                return EvaluateKeywords(schema, instance, path, root, depth, chain, ref budget, evaluated);
            }
            finally
            {
                if (entered) root.Exit();
            }
        }

        private static string? EvaluateKeywords(JsonElement schema, JsonElement instance, string path, McpSchemaDocument root, int depth, int chain, ref int budget, McpSchemaEvaluated? evaluated)
        {
            // Properties and items this schema's keywords evaluated, for unevaluatedProperties and unevaluatedItems.
            McpSchemaEvaluated local = new McpSchemaEvaluated();

            string? error = CheckReference(schema, instance, path, root, depth, chain, ref budget, local);
            if (error != null) return error;

            // draft-07: a schema with $ref is only that reference; its other keywords are ignored.
            if (root.IsDraft07 && schema.TryGetProperty("$ref", out JsonElement _))
            {
                evaluated?.Merge(local);
                return null;
            }

            error = CheckType(schema, instance, path);
            if (error != null) return error;

            error = CheckEnumAndConst(schema, instance, path);
            if (error != null) return error;

            switch (instance.ValueKind)
            {
                case JsonValueKind.Object:
                    error = CheckObject(schema, instance, path, root, depth, chain, ref budget, local);
                    break;
                case JsonValueKind.Array:
                    error = CheckArray(schema, instance, path, root, depth, ref budget, local);
                    break;
                case JsonValueKind.String:
                    error = CheckString(schema, instance, path);
                    break;
                case JsonValueKind.Number:
                    error = CheckNumber(schema, instance, path);
                    break;
            }

            if (error != null) return error;

            error = CheckComposition(schema, instance, path, root, depth, chain, ref budget, local);
            if (error != null) return error;

            error = CheckUnevaluated(schema, instance, path, root, depth, ref budget, local);
            if (error != null) return error;

            evaluated?.Merge(local);
            return null;
        }

        // unevaluatedProperties and unevaluatedItems apply to what no other keyword of this schema (including its
        // in-place subschemas that passed) evaluated.
        private static string? CheckUnevaluated(JsonElement schema, JsonElement instance, string path, McpSchemaDocument root, int depth, ref int budget, McpSchemaEvaluated local)
        {
            // unevaluatedProperties and unevaluatedItems are 2019-09/2020-12 keywords, not draft-07 ones.
            if (root.IsDraft07) return null;

            if (instance.ValueKind == JsonValueKind.Object && schema.TryGetProperty("unevaluatedProperties", out JsonElement unevaluatedProperties) && IsSchema(unevaluatedProperties))
            {
                foreach (JsonProperty member in instance.EnumerateObject())
                {
                    if (local.Properties.Contains(member.Name)) continue;
                    if (unevaluatedProperties.ValueKind == JsonValueKind.False)
                    {
                        return $"{path} has unexpected property '{member.Name}'; the schema does not allow unevaluated properties.";
                    }

                    string? error = Evaluate(unevaluatedProperties, member.Value, $"{path}.{member.Name}", root, depth + 1, 0, ref budget);
                    if (error != null) return error;
                    local.Properties.Add(member.Name);
                }
            }

            if (instance.ValueKind == JsonValueKind.Array && schema.TryGetProperty("unevaluatedItems", out JsonElement unevaluatedItems) && IsSchema(unevaluatedItems))
            {
                int index = 0;
                foreach (JsonElement item in instance.EnumerateArray())
                {
                    if (!local.Items.Contains(index))
                    {
                        if (unevaluatedItems.ValueKind == JsonValueKind.False)
                        {
                            return $"{path}[{index}] is not allowed; the schema does not allow unevaluated items.";
                        }

                        string? error = Evaluate(unevaluatedItems, item, $"{path}[{index}]", root, depth + 1, 0, ref budget);
                        if (error != null) return error;
                        local.Items.Add(index);
                    }

                    index++;
                }
            }

            return null;
        }

        #region Keywords

        private static string? CheckReference(JsonElement schema, JsonElement instance, string path, McpSchemaDocument root, int depth, int chain, ref int budget, McpSchemaEvaluated local)
        {
            if (schema.TryGetProperty("$ref", out JsonElement reference) && reference.ValueKind == JsonValueKind.String)
            {
                // An unresolvable reference fails validation instead of being treated as permissive.
                if (!root.TryResolve(reference.GetString(), out JsonElement target, out McpSchemaResource? resource))
                {
                    return $"{path} cannot be validated: the schema reference '{reference.GetString()}' cannot be resolved.";
                }

                string? error = EvaluateIn(resource, target, instance, path, root, depth + 1, chain + 1, ref budget, local);
                if (error != null) return error;
            }

            if (!root.IsDraft07 && schema.TryGetProperty("$dynamicRef", out JsonElement dynamicReference) && dynamicReference.ValueKind == JsonValueKind.String)
            {
                if (!root.TryResolveDynamic(dynamicReference.GetString(), out JsonElement target, out McpSchemaResource? resource))
                {
                    return $"{path} cannot be validated: the schema reference '{dynamicReference.GetString()}' cannot be resolved.";
                }

                string? error = EvaluateIn(resource, target, instance, path, root, depth + 1, chain + 1, ref budget, local);
                if (error != null) return error;
            }

            return null;
        }

        // Evaluates a reference target within the resource it belongs to, so its own references resolve there.
        private static string? EvaluateIn(McpSchemaResource? resource, JsonElement target, JsonElement instance, string path, McpSchemaDocument root, int depth, int chain, ref int budget, McpSchemaEvaluated local)
        {
            if (resource == null) return Evaluate(target, instance, path, root, depth, chain, ref budget, local);
            root.Enter(resource);
            try
            {
                return Evaluate(target, instance, path, root, depth, chain, ref budget, local);
            }
            finally
            {
                root.Exit();
            }
        }

        private static string? CheckType(JsonElement schema, JsonElement instance, string path)
        {
            if (!schema.TryGetProperty("type", out JsonElement type))
            {
                return null;
            }

            List<string> names = ReadTypeNames(type);
            if (names.Count == 0)
            {
                return null;
            }

            if (names.Any(name => TypeMatches(name, instance)))
            {
                return null;
            }

            return $"{path} must be a JSON {String.Join(" or ", names)}.";
        }

        private static string? CheckEnumAndConst(JsonElement schema, JsonElement instance, string path)
        {
            if (schema.TryGetProperty("enum", out JsonElement allowed) && allowed.ValueKind == JsonValueKind.Array)
            {
                bool found = false;
                foreach (JsonElement candidate in allowed.EnumerateArray())
                {
                    if (JsonEquals(candidate, instance, 0))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return $"{path} must be one of the allowed values: {Describe(allowed)}.";
                }
            }

            if (schema.TryGetProperty("const", out JsonElement constant) && !JsonEquals(constant, instance, 0))
            {
                return $"{path} must be exactly {Describe(constant)}.";
            }

            return null;
        }

        private static string? CheckObject(JsonElement schema, JsonElement instance, string path, McpSchemaDocument root, int depth, int chain, ref int budget, McpSchemaEvaluated local)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            int memberCount = 0;
            foreach (JsonProperty member in instance.EnumerateObject())
            {
                names.Add(member.Name);
                memberCount++;
            }

            if (schema.TryGetProperty("required", out JsonElement required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement requiredName in required.EnumerateArray())
                {
                    if (requiredName.ValueKind != JsonValueKind.String) continue;
                    string? name = requiredName.GetString();
                    if (String.IsNullOrEmpty(name)) continue;

                    if (!names.Contains(name))
                    {
                        return $"{path} is missing required property '{name}'.";
                    }
                }
            }

            if (TryGetCount(schema, "minProperties", out long minProperties) && memberCount < minProperties)
            {
                return $"{path} must have at least {minProperties} {Plural(minProperties, "property", "properties")}.";
            }

            if (TryGetCount(schema, "maxProperties", out long maxProperties) && memberCount > maxProperties)
            {
                return $"{path} must have at most {maxProperties} {Plural(maxProperties, "property", "properties")}.";
            }

            if (!root.IsDraft07 && schema.TryGetProperty("dependentRequired", out JsonElement dependentRequired) && dependentRequired.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty dependency in dependentRequired.EnumerateObject())
                {
                    if (!names.Contains(dependency.Name) || dependency.Value.ValueKind != JsonValueKind.Array) continue;

                    foreach (JsonElement dependent in dependency.Value.EnumerateArray())
                    {
                        if (dependent.ValueKind != JsonValueKind.String) continue;
                        string? dependentName = dependent.GetString();
                        if (!String.IsNullOrEmpty(dependentName) && !names.Contains(dependentName))
                        {
                            return $"{path} has property '{dependency.Name}', which requires property '{dependentName}'.";
                        }
                    }
                }
            }

            if (!root.IsDraft07 && schema.TryGetProperty("dependentSchemas", out JsonElement dependentSchemas) && dependentSchemas.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty dependency in dependentSchemas.EnumerateObject())
                {
                    if (!names.Contains(dependency.Name)) continue;

                    string? error = Evaluate(dependency.Value, instance, path, root, depth + 1, chain + 1, ref budget, local);
                    if (error != null) return error;
                }
            }

            // draft-07 dependencies: an array lists required properties, a schema applies to the whole object.
            if (root.IsDraft07 && schema.TryGetProperty("dependencies", out JsonElement dependencies) && dependencies.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty dependency in dependencies.EnumerateObject())
                {
                    if (!names.Contains(dependency.Name)) continue;
                    if (dependency.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement dependent in dependency.Value.EnumerateArray())
                        {
                            if (dependent.ValueKind != JsonValueKind.String) continue;
                            string? dependentName = dependent.GetString();
                            if (!String.IsNullOrEmpty(dependentName) && !names.Contains(dependentName))
                            {
                                return $"{path} has property '{dependency.Name}', which requires property '{dependentName}'.";
                            }
                        }
                    }
                    else if (IsSchema(dependency.Value))
                    {
                        string? error = Evaluate(dependency.Value, instance, path, root, depth + 1, chain + 1, ref budget, local);
                        if (error != null) return error;
                    }
                }
            }

            bool hasProperties = schema.TryGetProperty("properties", out JsonElement properties) && properties.ValueKind == JsonValueKind.Object;
            bool hasPatterns = schema.TryGetProperty("patternProperties", out JsonElement patterns) && patterns.ValueKind == JsonValueKind.Object;
            bool hasAdditional = schema.TryGetProperty("additionalProperties", out JsonElement additional)
                && (additional.ValueKind == JsonValueKind.Object || additional.ValueKind == JsonValueKind.False || additional.ValueKind == JsonValueKind.True);
            bool hasPropertyNames = schema.TryGetProperty("propertyNames", out JsonElement propertyNames)
                && (propertyNames.ValueKind == JsonValueKind.Object || propertyNames.ValueKind == JsonValueKind.False || propertyNames.ValueKind == JsonValueKind.True);

            if (!hasProperties && !hasPatterns && !hasAdditional && !hasPropertyNames)
            {
                return null;
            }

            foreach (JsonProperty member in instance.EnumerateObject())
            {
                string memberPath = $"{path}.{member.Name}";

                if (hasPropertyNames)
                {
                    JsonElement nameElement = JsonSerializer.SerializeToElement(member.Name);
                    string? nameError = Evaluate(propertyNames, nameElement, $"{path} property name '{member.Name}'", root, depth + 1, 0, ref budget);
                    if (nameError != null) return nameError;
                }

                bool declared = false;
                if (hasProperties && properties.TryGetProperty(member.Name, out JsonElement propertySchema))
                {
                    declared = true;
                    local.Properties.Add(member.Name);
                    string? error = Evaluate(propertySchema, member.Value, memberPath, root, depth + 1, 0, ref budget);
                    if (error != null) return error;
                }

                bool matchedPattern = false;
                if (hasPatterns)
                {
                    foreach (JsonProperty pattern in patterns.EnumerateObject())
                    {
                        bool? matched = MatchPattern(pattern.Name, member.Name, out bool timedOut);
                        if (timedOut)
                        {
                            return $"{memberPath} could not be checked against the property name pattern '{pattern.Name}' in time.";
                        }

                        if (matched != true) continue;

                        matchedPattern = true;
                        local.Properties.Add(member.Name);
                        string? error = Evaluate(pattern.Value, member.Value, memberPath, root, depth + 1, 0, ref budget);
                        if (error != null) return error;
                    }
                }

                if (declared || matchedPattern || !hasAdditional)
                {
                    continue;
                }

                local.Properties.Add(member.Name);

                if (additional.ValueKind == JsonValueKind.False)
                {
                    return $"{path} has unexpected property '{member.Name}'; the schema does not allow additional properties.";
                }

                if (additional.ValueKind == JsonValueKind.Object)
                {
                    string? error = Evaluate(additional, member.Value, memberPath, root, depth + 1, 0, ref budget);
                    if (error != null) return error;
                }
            }

            return null;
        }

        private static string? CheckArray(JsonElement schema, JsonElement instance, string path, McpSchemaDocument root, int depth, ref int budget, McpSchemaEvaluated local)
        {
            int length = instance.GetArrayLength();

            if (TryGetCount(schema, "minItems", out long minItems) && length < minItems)
            {
                return $"{path} must contain at least {minItems} {Plural(minItems, "item", "items")}.";
            }

            if (TryGetCount(schema, "maxItems", out long maxItems) && length > maxItems)
            {
                return $"{path} must contain at most {maxItems} {Plural(maxItems, "item", "items")}.";
            }

            if (schema.TryGetProperty("uniqueItems", out JsonElement unique) && unique.ValueKind == JsonValueKind.True && length > 1)
            {
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                int index = 0;
                foreach (JsonElement item in instance.EnumerateArray())
                {
                    StringBuilder canonical = new StringBuilder();
                    AppendCanonical(canonical, item, 0);
                    if (!seen.Add(canonical.ToString()))
                    {
                        return $"{path} must not contain duplicate items; {path}[{index}] repeats an earlier item.";
                    }

                    index++;
                }
            }

            // Positional schemas: prefixItems (2020-12), or the array form of items with additionalItems (draft-07).
            JsonElement positional = default;
            bool hasPositional = false;
            JsonElement rest = default;
            bool hasRest = false;

            if (!root.IsDraft07 && schema.TryGetProperty("prefixItems", out JsonElement prefixItems) && prefixItems.ValueKind == JsonValueKind.Array)
            {
                positional = prefixItems;
                hasPositional = true;
            }

            if (schema.TryGetProperty("items", out JsonElement items))
            {
                if (items.ValueKind == JsonValueKind.Array)
                {
                    if (root.IsDraft07 && !hasPositional)
                    {
                        positional = items;
                        hasPositional = true;
                        if (schema.TryGetProperty("additionalItems", out JsonElement additionalItems) && IsSchema(additionalItems))
                        {
                            rest = additionalItems;
                            hasRest = true;
                        }
                    }
                }
                else if (IsSchema(items))
                {
                    rest = items;
                    hasRest = true;
                }
            }

            int positionalCount = hasPositional ? positional.GetArrayLength() : 0;

            if (hasPositional || hasRest)
            {
                int index = 0;
                foreach (JsonElement item in instance.EnumerateArray())
                {
                    string? error = null;
                    if (index < positionalCount)
                    {
                        local.Items.Add(index);
                        error = Evaluate(positional[index], item, $"{path}[{index}]", root, depth + 1, 0, ref budget);
                    }
                    else if (hasRest)
                    {
                        local.Items.Add(index);
                        if (rest.ValueKind == JsonValueKind.False)
                        {
                            return $"{path} must contain at most {positionalCount} {Plural(positionalCount, "item", "items")}.";
                        }

                        error = Evaluate(rest, item, $"{path}[{index}]", root, depth + 1, 0, ref budget);
                    }

                    if (error != null) return error;
                    index++;
                }
            }

            if (schema.TryGetProperty("contains", out JsonElement contains) && IsSchema(contains))
            {
                // minContains and maxContains are 2019-09/2020-12 keywords.
                long minContains = 1;
                if (!root.IsDraft07 && TryGetCount(schema, "minContains", out long configuredMin)) minContains = configuredMin;
                long maxContains = 0;
                bool hasMaxContains = !root.IsDraft07 && TryGetCount(schema, "maxContains", out maxContains);

                long matches = 0;
                int index = 0;
                foreach (JsonElement item in instance.EnumerateArray())
                {
                    if (Evaluate(contains, item, $"{path}[{index}]", root, depth + 1, 0, ref budget) == null)
                    {
                        matches++;
                        local.Items.Add(index);
                    }

                    index++;
                }

                if (matches < minContains)
                {
                    return $"{path} must contain at least {minContains} {Plural(minContains, "item", "items")} matching the 'contains' schema.";
                }

                if (hasMaxContains && matches > maxContains)
                {
                    return $"{path} must contain at most {maxContains} {Plural(maxContains, "item", "items")} matching the 'contains' schema.";
                }
            }

            return null;
        }

        private static string? CheckString(JsonElement schema, JsonElement instance, string path)
        {
            string text = instance.GetString() ?? String.Empty;
            bool hasMin = TryGetCount(schema, "minLength", out long minLength);
            bool hasMax = TryGetCount(schema, "maxLength", out long maxLength);

            if (hasMin || hasMax)
            {
                long length = CountCodePoints(text);
                if (hasMin && length < minLength)
                {
                    return $"{path} must be at least {minLength} {Plural(minLength, "character", "characters")} long.";
                }

                if (hasMax && length > maxLength)
                {
                    return $"{path} must be at most {maxLength} {Plural(maxLength, "character", "characters")} long.";
                }
            }

            if (schema.TryGetProperty("pattern", out JsonElement pattern) && pattern.ValueKind == JsonValueKind.String)
            {
                string patternText = pattern.GetString() ?? String.Empty;
                bool? matched = MatchPattern(patternText, text, out bool timedOut);
                if (timedOut)
                {
                    return $"{path} could not be checked against the pattern '{patternText}' in time.";
                }

                if (matched == false)
                {
                    return $"{path} must match the pattern '{patternText}'.";
                }
            }

            return null;
        }

        private static string? CheckNumber(JsonElement schema, JsonElement instance, string path)
        {
            // exclusiveMinimum and exclusiveMaximum are numbers in draft-07 and 2020-12 (the boolean form is draft-04).
            schema.TryGetProperty("exclusiveMinimum", out JsonElement exclusiveMinimum);
            schema.TryGetProperty("exclusiveMaximum", out JsonElement exclusiveMaximum);

            if (schema.TryGetProperty("minimum", out JsonElement minimum) && minimum.ValueKind == JsonValueKind.Number)
            {
                int? comparison = CompareNumbers(instance, minimum);
                if (comparison < 0)
                {
                    return $"{path} must be greater than or equal to {minimum.GetRawText()}.";
                }
            }

            if (schema.TryGetProperty("maximum", out JsonElement maximum) && maximum.ValueKind == JsonValueKind.Number)
            {
                int? comparison = CompareNumbers(instance, maximum);
                if (comparison > 0)
                {
                    return $"{path} must be less than or equal to {maximum.GetRawText()}.";
                }
            }

            if (exclusiveMinimum.ValueKind == JsonValueKind.Number)
            {
                int? comparison = CompareNumbers(instance, exclusiveMinimum);
                if (comparison <= 0)
                {
                    return $"{path} must be greater than {exclusiveMinimum.GetRawText()}.";
                }
            }

            if (exclusiveMaximum.ValueKind == JsonValueKind.Number)
            {
                int? comparison = CompareNumbers(instance, exclusiveMaximum);
                if (comparison >= 0)
                {
                    return $"{path} must be less than {exclusiveMaximum.GetRawText()}.";
                }
            }

            if (schema.TryGetProperty("multipleOf", out JsonElement multipleOf) && multipleOf.ValueKind == JsonValueKind.Number)
            {
                bool? isMultiple = IsMultipleOf(instance, multipleOf);
                if (isMultiple == false)
                {
                    return $"{path} must be a multiple of {multipleOf.GetRawText()}.";
                }
            }

            return null;
        }

        private static string? CheckComposition(JsonElement schema, JsonElement instance, string path, McpSchemaDocument root, int depth, int chain, ref int budget, McpSchemaEvaluated local)
        {
            if (schema.TryGetProperty("allOf", out JsonElement allOf) && allOf.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement subschema in allOf.EnumerateArray())
                {
                    string? error = Evaluate(subschema, instance, path, root, depth + 1, chain + 1, ref budget, local);
                    if (error != null) return error;
                }
            }

            if (schema.TryGetProperty("anyOf", out JsonElement anyOf) && anyOf.ValueKind == JsonValueKind.Array && anyOf.GetArrayLength() > 0)
            {
                string? firstError = null;
                bool matched = false;
                // Every passing branch contributes annotations, so all branches are evaluated.
                foreach (JsonElement subschema in anyOf.EnumerateArray())
                {
                    McpSchemaEvaluated branch = new McpSchemaEvaluated();
                    string? error = Evaluate(subschema, instance, path, root, depth + 1, chain + 1, ref budget, branch);
                    if (error == null)
                    {
                        matched = true;
                        local.Merge(branch);
                        continue;
                    }

                    firstError ??= error;
                }

                if (!matched)
                {
                    return $"{path} must match at least one of the 'anyOf' schemas; first mismatch: {firstError}";
                }
            }

            if (schema.TryGetProperty("oneOf", out JsonElement oneOf) && oneOf.ValueKind == JsonValueKind.Array && oneOf.GetArrayLength() > 0)
            {
                string? firstError = null;
                int matches = 0;
                McpSchemaEvaluated? passing = null;
                foreach (JsonElement subschema in oneOf.EnumerateArray())
                {
                    McpSchemaEvaluated branch = new McpSchemaEvaluated();
                    string? error = Evaluate(subschema, instance, path, root, depth + 1, chain + 1, ref budget, branch);
                    if (error == null)
                    {
                        matches++;
                        passing = branch;
                        if (matches > 1) break;
                    }
                    else
                    {
                        firstError ??= error;
                    }
                }

                if (matches == 0)
                {
                    return $"{path} must match exactly one of the 'oneOf' schemas but matches none; first mismatch: {firstError}";
                }

                if (matches > 1)
                {
                    return $"{path} must match exactly one of the 'oneOf' schemas but matches more than one.";
                }

                if (passing != null) local.Merge(passing);
            }

            if (schema.TryGetProperty("not", out JsonElement not) && IsSchema(not))
            {
                if (Evaluate(not, instance, path, root, depth + 1, chain + 1, ref budget) == null)
                {
                    return $"{path} must not match the 'not' schema.";
                }
            }

            if (schema.TryGetProperty("if", out JsonElement condition) && IsSchema(condition))
            {
                McpSchemaEvaluated conditionAnnotations = new McpSchemaEvaluated();
                bool conditionHolds = Evaluate(condition, instance, path, root, depth + 1, chain + 1, ref budget, conditionAnnotations) == null;
                if (conditionHolds) local.Merge(conditionAnnotations);
                string branchName = conditionHolds ? "then" : "else";
                if (schema.TryGetProperty(branchName, out JsonElement branch) && IsSchema(branch))
                {
                    string? error = Evaluate(branch, instance, path, root, depth + 1, chain + 1, ref budget, local);
                    if (error != null) return error;
                }
            }

            return null;
        }

        #endregion

        #region Helpers

        private static bool IsSchema(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Object || element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False;
        }

        private static List<string> ReadTypeNames(JsonElement type)
        {
            List<string> names = new List<string>();
            if (type.ValueKind == JsonValueKind.String)
            {
                string? name = type.GetString();
                if (name != null && _KnownTypes.Contains(name, StringComparer.Ordinal)) names.Add(name);
            }
            else if (type.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in type.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String) continue;
                    string? name = entry.GetString();
                    if (name != null && _KnownTypes.Contains(name, StringComparer.Ordinal) && !names.Contains(name)) names.Add(name);
                }
            }

            return names;
        }

        private static bool TypeMatches(string type, JsonElement instance)
        {
            return type switch
            {
                "object" => instance.ValueKind == JsonValueKind.Object,
                "array" => instance.ValueKind == JsonValueKind.Array,
                "string" => instance.ValueKind == JsonValueKind.String,
                "number" => instance.ValueKind == JsonValueKind.Number,
                "integer" => instance.ValueKind == JsonValueKind.Number && IsIntegral(instance.GetRawText()),
                "boolean" => instance.ValueKind == JsonValueKind.True || instance.ValueKind == JsonValueKind.False,
                "null" => instance.ValueKind == JsonValueKind.Null,
                _ => true
            };
        }

        // Exact test on the JSON number text: the value is digits x 10^(exponent - fractionDigits), which is an
        // integer when every non-zero digit lands left of the decimal point (so 1.0 and 1e2 are integers, 1.5 is not).
        private static bool IsIntegral(string raw)
        {
            int position = 0;
            if (position < raw.Length && raw[position] == '-') position++;

            StringBuilder digits = new StringBuilder();
            while (position < raw.Length && Char.IsAsciiDigit(raw[position]))
            {
                digits.Append(raw[position]);
                position++;
            }

            int fractionDigits = 0;
            if (position < raw.Length && raw[position] == '.')
            {
                position++;
                while (position < raw.Length && Char.IsAsciiDigit(raw[position]))
                {
                    digits.Append(raw[position]);
                    fractionDigits++;
                    position++;
                }
            }

            long exponent = 0;
            if (position < raw.Length && (raw[position] == 'e' || raw[position] == 'E'))
            {
                position++;
                bool negative = false;
                if (position < raw.Length && (raw[position] == '+' || raw[position] == '-'))
                {
                    negative = raw[position] == '-';
                    position++;
                }

                while (position < raw.Length && Char.IsAsciiDigit(raw[position]))
                {
                    if (exponent < 1000000000L) exponent = (exponent * 10) + (raw[position] - '0');
                    position++;
                }

                if (negative) exponent = -exponent;
            }

            int trailingZeros = 0;
            for (int index = digits.Length - 1; index >= 0 && digits[index] == '0'; index--)
            {
                trailingZeros++;
            }

            if (trailingZeros == digits.Length)
            {
                // The value is zero.
                return true;
            }

            return exponent - fractionDigits + trailingZeros >= 0;
        }

        private static int? CompareNumbers(JsonElement left, JsonElement right)
        {
            if (left.TryGetDecimal(out decimal leftDecimal) && right.TryGetDecimal(out decimal rightDecimal))
            {
                return Decimal.Compare(leftDecimal, rightDecimal);
            }

            if (left.TryGetDouble(out double leftDouble) && right.TryGetDouble(out double rightDouble))
            {
                return leftDouble.CompareTo(rightDouble);
            }

            return null;
        }

        private static bool? IsMultipleOf(JsonElement value, JsonElement divisor)
        {
            if (value.TryGetDecimal(out decimal valueDecimal) && divisor.TryGetDecimal(out decimal divisorDecimal))
            {
                if (divisorDecimal <= 0m) return null;
                return valueDecimal % divisorDecimal == 0m;
            }

            if (value.TryGetDouble(out double valueDouble) && divisor.TryGetDouble(out double divisorDouble))
            {
                if (divisorDouble <= 0 || Double.IsNaN(divisorDouble)) return null;
                double quotient = valueDouble / divisorDouble;
                if (Double.IsInfinity(quotient) || Double.IsNaN(quotient)) return null;
                return Math.Abs(quotient - Math.Round(quotient)) <= 1e-9 * Math.Max(1.0, Math.Abs(quotient));
            }

            return null;
        }

        // Reads a non-negative integral keyword value (such as minLength); a malformed value is ignored.
        private static bool TryGetCount(JsonElement schema, string keyword, out long count)
        {
            count = 0;
            if (!schema.TryGetProperty(keyword, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            if (value.TryGetInt64(out long whole))
            {
                if (whole < 0) return false;
                count = whole;
                return true;
            }

            if (value.TryGetDouble(out double real) && real >= 0 && Math.Floor(real) == real)
            {
                count = real >= Int64.MaxValue ? Int64.MaxValue : (long)real;
                return true;
            }

            return false;
        }

        // JSON Schema measures string length in Unicode code points, so a surrogate pair counts once.
        private static long CountCodePoints(string text)
        {
            long count = 0;
            for (int index = 0; index < text.Length; index++)
            {
                if (Char.IsHighSurrogate(text[index]) && index + 1 < text.Length && Char.IsLowSurrogate(text[index + 1]))
                {
                    index++;
                }

                count++;
            }

            return count;
        }

        private static string Plural(long count, string singular, string plural)
        {
            return count == 1 ? singular : plural;
        }

        private static string Describe(JsonElement value)
        {
            string raw = value.GetRawText();
            return raw.Length <= _MaximumMessageValueLength ? raw : raw.Substring(0, _MaximumMessageValueLength) + "...";
        }

        // Returns true or false for a match, or null when the pattern is not a valid regular expression (the keyword
        // is then ignored). timedOut reports a match that exceeded the time limit, which callers treat as a failure.
        private static bool? MatchPattern(string pattern, string input, out bool timedOut)
        {
            timedOut = false;
            Regex? regex = GetRegex(pattern);
            if (regex == null)
            {
                return null;
            }

            try
            {
                return regex.IsMatch(input);
            }
            catch (RegexMatchTimeoutException)
            {
                timedOut = true;
                return null;
            }
        }

        private static Regex? GetRegex(string pattern)
        {
            if (_RegexCache.TryGetValue(pattern, out Regex? cached))
            {
                return cached;
            }

            Regex? regex = null;
            try
            {
                // JSON Schema patterns are ECMA-262 regular expressions: ECMAScript mode gives \d, \w, and \s their
                // ASCII meaning. Matching is bounded by the match timeout.
                regex = new Regex(pattern, RegexOptions.ECMAScript, _RegexTimeout);
            }
            catch (ArgumentException)
            {
                // A construct .NET's ECMAScript mode does not accept (such as \p{...} Unicode property escapes, which
                // ECMA-262 allows with the u flag): the .NET engine runs it, linear-time where possible.
                try
                {
                    regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, _RegexTimeout);
                }
                catch (Exception nonBacktrackingError) when (nonBacktrackingError is NotSupportedException || nonBacktrackingError is ArgumentException)
                {
                    try
                    {
                        regex = new Regex(pattern, RegexOptions.CultureInvariant, _RegexTimeout);
                    }
                    catch (ArgumentException)
                    {
                        // Not a valid regular expression: the keyword that uses it is ignored.
                        regex = null;
                    }
                }
            }

            if (_RegexCache.Count >= _MaximumCachedPatterns)
            {
                _RegexCache.Clear();
            }

            _RegexCache[pattern] = regex;
            return regex;
        }

        // JSON equality for enum and const: numbers compare by value (1 equals 1.0), objects ignore member order.
        private static bool JsonEquals(JsonElement left, JsonElement right, int depth)
        {
            if (depth > _MaximumNestingDepth)
            {
                return false;
            }

            if (left.ValueKind != right.ValueKind)
            {
                return false;
            }

            switch (left.ValueKind)
            {
                case JsonValueKind.Number:
                    int? comparison = CompareNumbers(left, right);
                    return comparison.HasValue ? comparison.Value == 0 : String.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
                case JsonValueKind.String:
                    return String.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);
                case JsonValueKind.Array:
                    if (left.GetArrayLength() != right.GetArrayLength()) return false;
                    using (JsonElement.ArrayEnumerator leftItems = left.EnumerateArray())
                    using (JsonElement.ArrayEnumerator rightItems = right.EnumerateArray())
                    {
                        while (leftItems.MoveNext() && rightItems.MoveNext())
                        {
                            if (!JsonEquals(leftItems.Current, rightItems.Current, depth + 1)) return false;
                        }
                    }

                    return true;
                case JsonValueKind.Object:
                    int leftCount = 0;
                    foreach (JsonProperty member in left.EnumerateObject())
                    {
                        leftCount++;
                        if (!right.TryGetProperty(member.Name, out JsonElement other) || !JsonEquals(member.Value, other, depth + 1)) return false;
                    }

                    return leftCount == right.EnumerateObject().Count();
                default:
                    // true, false, and null are equal to themselves.
                    return true;
            }
        }

        // Builds a canonical text form so uniqueItems runs in linear time: sorted member names, numbers by value.
        private static void AppendCanonical(StringBuilder builder, JsonElement value, int depth)
        {
            if (depth > _MaximumNestingDepth)
            {
                builder.Append(value.GetRawText());
                return;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    builder.Append('{');
                    foreach (JsonProperty member in value.EnumerateObject().OrderBy(member => member.Name, StringComparer.Ordinal))
                    {
                        builder.Append(member.Name.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(member.Name).Append('=');
                        AppendCanonical(builder, member.Value, depth + 1);
                        builder.Append(',');
                    }

                    builder.Append('}');
                    break;
                case JsonValueKind.Array:
                    builder.Append('[');
                    foreach (JsonElement item in value.EnumerateArray())
                    {
                        AppendCanonical(builder, item, depth + 1);
                        builder.Append(',');
                    }

                    builder.Append(']');
                    break;
                case JsonValueKind.String:
                    string text = value.GetString() ?? String.Empty;
                    builder.Append('s').Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text);
                    break;
                case JsonValueKind.Number:
                    builder.Append('n').Append(CanonicalNumber(value));
                    break;
                case JsonValueKind.True:
                    builder.Append('t');
                    break;
                case JsonValueKind.False:
                    builder.Append('f');
                    break;
                default:
                    builder.Append('z');
                    break;
            }
        }

        private static string CanonicalNumber(JsonElement value)
        {
            if (value.TryGetDecimal(out decimal number))
            {
                string text = number.ToString(CultureInfo.InvariantCulture);
                if (text.Contains('.', StringComparison.Ordinal))
                {
                    text = text.TrimEnd('0').TrimEnd('.');
                }

                return text == "-0" ? "0" : text;
            }

            if (value.TryGetDouble(out double real))
            {
                return real.ToString("R", CultureInfo.InvariantCulture);
            }

            return value.GetRawText();
        }

        #endregion
    }
}
