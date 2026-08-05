namespace Voltaic.Mcp
{
    using Voltaic.Core;
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

        public string? ServerInstructions { get; set; }

        public long? ListCacheTtlMs { get; set; }

        public string? ListCacheScope { get; set; }

        public bool AdvertiseTasksExtension { get; set; }

        public long? DiscoverCacheTtlMs { get; set; }

        public string? DiscoverCacheScope { get; set; }

        public McpSessionLifecycleState State { get; private set; } = McpSessionLifecycleState.Created;

        public string MinimumLogLevel { get; private set; } = "info";

        public int PageSize { get; set; } = DefaultPageSize;

        public McpEndpoint(string serverName)
        {
            ServerName = serverName;
        }

        public object Initialize(RpcParameters? args)
        {
            string clientProtocolVersion = ProtocolVersion;
            McpInitializeParams? initialize = args?.Deserialize<McpInitializeParams>();
            if (initialize != null && !String.IsNullOrEmpty(initialize.ProtocolVersion))
            {
                try
                {
                    clientProtocolVersion = McpProtocol.NegotiateVersion(initialize.ProtocolVersion);
                }
                catch (ArgumentException)
                {
                    throw McpProtocolException.UnsupportedVersion(initialize.ProtocolVersion!);
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

        public object Initialized(RpcParameters? args)
        {
            State = McpSessionLifecycleState.Initialized;
            return new { };
        }

        public McpDiscoverResult Discover(RpcParameters? args)
        {
            McpServerCapabilities capabilities = BuildCapabilities();

            McpDiscoverResult result = new McpDiscoverResult
            {
                SupportedVersions = McpProtocol.SupportedVersionStrings().ToList(),
                Capabilities = capabilities,
                Instructions = ServerInstructions,
                TtlMs = DiscoverCacheTtlMs,
                CacheScope = DiscoverCacheScope,
                Meta = new Dictionary<string, object?>
                {
                    {
                        McpProtocol.MetaServerInfoKey,
                        new McpImplementation { Name = ServerName, Version = ServerVersion }
                    }
                }
            };

            return result;
        }

        public object Ping(RpcParameters? args)
        {
            return new { };
        }

        public ToolDefinition RegisterTool(ToolDefinition definition, Func<RpcParameters?, CancellationToken, Task<object>> handler)
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

        public McpPrompt RegisterPrompt(McpPrompt prompt, Func<RpcParameters?, CancellationToken, Task<McpGetPromptResult>> handler)
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

        public McpListToolsResult ListTools(RpcParameters? args)
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
                NextCursor = page.NextCursor,
                TtlMs = ListCacheTtlMs,
                CacheScope = ListCacheScope
            };
        }

        public async Task<object> CallToolAsync(RpcParameters? args, CancellationToken token)
        {
            if (args == null || !args.HasValue)
            {
                throw McpProtocolException.InvalidParams("tools/call requires params with a name.");
            }

            McpNamedArgumentsParams? call = args.Deserialize<McpNamedArgumentsParams>();
            if (call == null || call.Name == null)
            {
                throw McpProtocolException.InvalidParams("tools/call requires a name parameter.");
            }

            string toolName = call.Name;
            if (String.IsNullOrWhiteSpace(toolName))
            {
                throw McpProtocolException.InvalidParams("tools/call name must be a non-empty string.");
            }

            RpcParameters? toolArguments = call.Arguments == null ? null : RpcParameters.FromObject(call.Arguments);

            ToolRegistration? tool;
            lock (_Lock)
            {
                tool = _Tools.FirstOrDefault(existing => StringComparer.Ordinal.Equals(existing.Definition.Name, toolName));
            }

            if (tool == null)
            {
                throw McpProtocolException.InvalidParams($"Tool '{toolName}' was not found.");
            }

            McpSchemaValidator.Validate(tool.Definition.InputSchema, toolArguments?.RawJson, $"Tool '{toolName}' arguments");
            object result = await tool.Handler(toolArguments, token).ConfigureAwait(false);

            // Multi Round-Trip Requests: a handler may return an input-required result to ask the
            // client for more information. It flows through unchanged rather than being wrapped as
            // a tool result.
            if (result is McpInputRequiredResult inputRequired)
            {
                return inputRequired;
            }

            if (result is McpToolCallResult toolCallResult)
            {
                if (tool.Definition.OutputSchema != null && toolCallResult.StructuredContent != null)
                {
                    McpSchemaValidator.Validate(tool.Definition.OutputSchema, JsonSerializer.Serialize(toolCallResult.StructuredContent), $"Tool '{toolName}' structured output");
                }

                return toolCallResult;
            }

            if (tool.Definition.OutputSchema != null)
            {
                McpSchemaValidator.Validate(tool.Definition.OutputSchema, JsonSerializer.Serialize(result), $"Tool '{toolName}' output");
                return McpToolCallResult.FromStructured(result);
            }

            if (result is string text)
            {
                return McpToolCallResult.FromText(text);
            }

            return McpToolCallResult.FromText(JsonSerializer.Serialize(result));
        }

        public McpListResourcesResult ListResources(RpcParameters? args)
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
                NextCursor = page.NextCursor,
                TtlMs = ListCacheTtlMs,
                CacheScope = ListCacheScope
            };
        }

        public McpListResourceTemplatesResult ListResourceTemplates(RpcParameters? args)
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
                NextCursor = page.NextCursor,
                TtlMs = ListCacheTtlMs,
                CacheScope = ListCacheScope
            };
        }

        public async Task<object> ReadResourceAsync(RpcParameters? args, CancellationToken token)
        {
            McpUriParams? parameters = args?.Deserialize<McpUriParams>();
            if (parameters == null || parameters.Uri == null)
            {
                throw McpProtocolException.InvalidParams("resources/read requires a uri parameter.");
            }

            if (String.IsNullOrWhiteSpace(parameters.Uri))
            {
                throw McpProtocolException.InvalidParams("resources/read uri must be a non-empty string.");
            }

            string uri = parameters.Uri;

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

        public McpListPromptsResult ListPrompts(RpcParameters? args)
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
                NextCursor = page.NextCursor,
                TtlMs = ListCacheTtlMs,
                CacheScope = ListCacheScope
            };
        }

        public async Task<object> GetPromptAsync(RpcParameters? args, CancellationToken token)
        {
            McpNamedArgumentsParams? request = args?.Deserialize<McpNamedArgumentsParams>();
            if (request == null || request.Name == null)
            {
                throw McpProtocolException.InvalidParams("prompts/get requires a name parameter.");
            }

            string promptName = request.Name;
            if (String.IsNullOrWhiteSpace(promptName))
            {
                throw McpProtocolException.InvalidParams("prompts/get name must be a non-empty string.");
            }

            RpcParameters? promptArguments = request.Arguments == null ? null : RpcParameters.FromObject(request.Arguments);

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

        public async Task<object> CompleteAsync(RpcParameters? args, CancellationToken token)
        {
            if (args == null || !args.HasValue)
            {
                throw McpProtocolException.InvalidParams("completion/complete requires params.");
            }

            McpCompleteRequest? request = args.Deserialize<McpCompleteRequest>();
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

        public object SubscribeResource(RpcParameters? args)
        {
            string uri = GetRequiredUri(args, "resources/subscribe");
            lock (_Lock)
            {
                _ResourceSubscriptions.Add(uri);
            }

            return new { };
        }

        public object UnsubscribeResource(RpcParameters? args)
        {
            string uri = GetRequiredUri(args, "resources/unsubscribe");
            lock (_Lock)
            {
                _ResourceSubscriptions.Remove(uri);
            }

            return new { };
        }

        public object SetLogLevel(RpcParameters? args)
        {
            McpLevelParams? parameters = args?.Deserialize<McpLevelParams>();
            if (parameters == null || parameters.Level == null)
            {
                throw McpProtocolException.InvalidParams("logging/setLevel requires a level parameter.");
            }

            string level = parameters.Level;
            if (!IsValidLogLevel(level))
            {
                throw McpProtocolException.InvalidParams($"Invalid log level '{level}'.");
            }

            MinimumLogLevel = level;
            return new { };
        }

        public object Cancelled(RpcParameters? args)
        {
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

        private McpServerCapabilities BuildCapabilities()
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

            McpServerCapabilities capabilities = new McpServerCapabilities
            {
                Tools = hasTools ? new McpListChangedCapability { ListChanged = SupportsListChangedNotifications } : null,
                Resources = hasResources ? new McpResourceCapability { ListChanged = SupportsListChangedNotifications, Subscribe = SupportsResourceSubscriptions } : null,
                Prompts = hasPrompts ? new McpListChangedCapability { ListChanged = SupportsListChangedNotifications } : null,
                Completions = hasCompletions ? new { } : null,
                Logging = SupportsLogging ? new { } : null
            };

            if (AdvertiseTasksExtension)
            {
                capabilities.Extensions = new Dictionary<string, object>
                {
                    { McpProtocol.TasksExtensionId, new { } }
                };
            }

            return capabilities;
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

        private static void ValidateRequiredPromptArguments(McpPrompt prompt, RpcParameters? arguments)
        {
            if (prompt.Arguments == null)
            {
                return;
            }

            JsonValueInfo provided = JsonValueInfo.Parse(arguments?.RawJson);
            foreach (McpPromptArgument argument in prompt.Arguments.Where(argument => argument.Required == true))
            {
                if (provided.Kind != McpJsonValueKind.Object || provided.Members == null || !provided.Members.ContainsKey(argument.Name))
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

        private static string? GetCursor(RpcParameters? args)
        {
            McpCursorParams? parameters = args?.Deserialize<McpCursorParams>();
            return parameters?.Cursor;
        }

        private static string GetRequiredUri(RpcParameters? args, string method)
        {
            McpUriParams? parameters = args?.Deserialize<McpUriParams>();
            if (parameters == null || parameters.Uri == null)
            {
                throw McpProtocolException.InvalidParams($"{method} requires a uri parameter.");
            }

            if (String.IsNullOrWhiteSpace(parameters.Uri))
            {
                throw McpProtocolException.InvalidParams($"{method} uri must be a non-empty string.");
            }

            return parameters.Uri;
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

        private sealed record ToolRegistration(ToolDefinition Definition, Func<RpcParameters?, CancellationToken, Task<object>> Handler);

        private sealed record ResourceRegistration(McpResource Resource, Func<string, CancellationToken, Task<McpReadResourceResult>> Handler);

        private sealed record ResourceTemplateRegistration(
            McpResourceTemplate Template,
            Regex Pattern,
            Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<McpReadResourceResult>> Handler);

        private sealed record PromptRegistration(McpPrompt Prompt, Func<RpcParameters?, CancellationToken, Task<McpGetPromptResult>> Handler);

        private sealed record CompletionRegistration(
            string ReferenceType,
            string? ReferenceId,
            string? ArgumentName,
            Func<McpCompleteRequest, CancellationToken, Task<McpCompleteResult>> Handler);

        private sealed record Page<T>(List<T> Items, string? NextCursor);
    }
}
