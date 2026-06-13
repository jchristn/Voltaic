namespace Voltaic
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;

    internal sealed class McpEndpoint
    {
        private const int DefaultPageSize = 100;

        private readonly object _Lock = new object();
        private readonly List<ToolRegistration> _Tools = new List<ToolRegistration>();
        private readonly List<ResourceRegistration> _Resources = new List<ResourceRegistration>();
        private readonly List<ResourceTemplateRegistration> _ResourceTemplates = new List<ResourceTemplateRegistration>();
        private readonly List<PromptRegistration> _Prompts = new List<PromptRegistration>();
        private readonly List<CompletionRegistration> _CompletionProviders = new List<CompletionRegistration>();
        private readonly HashSet<string> _ResourceSubscriptions = new HashSet<string>(StringComparer.Ordinal);

        public string ProtocolVersion { get; set; } = McpProtocol.LatestProtocolVersion;

        public string ServerName { get; set; }

        public string ServerVersion { get; set; } = "1.0.0";

        public bool SupportsListChangedNotifications { get; set; } = true;

        public bool SupportsResourceSubscriptions { get; set; } = true;

        public bool SupportsLogging { get; set; } = true;

        public bool EnforceInitializationOrdering { get; set; }

        public McpSessionLifecycleState State { get; private set; } = McpSessionLifecycleState.Created;

        public string MinimumLogLevel { get; private set; } = "info";

        public int PageSize { get; set; } = DefaultPageSize;

        public McpEndpoint(string serverName)
        {
            ServerName = serverName;
        }

        public object Initialize(JsonElement? args)
        {
            string clientProtocolVersion = ProtocolVersion;
            if (args.HasValue && args.Value.TryGetProperty("protocolVersion", out JsonElement protocolVersionProp))
            {
                try
                {
                    clientProtocolVersion = McpProtocol.NegotiateVersion(protocolVersionProp.GetString() ?? ProtocolVersion);
                }
                catch (ArgumentException)
                {
                    string requested = protocolVersionProp.GetString() ?? string.Empty;
                    throw McpProtocolException.UnsupportedVersion(requested);
                }
            }

            State = McpSessionLifecycleState.Initializing;

            return new
            {
                protocolVersion = clientProtocolVersion,
                capabilities = BuildCapabilities(),
                serverInfo = new McpImplementation
                {
                    Name = ServerName,
                    Version = ServerVersion
                }
            };
        }

        public object Initialized(JsonElement? args)
        {
            State = McpSessionLifecycleState.Initialized;
            return new { };
        }

        public object Ping(JsonElement? args)
        {
            return new { };
        }

        public ToolDefinition RegisterTool(ToolDefinition definition, Func<JsonElement?, CancellationToken, Task<object>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ValidateToolDefinition(definition);

            lock (_Lock)
            {
                _Tools.RemoveAll(tool => StringComparer.Ordinal.Equals(tool.Definition.Name, definition.Name));
                _Tools.Add(new ToolRegistration(definition, handler));
            }

            return definition;
        }

        public McpResource RegisterResource(McpResource resource, Func<string, CancellationToken, Task<McpReadResourceResult>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ValidateResource(resource);

            lock (_Lock)
            {
                _Resources.RemoveAll(existing => StringComparer.Ordinal.Equals(existing.Resource.Uri, resource.Uri));
                _Resources.Add(new ResourceRegistration(resource, handler));
            }

            return resource;
        }

        public McpResourceTemplate RegisterResourceTemplate(
            McpResourceTemplate template,
            Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<McpReadResourceResult>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ValidateResourceTemplate(template);

            lock (_Lock)
            {
                _ResourceTemplates.RemoveAll(existing => StringComparer.Ordinal.Equals(existing.Template.UriTemplate, template.UriTemplate));
                _ResourceTemplates.Add(new ResourceTemplateRegistration(template, CreateTemplateRegex(template.UriTemplate), handler));
            }

            return template;
        }

        public McpPrompt RegisterPrompt(McpPrompt prompt, Func<JsonElement?, CancellationToken, Task<McpGetPromptResult>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ValidatePrompt(prompt);

            lock (_Lock)
            {
                _Prompts.RemoveAll(existing => StringComparer.Ordinal.Equals(existing.Prompt.Name, prompt.Name));
                _Prompts.Add(new PromptRegistration(prompt, handler));
            }

            return prompt;
        }

        public void RegisterCompletionProvider(
            string referenceType,
            string? referenceId,
            string? argumentName,
            Func<McpCompleteRequest, CancellationToken, Task<McpCompleteResult>> handler)
        {
            if (String.IsNullOrWhiteSpace(referenceType)) throw new ArgumentNullException(nameof(referenceType));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            lock (_Lock)
            {
                _CompletionProviders.RemoveAll(existing =>
                    StringComparer.Ordinal.Equals(existing.ReferenceType, referenceType) &&
                    StringComparer.Ordinal.Equals(existing.ReferenceId, referenceId) &&
                    StringComparer.Ordinal.Equals(existing.ArgumentName, argumentName));
                _CompletionProviders.Add(new CompletionRegistration(referenceType, referenceId, argumentName, handler));
            }
        }

        public McpListToolsResult ListTools(JsonElement? args)
        {
            List<ToolDefinition> tools;
            lock (_Lock)
            {
                tools = _Tools.Select(tool => tool.Definition).ToList();
            }

            Page<ToolDefinition> page = PageItems(tools, GetCursor(args));
            return new McpListToolsResult
            {
                Tools = page.Items,
                NextCursor = page.NextCursor
            };
        }

        public async Task<object> CallToolAsync(JsonElement? args, CancellationToken token)
        {
            if (!args.HasValue)
            {
                throw McpProtocolException.InvalidParams("tools/call requires params with a name.");
            }

            if (!args.Value.TryGetProperty("name", out JsonElement nameElement))
            {
                throw McpProtocolException.InvalidParams("tools/call requires a name parameter.");
            }

            string? toolName = nameElement.GetString();
            if (String.IsNullOrWhiteSpace(toolName))
            {
                throw McpProtocolException.InvalidParams("tools/call name must be a non-empty string.");
            }

            JsonElement? toolArguments = null;
            if (args.Value.TryGetProperty("arguments", out JsonElement argsElement))
            {
                toolArguments = argsElement;
            }

            ToolRegistration? tool;
            lock (_Lock)
            {
                tool = _Tools.FirstOrDefault(existing => StringComparer.Ordinal.Equals(existing.Definition.Name, toolName));
            }

            if (tool == null)
            {
                throw McpProtocolException.InvalidParams($"Tool '{toolName}' was not found.");
            }

            ValidateJsonSchema(tool.Definition.InputSchema, toolArguments, $"Tool '{toolName}' arguments");
            object result = await tool.Handler(toolArguments, token).ConfigureAwait(false);

            if (result is McpToolCallResult toolCallResult)
            {
                if (tool.Definition.OutputSchema != null && toolCallResult.StructuredContent != null)
                {
                    ValidateJsonSchema(tool.Definition.OutputSchema, SerializeToElement(toolCallResult.StructuredContent), $"Tool '{toolName}' structured output");
                }

                return toolCallResult;
            }

            if (tool.Definition.OutputSchema != null)
            {
                ValidateJsonSchema(tool.Definition.OutputSchema, SerializeToElement(result), $"Tool '{toolName}' output");
                return McpToolCallResult.FromStructured(result);
            }

            if (result is string text)
            {
                return McpToolCallResult.FromText(text);
            }

            return McpToolCallResult.FromText(JsonSerializer.Serialize(result));
        }

        public McpListResourcesResult ListResources(JsonElement? args)
        {
            List<McpResource> resources;
            lock (_Lock)
            {
                resources = _Resources.Select(resource => resource.Resource).ToList();
            }

            Page<McpResource> page = PageItems(resources, GetCursor(args));
            return new McpListResourcesResult
            {
                Resources = page.Items,
                NextCursor = page.NextCursor
            };
        }

        public McpListResourceTemplatesResult ListResourceTemplates(JsonElement? args)
        {
            List<McpResourceTemplate> templates;
            lock (_Lock)
            {
                templates = _ResourceTemplates.Select(template => template.Template).ToList();
            }

            Page<McpResourceTemplate> page = PageItems(templates, GetCursor(args));
            return new McpListResourceTemplatesResult
            {
                ResourceTemplates = page.Items,
                NextCursor = page.NextCursor
            };
        }

        public async Task<object> ReadResourceAsync(JsonElement? args, CancellationToken token)
        {
            if (!args.HasValue || !args.Value.TryGetProperty("uri", out JsonElement uriElement))
            {
                throw McpProtocolException.InvalidParams("resources/read requires a uri parameter.");
            }

            string? uri = uriElement.GetString();
            if (String.IsNullOrWhiteSpace(uri))
            {
                throw McpProtocolException.InvalidParams("resources/read uri must be a non-empty string.");
            }

            ResourceRegistration? resource;
            List<ResourceTemplateRegistration> templates;
            lock (_Lock)
            {
                resource = _Resources.FirstOrDefault(existing => StringComparer.Ordinal.Equals(existing.Resource.Uri, uri));
                templates = _ResourceTemplates.ToList();
            }

            if (resource != null)
            {
                return await resource.Handler(uri, token).ConfigureAwait(false);
            }

            foreach (ResourceTemplateRegistration template in templates)
            {
                Match match = template.Pattern.Match(uri);
                if (match.Success)
                {
                    Dictionary<string, string> variables = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (string groupName in template.Pattern.GetGroupNames())
                    {
                        if (!Int32.TryParse(groupName, out _) && match.Groups[groupName].Success)
                        {
                            variables[groupName] = Uri.UnescapeDataString(match.Groups[groupName].Value);
                        }
                    }

                    return await template.Handler(uri, variables, token).ConfigureAwait(false);
                }
            }

            throw McpProtocolException.InvalidParams($"Resource '{uri}' was not found.");
        }

        public McpListPromptsResult ListPrompts(JsonElement? args)
        {
            List<McpPrompt> prompts;
            lock (_Lock)
            {
                prompts = _Prompts.Select(prompt => prompt.Prompt).ToList();
            }

            Page<McpPrompt> page = PageItems(prompts, GetCursor(args));
            return new McpListPromptsResult
            {
                Prompts = page.Items,
                NextCursor = page.NextCursor
            };
        }

        public async Task<object> GetPromptAsync(JsonElement? args, CancellationToken token)
        {
            if (!args.HasValue || !args.Value.TryGetProperty("name", out JsonElement nameElement))
            {
                throw McpProtocolException.InvalidParams("prompts/get requires a name parameter.");
            }

            string? promptName = nameElement.GetString();
            if (String.IsNullOrWhiteSpace(promptName))
            {
                throw McpProtocolException.InvalidParams("prompts/get name must be a non-empty string.");
            }

            JsonElement? promptArguments = null;
            if (args.Value.TryGetProperty("arguments", out JsonElement argumentsElement))
            {
                promptArguments = argumentsElement;
            }

            PromptRegistration? prompt;
            lock (_Lock)
            {
                prompt = _Prompts.FirstOrDefault(existing => StringComparer.Ordinal.Equals(existing.Prompt.Name, promptName));
            }

            if (prompt == null)
            {
                throw McpProtocolException.InvalidParams($"Prompt '{promptName}' was not found.");
            }

            ValidateRequiredPromptArguments(prompt.Prompt, promptArguments);
            return await prompt.Handler(promptArguments, token).ConfigureAwait(false);
        }

        public async Task<object> CompleteAsync(JsonElement? args, CancellationToken token)
        {
            if (!args.HasValue)
            {
                throw McpProtocolException.InvalidParams("completion/complete requires params.");
            }

            McpCompleteRequest? request = JsonSerializer.Deserialize<McpCompleteRequest>(args.Value.GetRawText());
            if (request == null)
            {
                throw McpProtocolException.InvalidParams("completion/complete params were invalid.");
            }

            ValidateCompletionRequest(request);

            string? referenceId = request.Ref.Type == "ref/prompt" ? request.Ref.Name : request.Ref.Uri;
            CompletionRegistration? provider;
            lock (_Lock)
            {
                provider = _CompletionProviders.FirstOrDefault(existing =>
                    StringComparer.Ordinal.Equals(existing.ReferenceType, request.Ref.Type) &&
                    StringComparer.Ordinal.Equals(existing.ReferenceId, referenceId) &&
                    StringComparer.Ordinal.Equals(existing.ArgumentName, request.Argument.Name))
                    ?? _CompletionProviders.FirstOrDefault(existing =>
                        StringComparer.Ordinal.Equals(existing.ReferenceType, request.Ref.Type) &&
                        StringComparer.Ordinal.Equals(existing.ReferenceId, referenceId) &&
                        existing.ArgumentName == null)
                    ?? _CompletionProviders.FirstOrDefault(existing =>
                        StringComparer.Ordinal.Equals(existing.ReferenceType, request.Ref.Type) &&
                        existing.ReferenceId == null &&
                        existing.ArgumentName == null);
            }

            if (provider == null)
            {
                return new McpCompleteResult();
            }

            McpCompleteResult result = await provider.Handler(request, token).ConfigureAwait(false);
            result.Completion.Values = result.Completion.Values.Take(100).ToList();
            if (result.Completion.Total == null)
            {
                result.Completion.Total = result.Completion.Values.Count;
            }

            if (result.Completion.HasMore == null)
            {
                result.Completion.HasMore = result.Completion.Total > result.Completion.Values.Count;
            }

            return result;
        }

        public object SubscribeResource(JsonElement? args)
        {
            string uri = GetRequiredUri(args, "resources/subscribe");
            lock (_Lock)
            {
                _ResourceSubscriptions.Add(uri);
            }

            return new { };
        }

        public object UnsubscribeResource(JsonElement? args)
        {
            string uri = GetRequiredUri(args, "resources/unsubscribe");
            lock (_Lock)
            {
                _ResourceSubscriptions.Remove(uri);
            }

            return new { };
        }

        public object SetLogLevel(JsonElement? args)
        {
            if (!args.HasValue || !args.Value.TryGetProperty("level", out JsonElement levelElement))
            {
                throw McpProtocolException.InvalidParams("logging/setLevel requires a level parameter.");
            }

            string? level = levelElement.GetString();
            if (!IsValidLogLevel(level))
            {
                throw McpProtocolException.InvalidParams($"Invalid log level '{level}'.");
            }

            MinimumLogLevel = level!;
            return new { };
        }

        public object Cancelled(JsonElement? args)
        {
            if (!args.HasValue)
            {
                return new { };
            }

            if (!args.Value.TryGetProperty("requestId", out JsonElement requestId) ||
                requestId.ValueKind == JsonValueKind.Null ||
                requestId.ValueKind == JsonValueKind.Undefined)
            {
                return new { };
            }

            return new { };
        }

        public void Clear()
        {
            lock (_Lock)
            {
                _Tools.Clear();
                _Resources.Clear();
                _ResourceTemplates.Clear();
                _Prompts.Clear();
                _CompletionProviders.Clear();
                _ResourceSubscriptions.Clear();
            }

            State = McpSessionLifecycleState.Closed;
        }

        private object BuildCapabilities()
        {
            bool hasTools;
            bool hasResources;
            bool hasPrompts;
            bool hasCompletions;

            lock (_Lock)
            {
                hasTools = _Tools.Count > 0;
                hasResources = _Resources.Count > 0 || _ResourceTemplates.Count > 0;
                hasPrompts = _Prompts.Count > 0;
                hasCompletions = _CompletionProviders.Count > 0;
            }

            return new McpServerCapabilities
            {
                Tools = hasTools ? new McpListChangedCapability { ListChanged = SupportsListChangedNotifications } : null,
                Resources = hasResources ? new McpResourceCapability { ListChanged = SupportsListChangedNotifications, Subscribe = SupportsResourceSubscriptions } : null,
                Prompts = hasPrompts ? new McpListChangedCapability { ListChanged = SupportsListChangedNotifications } : null,
                Completions = hasCompletions ? new { } : null,
                Logging = SupportsLogging ? new { } : null
            };
        }

        private static void ValidateToolDefinition(ToolDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (String.IsNullOrWhiteSpace(definition.Name)) throw new ArgumentException("Tool definition must include a name.", nameof(definition));
            if (String.IsNullOrWhiteSpace(definition.Description)) throw new ArgumentException("Tool definition must include a description.", nameof(definition));
            if (definition.InputSchema == null) throw new ArgumentException("Tool definition must include an input schema.", nameof(definition));
        }

        private static void ValidateResource(McpResource resource)
        {
            if (resource == null) throw new ArgumentNullException(nameof(resource));
            if (String.IsNullOrWhiteSpace(resource.Uri)) throw new ArgumentException("Resource must include a URI.", nameof(resource));
            if (String.IsNullOrWhiteSpace(resource.Name)) throw new ArgumentException("Resource must include a name.", nameof(resource));
        }

        private static void ValidateResourceTemplate(McpResourceTemplate template)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            if (String.IsNullOrWhiteSpace(template.UriTemplate)) throw new ArgumentException("Resource template must include a URI template.", nameof(template));
            if (String.IsNullOrWhiteSpace(template.Name)) throw new ArgumentException("Resource template must include a name.", nameof(template));
        }

        private static void ValidatePrompt(McpPrompt prompt)
        {
            if (prompt == null) throw new ArgumentNullException(nameof(prompt));
            if (String.IsNullOrWhiteSpace(prompt.Name)) throw new ArgumentException("Prompt must include a name.", nameof(prompt));
        }

        private static void ValidateRequiredPromptArguments(McpPrompt prompt, JsonElement? arguments)
        {
            if (prompt.Arguments == null)
            {
                return;
            }

            foreach (McpPromptArgument argument in prompt.Arguments.Where(argument => argument.Required == true))
            {
                if (!arguments.HasValue || !arguments.Value.TryGetProperty(argument.Name, out _))
                {
                    throw McpProtocolException.InvalidParams($"Prompt '{prompt.Name}' requires argument '{argument.Name}'.");
                }
            }
        }

        private Page<T> PageItems<T>(List<T> items, string? cursor)
        {
            int offset = 0;
            if (!String.IsNullOrEmpty(cursor) && (!Int32.TryParse(cursor, out offset) || offset < 0))
            {
                throw McpProtocolException.InvalidParams($"Invalid cursor '{cursor}'.");
            }

            int pageSize = Math.Max(1, PageSize);
            List<T> pageItems = items.Skip(offset).Take(pageSize).ToList();
            int nextOffset = offset + pageItems.Count;
            return new Page<T>(pageItems, nextOffset < items.Count ? nextOffset.ToString() : null);
        }

        private static string? GetCursor(JsonElement? args)
        {
            if (args.HasValue && args.Value.TryGetProperty("cursor", out JsonElement cursorElement))
            {
                return cursorElement.GetString();
            }

            return null;
        }

        private static string GetRequiredUri(JsonElement? args, string method)
        {
            if (!args.HasValue || !args.Value.TryGetProperty("uri", out JsonElement uriElement))
            {
                throw McpProtocolException.InvalidParams($"{method} requires a uri parameter.");
            }

            string? uri = uriElement.GetString();
            if (String.IsNullOrWhiteSpace(uri))
            {
                throw McpProtocolException.InvalidParams($"{method} uri must be a non-empty string.");
            }

            return uri;
        }

        private static void ValidateCompletionRequest(McpCompleteRequest request)
        {
            if (String.IsNullOrWhiteSpace(request.Ref.Type))
            {
                throw McpProtocolException.InvalidParams("completion/complete ref.type is required.");
            }

            if (request.Ref.Type != "ref/prompt" && request.Ref.Type != "ref/resource")
            {
                throw McpProtocolException.InvalidParams($"Unsupported completion reference type '{request.Ref.Type}'.");
            }

            if (request.Ref.Type == "ref/prompt" && String.IsNullOrWhiteSpace(request.Ref.Name))
            {
                throw McpProtocolException.InvalidParams("Prompt completion references require ref.name.");
            }

            if (request.Ref.Type == "ref/resource" && String.IsNullOrWhiteSpace(request.Ref.Uri))
            {
                throw McpProtocolException.InvalidParams("Resource completion references require ref.uri.");
            }

            if (String.IsNullOrWhiteSpace(request.Argument.Name))
            {
                throw McpProtocolException.InvalidParams("completion/complete argument.name is required.");
            }
        }

        private static bool IsValidLogLevel(string? level)
        {
            return level == "debug" ||
                level == "info" ||
                level == "notice" ||
                level == "warning" ||
                level == "error" ||
                level == "critical" ||
                level == "alert" ||
                level == "emergency";
        }

        private static void ValidateJsonSchema(object schema, JsonElement? value, string context)
        {
            JsonElement schemaElement = SerializeToElement(schema);
            ValidateJsonSchema(schemaElement, value, context);
        }

        private static JsonElement SerializeToElement(object? value)
        {
            return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value));
        }

        private static void ValidateJsonSchema(JsonElement schema, JsonElement? value, string context)
        {
            if (schema.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (schema.TryGetProperty("required", out JsonElement requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement requiredItem in requiredElement.EnumerateArray())
                {
                    string? requiredName = requiredItem.GetString();
                    if (!String.IsNullOrEmpty(requiredName) &&
                        (!value.HasValue || value.Value.ValueKind != JsonValueKind.Object || !value.Value.TryGetProperty(requiredName, out _)))
                    {
                        throw McpProtocolException.ValidationError($"{context} is missing required property '{requiredName}'.");
                    }
                }
            }

            if (!schema.TryGetProperty("type", out JsonElement typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            string? expectedType = typeElement.GetString();
            if (String.IsNullOrEmpty(expectedType))
            {
                return;
            }

            if (!value.HasValue)
            {
                if (expectedType == "object")
                {
                    return;
                }

                throw McpProtocolException.ValidationError($"{context} is required.");
            }

            if (!JsonTypeMatches(expectedType, value.Value))
            {
                throw McpProtocolException.ValidationError($"{context} must be a JSON {expectedType}.");
            }

            if (expectedType == "object" &&
                value.Value.ValueKind == JsonValueKind.Object &&
                schema.TryGetProperty("properties", out JsonElement propertiesElement) &&
                propertiesElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in propertiesElement.EnumerateObject())
                {
                    if (value.Value.TryGetProperty(property.Name, out JsonElement propertyValue))
                    {
                        ValidateJsonSchema(property.Value, propertyValue, $"{context}.{property.Name}");
                    }
                }
            }
        }

        private static bool JsonTypeMatches(string expectedType, JsonElement value)
        {
            return expectedType switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "boolean" => value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False,
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => true
            };
        }

        private static Regex CreateTemplateRegex(string uriTemplate)
        {
            StringBuilder pattern = new StringBuilder();
            for (int i = 0; i < uriTemplate.Length; i++)
            {
                if (uriTemplate[i] == '{')
                {
                    int end = uriTemplate.IndexOf('}', i + 1);
                    if (end <= i + 1)
                    {
                        throw new ArgumentException("Resource template variables must use {name} syntax.", nameof(uriTemplate));
                    }

                    string variableName = uriTemplate.Substring(i + 1, end - i - 1);
                    if (!Regex.IsMatch(variableName, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
                    {
                        throw new ArgumentException($"Invalid resource template variable '{variableName}'.", nameof(uriTemplate));
                    }

                    pattern.Append("(?<").Append(variableName).Append(">[^/?#]+)");
                    i = end;
                    continue;
                }

                pattern.Append(Regex.Escape(uriTemplate[i].ToString()));
            }

            return new Regex("^" + pattern + "$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        }

        private sealed record ToolRegistration(ToolDefinition Definition, Func<JsonElement?, CancellationToken, Task<object>> Handler);

        private sealed record ResourceRegistration(McpResource Resource, Func<string, CancellationToken, Task<McpReadResourceResult>> Handler);

        private sealed record ResourceTemplateRegistration(
            McpResourceTemplate Template,
            Regex Pattern,
            Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<McpReadResourceResult>> Handler);

        private sealed record PromptRegistration(McpPrompt Prompt, Func<JsonElement?, CancellationToken, Task<McpGetPromptResult>> Handler);

        private sealed record CompletionRegistration(
            string ReferenceType,
            string? ReferenceId,
            string? ArgumentName,
            Func<McpCompleteRequest, CancellationToken, Task<McpCompleteResult>> Handler);

        private sealed record Page<T>(List<T> Items, string? NextCursor);
    }
}
