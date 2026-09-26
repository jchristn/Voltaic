namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.Json.Nodes;

    /// <summary>
    /// Downgrades server-produced MCP JSON (results, server notifications, and server capabilities) so it
    /// contains only what a handshake-era protocol revision (<c>2024-11-05</c> through <c>2025-11-25</c>) defines.
    /// Fields added in later revisions are removed and content block types that did not yet exist are converted
    /// to <c>text</c> blocks. Fields that exist in the target revision are never removed.
    /// </summary>
    /// <remarks>
    /// All methods mutate the supplied <see cref="JsonNode"/> in place. They are no-ops for a null node, a node
    /// that is not a JSON object, an unknown protocol version, and the stateless <c>2026-07-28</c> revision.
    /// For <c>2025-11-25</c> only the stateless-era additions (<c>resultType</c>, <c>ttlMs</c>, <c>cacheScope</c>,
    /// and the <c>extensions</c> capability) are removed, because the <c>2025-11-25</c> schema does not define them.
    /// Thread safety: the class is stateless and safe to call concurrently, but callers must not mutate the same
    /// node from multiple threads at once.
    /// </remarks>
    internal static class McpVersionCompatibility
    {
        // Ordinals for the recognized revisions; 0 means unknown.
        private const int _Unknown = 0;
        private const int _Rev20241105 = 1;
        private const int _Rev20250326 = 2;
        private const int _Rev20250618 = 3;
        private const int _Rev20251125 = 4;
        private const int _Rev20260728 = 5;

        /// <summary>
        /// Removes from a method result everything the given protocol revision does not define, and converts
        /// content blocks that did not exist in that revision.
        /// </summary>
        /// <param name="result">The JSON-RPC <c>result</c> object. Null or non-object values are ignored.</param>
        /// <param name="method">The request method the result answers (for example <c>tools/call</c>). Null or unknown methods only receive the top-level stateless-field cleanup.</param>
        /// <param name="protocolVersion">The negotiated protocol version. Unknown versions and <c>2026-07-28</c> make this a no-op.</param>
        internal static void DowngradeResult(JsonNode? result, string method, string protocolVersion)
        {
            int revision = GetRevision(protocolVersion);
            if (!IsHandshakeRevision(revision)) return;
            if (result is not JsonObject obj) return;

            // Stateless-era (2026-07-28) result fields never exist in a handshake-era revision.
            obj.Remove("resultType");
            obj.Remove("ttlMs");
            obj.Remove("cacheScope");

            switch (method)
            {
                case "initialize":
                    DowngradeImplementation(obj["serverInfo"], revision);
                    DowngradeCapabilities(obj["capabilities"], protocolVersion);
                    break;

                case "tools/list":
                    ForEachObject(obj["tools"], tool => DowngradeTool(tool, revision));
                    break;

                case "tools/call":
                    DowngradeCallToolResult(obj, revision);
                    break;

                case "resources/list":
                    ForEachObject(obj["resources"], resource => DowngradeResource(resource, revision));
                    break;

                case "resources/templates/list":
                    ForEachObject(obj["resourceTemplates"], template => DowngradeResource(template, revision));
                    break;

                case "resources/read":
                    ForEachObject(obj["contents"], contents => DowngradeResourceContents(contents, revision));
                    break;

                case "prompts/list":
                    ForEachObject(obj["prompts"], prompt => DowngradePrompt(prompt, revision));
                    break;

                case "prompts/get":
                    ForEachObject(obj["messages"], message => DowngradePromptMessage(message, revision));
                    break;

                default:
                    // completion/complete, ping, logging/setLevel, resources/subscribe, and unknown methods
                    // carry no revision-specific fields beyond the top-level cleanup above.
                    break;
            }
        }

        /// <summary>
        /// Removes from a server-to-client notification the parameters the given protocol revision does not define.
        /// </summary>
        /// <param name="notification">The full JSON-RPC notification object (with <c>method</c> and <c>params</c>). Null or non-object values are ignored.</param>
        /// <param name="protocolVersion">The negotiated protocol version. Unknown versions and <c>2026-07-28</c> make this a no-op.</param>
        internal static void DowngradeNotification(JsonNode? notification, string protocolVersion)
        {
            int revision = GetRevision(protocolVersion);
            if (!IsHandshakeRevision(revision)) return;
            if (notification is not JsonObject obj) return;

            string? method = GetString(obj, "method");
            if (obj["params"] is not JsonObject parameters) return;

            if (method == "notifications/progress" && revision < _Rev20250326)
            {
                // ProgressNotification.params.message was added in 2025-03-26.
                parameters.Remove("message");
            }

            // notifications/message, notifications/resources/updated, and the list_changed notifications have
            // the same parameters (plus the always-permitted params._meta) in every handshake-era revision.
        }

        /// <summary>
        /// Indicates whether a server-to-client notification method exists in the given protocol revision.
        /// Callers can use this to suppress notifications an older client cannot understand.
        /// </summary>
        /// <param name="method">The notification method. Null returns false.</param>
        /// <param name="protocolVersion">The negotiated protocol version. Unknown versions and <c>2026-07-28</c> return true (no filtering).</param>
        /// <returns>False only when the method is known to be undefined in a handshake-era revision.</returns>
        internal static bool IsServerNotificationDefined(string? method, string protocolVersion)
        {
            if (method == null) return false;

            int revision = GetRevision(protocolVersion);
            if (!IsHandshakeRevision(revision)) return true;

            switch (method)
            {
                case "notifications/tasks/status":
                case "notifications/elicitation/complete":
                    // Both were added in 2025-11-25.
                    return revision >= _Rev20251125;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Removes from a <c>ServerCapabilities</c> object the capabilities the given protocol revision does not define.
        /// </summary>
        /// <param name="capabilities">The capabilities object. Null or non-object values are ignored.</param>
        /// <param name="protocolVersion">The negotiated protocol version. Unknown versions and <c>2026-07-28</c> make this a no-op.</param>
        internal static void DowngradeCapabilities(JsonNode? capabilities, string protocolVersion)
        {
            int revision = GetRevision(protocolVersion);
            if (!IsHandshakeRevision(revision)) return;
            if (capabilities is not JsonObject obj) return;

            // completions was added in 2025-03-26.
            if (revision < _Rev20250326) obj.Remove("completions");

            // tasks was added in 2025-11-25.
            if (revision < _Rev20251125) obj.Remove("tasks");

            // extensions exists only in the stateless era (2026-07-28).
            obj.Remove("extensions");
        }

        private static int GetRevision(string? protocolVersion)
        {
            switch (protocolVersion)
            {
                case McpProtocol.ProtocolVersion20241105: return _Rev20241105;
                case McpProtocol.ProtocolVersion20250326: return _Rev20250326;
                case McpProtocol.ProtocolVersion20250618: return _Rev20250618;
                case McpProtocol.ProtocolVersion20251125: return _Rev20251125;
                case McpProtocol.ProtocolVersion20260728: return _Rev20260728;
                default: return _Unknown;
            }
        }

        private static bool IsHandshakeRevision(int revision)
        {
            return revision >= _Rev20241105 && revision <= _Rev20251125;
        }

        private static void ForEachObject(JsonNode? array, Action<JsonObject> action)
        {
            if (array is not JsonArray items) return;
            foreach (JsonNode? item in items)
            {
                if (item is JsonObject obj) action(obj);
            }
        }

        private static string? GetString(JsonObject obj, string propertyName)
        {
            if (obj[propertyName] is JsonValue value && value.TryGetValue<string>(out string? text)) return text;
            return null;
        }

        private static void DowngradeImplementation(JsonNode? node, int revision)
        {
            if (node is not JsonObject implementation) return;

            // BaseMetadata.title was added in 2025-06-18.
            if (revision < _Rev20250618) implementation.Remove("title");

            // description, websiteUrl, and icons were added in 2025-11-25.
            if (revision < _Rev20251125)
            {
                implementation.Remove("description");
                implementation.Remove("websiteUrl");
                implementation.Remove("icons");
            }
        }

        private static void DowngradeTool(JsonObject tool, int revision)
        {
            if (revision < _Rev20250618)
            {
                // title, outputSchema, and _meta were added in 2025-06-18. In 2025-03-26 the display title can
                // still travel as ToolAnnotations.title, so it is moved there rather than lost.
                string? title = GetString(tool, "title");
                if (revision >= _Rev20250326 && !String.IsNullOrEmpty(title))
                {
                    if (tool["annotations"] is not JsonObject annotations)
                    {
                        annotations = new JsonObject();
                        tool["annotations"] = annotations;
                    }

                    if (!annotations.ContainsKey("title")) annotations["title"] = title;
                }

                tool.Remove("title");
                tool.Remove("outputSchema");
                tool.Remove("_meta");
            }

            // Before 2026-07-28 an outputSchema must be an object schema; one of another type is omitted.
            if (tool["outputSchema"] is JsonObject outputSchema && outputSchema["type"] is JsonValue outputType
                && (!outputType.TryGetValue(out string? typeName) || typeName != "object"))
            {
                tool.Remove("outputSchema");
            }

            // ToolAnnotations was added in 2025-03-26.
            if (revision < _Rev20250326) tool.Remove("annotations");

            // icons and execution were added in 2025-11-25.
            if (revision < _Rev20251125)
            {
                tool.Remove("icons");
                tool.Remove("execution");
            }

            // inputSchema is a JSON Schema document and is deliberately left untouched.
        }

        private static void DowngradeCallToolResult(JsonObject result, int revision)
        {
            JsonArray? content = result["content"] as JsonArray;
            bool hadTextBlock = content != null
                && content.OfType<JsonObject>().Any(block => GetString(block, "type") == "text");

            if (content != null) DowngradeContentArray(content, revision);

            // structuredContent was added in 2025-06-18, and until 2026-07-28 it must be a JSON object. Keep the data
            // reachable by making sure a text block carries it (FromStructured already adds one; handler-built results
            // may not).
            bool undefined = revision < _Rev20250618;
            bool notAnObject = result.ContainsKey("structuredContent") && result["structuredContent"] is not JsonObject;
            if (result.ContainsKey("structuredContent") && (undefined || notAnObject))
            {
                JsonNode? structured = result["structuredContent"];
                result.Remove("structuredContent");

                if (!hadTextBlock && structured != null)
                {
                    if (content == null)
                    {
                        content = new JsonArray();
                        result["content"] = content;
                    }

                    content.Add(CreateTextBlock(structured.ToJsonString(), null, revision));
                }
            }
        }

        private static void DowngradeContentArray(JsonArray content, int revision)
        {
            for (int i = 0; i < content.Count; i++)
            {
                if (content[i] is not JsonObject block) continue;
                JsonObject replacement = DowngradeContentBlock(block, revision);
                if (!ReferenceEquals(replacement, block)) content[i] = replacement;
            }
        }

        private static void DowngradePromptMessage(JsonObject message, int revision)
        {
            if (message["content"] is not JsonObject block) return;
            JsonObject replacement = DowngradeContentBlock(block, revision);
            if (!ReferenceEquals(replacement, block)) message["content"] = replacement;
        }

        // Returns the same block (mutated) or a new text block that replaces a type the revision lacks.
        private static JsonObject DowngradeContentBlock(JsonObject block, int revision)
        {
            string? type = GetString(block, "type");

            if (type == "audio" && revision < _Rev20250326)
            {
                // AudioContent was added in 2025-03-26.
                string mimeType = GetString(block, "mimeType") ?? "unknown";
                return CreateTextBlock("[audio content omitted: " + mimeType + "]", block["annotations"], revision);
            }

            if (type == "resource_link" && revision < _Rev20250618)
            {
                // ResourceLink was added in 2025-06-18. An EmbeddedResource would need the resource body, which
                // a link does not carry, so a descriptive text block is the faithful replacement.
                return CreateTextBlock(DescribeResourceLink(block), block["annotations"], revision);
            }

            if (type == "resource_link")
            {
                // ResourceLink extends Resource, so it follows the Resource rules (icons before 2025-11-25).
                DowngradeResource(block, revision);
                return block;
            }

            if (revision < _Rev20250618) block.Remove("_meta");
            DowngradeAnnotations(block["annotations"], revision);

            if (type == "resource") DowngradeResourceContents(block["resource"] as JsonObject, revision);

            return block;
        }

        private static JsonObject CreateTextBlock(string text, JsonNode? annotations, int revision)
        {
            JsonObject textBlock = new JsonObject
            {
                ["type"] = "text",
                ["text"] = text
            };

            // TextContent carries annotations in every handshake-era revision (Annotated in 2024-11-05).
            if (annotations is JsonObject source)
            {
                JsonObject copy = (JsonObject)source.DeepClone();
                DowngradeAnnotations(copy, revision);
                textBlock["annotations"] = copy;
            }

            return textBlock;
        }

        private static string DescribeResourceLink(JsonObject link)
        {
            string uri = GetString(link, "uri") ?? string.Empty;
            string? name = GetString(link, "title") ?? GetString(link, "name");
            string? mimeType = GetString(link, "mimeType");
            string? description = GetString(link, "description");

            StringBuilder builder = new StringBuilder("[resource link: ");
            if (!String.IsNullOrEmpty(name)) builder.Append(name).Append(' ');
            builder.Append('<').Append(uri).Append('>');
            if (!String.IsNullOrEmpty(mimeType)) builder.Append(" (").Append(mimeType).Append(')');
            if (!String.IsNullOrEmpty(description)) builder.Append(" - ").Append(description);
            builder.Append(']');
            return builder.ToString();
        }

        private static void DowngradeAnnotations(JsonNode? node, int revision)
        {
            if (node is not JsonObject annotations) return;

            // Annotations.lastModified was added in 2025-06-18; audience and priority exist in every revision.
            if (revision < _Rev20250618) annotations.Remove("lastModified");
        }

        // Applies to Resource, ResourceTemplate, and ResourceLink.
        private static void DowngradeResource(JsonObject resource, int revision)
        {
            if (revision < _Rev20250618)
            {
                // title and _meta were added in 2025-06-18.
                resource.Remove("title");
                resource.Remove("_meta");
            }

            // icons were added in 2025-11-25.
            if (revision < _Rev20251125) resource.Remove("icons");

            DowngradeAnnotations(resource["annotations"], revision);
        }

        // Applies to TextResourceContents and BlobResourceContents.
        private static void DowngradeResourceContents(JsonObject? contents, int revision)
        {
            if (contents == null) return;

            // ResourceContents._meta was added in 2025-06-18.
            if (revision < _Rev20250618) contents.Remove("_meta");
        }

        private static void DowngradePrompt(JsonObject prompt, int revision)
        {
            if (revision < _Rev20250618)
            {
                // Prompt.title, Prompt._meta, and PromptArgument.title were added in 2025-06-18.
                prompt.Remove("title");
                prompt.Remove("_meta");
                ForEachObject(prompt["arguments"], argument => argument.Remove("title"));
            }

            // icons were added in 2025-11-25.
            if (revision < _Rev20251125) prompt.Remove("icons");
        }
    }
}
