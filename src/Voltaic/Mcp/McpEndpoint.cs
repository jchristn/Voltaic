namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Core;

    internal sealed class McpEndpoint
    {
        private const int DefaultPageSize = 100;

        private readonly object _Lock = new object();
        private readonly List<ToolRegistration> _Tools = new List<ToolRegistration>();
        private readonly List<ResourceRegistration> _Resources = new List<ResourceRegistration>();
        private readonly List<ResourceTemplateRegistration> _ResourceTemplates = new List<ResourceTemplateRegistration>();
        private readonly List<PromptRegistration> _Prompts = new List<PromptRegistration>();
        private readonly List<CompletionRegistration> _CompletionProviders = new List<CompletionRegistration>();
        private static readonly Regex _ToolNamePattern = new Regex("^[A-Za-z0-9_.-]{1,128}\\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public string ProtocolVersion { get; set; } = McpProtocol.LatestProtocolVersion;

        public string MaximumHandshakeProtocolVersion { get; set; } = McpProtocol.NewestHandshakeProtocolVersion;

        public string ServerName { get; set; }

        public string ServerVersion { get; set; } = "1.0.0";

        public bool SupportsListChangedNotifications { get; set; } = true;

        public bool SupportsResourceSubscriptions { get; set; } = true;

        public bool SupportsLogging { get; set; } = true;

        public bool IncludeToolExceptionMessages { get; set; }

        public Action<string>? ErrorLog { get; set; }

        // Raised with "tools", "resources", or "prompts" when a registration changes the list clients see, so the
        // server can send notifications/{kind}/list_changed (MCP: servers that declare listChanged should).
        public Action<string>? ListChanged { get; set; }

        private void RaiseListChanged(string kind)
        {
            try
            {
                ListChanged?.Invoke(kind);
            }
            catch (Exception ex)
            {
                ErrorLog?.Invoke($"list_changed notification failed: {ex.Message}");
            }
        }

        public string? ServerInstructions { get; set; }

        public long? ListCacheTtlMs { get; set; }

        public string? ListCacheScope { get; set; }

        public bool AdvertiseTasksExtension { get; set; }

        public long? DiscoverCacheTtlMs { get; set; }

        public string? DiscoverCacheScope { get; set; }

        public int PageSize { get; set; } = DefaultPageSize;

        public int PingIntervalMs { get; set; } = 30000;

        public int ProgressIntervalMs { get; set; } = 20;

        public int PingTimeoutMs { get; set; } = 10000;

        public int PingFailureThreshold { get; set; } = 1;

        public McpEndpoint(string serverName)
        {
            ServerName = serverName;
        }

        public object Initialize(RpcParameters? args)
        {
            // The initialize request must carry protocolVersion, capabilities, and clientInfo (name and version).
            JsonElement root = ParseObjectParams(args, "initialize");
            if (!root.TryGetProperty("protocolVersion", out JsonElement requested) || requested.ValueKind != JsonValueKind.String || String.IsNullOrWhiteSpace(requested.GetString()))
            {
                throw McpProtocolException.InvalidParams("initialize requires a protocolVersion string.");
            }

            if (!root.TryGetProperty("capabilities", out JsonElement capabilities) || capabilities.ValueKind != JsonValueKind.Object)
            {
                throw McpProtocolException.InvalidParams("initialize requires a capabilities object.");
            }

            if (!root.TryGetProperty("clientInfo", out JsonElement clientInfo) || clientInfo.ValueKind != JsonValueKind.Object
                || !clientInfo.TryGetProperty("name", out JsonElement clientName) || clientName.ValueKind != JsonValueKind.String
                || !clientInfo.TryGetProperty("version", out JsonElement clientVersion) || clientVersion.ValueKind != JsonValueKind.String)
            {
                throw McpProtocolException.InvalidParams("initialize requires a clientInfo object with name and version strings.");
            }

            // The specification requires a server that does not support the requested version to answer with another
            // version it supports (the client disconnects if it cannot use it), so an unknown version, such as one newer
            // than this server knows, negotiates to the cap instead of failing.
            string requestedVersion = requested.GetString()!;
            string negotiated = McpProtocol.IsSupportedVersion(requestedVersion)
                ? McpProtocol.NegotiateHandshakeVersion(requestedVersion, MaximumHandshakeProtocolVersion)
                : MaximumHandshakeProtocolVersion;

            Dictionary<string, object?> result = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                { "protocolVersion", negotiated },
                { "capabilities", BuildCapabilities() },
                { "serverInfo", new McpImplementation { Name = ServerName, Version = ServerVersion } }
            };

            if (!String.IsNullOrEmpty(ServerInstructions)) result["instructions"] = ServerInstructions;
            return result;
        }

        public object Initialized(RpcParameters? args)
        {
            return new McpEmptyResult();
        }

        /// <summary>
        /// Returns the revisions this server supports: the handshake-era revisions up to the handshake cap, and every
        /// stateless-era revision.
        /// </summary>
        public IReadOnlyList<string> SupportedVersions()
        {
            McpProtocolVersionInfo? cap = McpProtocol.GetVersionInfo(MaximumHandshakeProtocolVersion);
            int capIndex = cap == null ? -1 : McpProtocol.SupportedVersions.ToList().IndexOf(cap);
            return McpProtocol.SupportedVersions
                .Where((info, index) => info.Era == McpProtocolEra.Stateless || index <= capIndex)
                .Select(info => info.Version)
                .ToList();
        }

        public McpDiscoverResult Discover(RpcParameters? args)
        {
            // server/discover describes the stateless (2026-07-28) surface. Change notifications there are
            // delivered only through subscriptions/listen, which Voltaic does not implement, so the stateless
            // capabilities must not promise listChanged or resource subscriptions.
            McpServerCapabilities capabilities = BuildCapabilities(includeChangeNotifications: false);

            McpDiscoverResult result = new McpDiscoverResult
            {
                SupportedVersions = SupportedVersions().ToList(),
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

        public McpEmptyResult Ping(RpcParameters? args)
        {
            return new McpEmptyResult();
        }

        public bool UnregisterTool(string name)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            bool removed;
            lock (_Lock)
            {
                removed = _Tools.RemoveAll(tool => StringComparer.Ordinal.Equals(tool.Definition.Name, name)) > 0;
            }

            if (removed) RaiseListChanged("tools");
            return removed;
        }

        public List<McpHeaderParameter>? GetToolHeaderParameters(string name)
        {
            ToolRegistration? tool;
            lock (_Lock)
            {
                tool = _Tools.FirstOrDefault(existing => StringComparer.Ordinal.Equals(existing.Definition.Name, name));
            }

            return tool == null ? null : McpHeaderParameters.Extract(tool.Definition.InputSchema);
        }

        public ToolDefinition RegisterTool(ToolDefinition definition, Func<RpcParameters?, CancellationToken, Task<object>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ValidateToolDefinition(definition);
            definition.InputSchema = NormalizeObjectSchema(definition.InputSchema!, definition.Name, "input");
            if (definition.OutputSchema != null) definition.OutputSchema = NormalizeObjectSchema(definition.OutputSchema, definition.Name, "output");

            lock (_Lock)
            {
                _Tools.RemoveAll(tool => StringComparer.Ordinal.Equals(tool.Definition.Name, definition.Name));
                _Tools.Add(new ToolRegistration(definition, handler));
            }

            RaiseListChanged("tools");
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

            RaiseListChanged("resources");
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
                _ResourceTemplates.Add(new ResourceTemplateRegistration(template, new McpUriTemplate(template.UriTemplate), handler));
            }

            RaiseListChanged("resources");
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

            RaiseListChanged("prompts");
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

            Page<ToolDefinition> page = PageItems(tools, GetCursor(args), tool => tool.Name);
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

            McpNamedArgumentsParams? call = ParseParams<McpNamedArgumentsParams>(args);
            if (call == null || call.Name == null)
            {
                throw McpProtocolException.InvalidParams("tools/call requires a name parameter.");
            }

            string toolName = call.Name;
            if (String.IsNullOrWhiteSpace(toolName))
            {
                throw McpProtocolException.InvalidParams("tools/call name must be a non-empty string.");
            }

            if (call.Arguments != null && JsonSerializer.SerializeToElement(call.Arguments).ValueKind != JsonValueKind.Object)
            {
                throw McpProtocolException.InvalidParams("tools/call arguments must be a JSON object.");
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

            // Input validation failures are tool execution errors, not protocol errors: the MCP specification
            // (2025-11-25 and later) reports them as a result with isError true so the model can read the message
            // and retry with corrected arguments. The handler is not run.
            try
            {
                McpSchemaValidator.Validate(tool.Definition.InputSchema, toolArguments?.RawJson, $"Tool '{toolName}' arguments");
            }
            catch (McpProtocolException validationError)
            {
                McpToolCallResult invalid = McpToolCallResult.FromText(validationError.Message);
                invalid.IsError = true;
                return invalid;
            }

            // Make the MRTR retry state (inputResponses, requestState) available to the handler.
            object result;
            string? statelessVersion = McpRequestProtocol.StatelessVersion;
            using (McpToolCallContext.Push(new McpToolCallContext(toolName, call.InputResponses, call.RequestState, statelessVersion != null, McpRequestScope.Current)))
            {
                try
                {
                    result = await tool.Handler(toolArguments, token).ConfigureAwait(false);
                }
                catch (McpProtocolException)
                {
                    // A handler that throws a protocol exception asks for a JSON-RPC error explicitly.
                    throw;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception handlerError)
                {
                    // API failures and business-logic errors are tool execution errors (MCP 2025-06-18 and later):
                    // a result with isError true carries the message to the model so it can react, instead of a
                    // JSON-RPC protocol error the client may not show to the model.
                    McpToolCallResult failed = McpToolCallResult.FromText(FormatToolError(toolName, handlerError, IncludeToolExceptionMessages, ErrorLog));
                    failed.IsError = true;
                    return failed;
                }
            }

            // input_required exists only in the stateless revision. A handshake-era client would reject it as an
            // invalid tool result, so report it as a tool execution error the model can read instead.
            if (result is McpInputRequiredResult && statelessVersion == null)
            {
                McpToolCallResult unsupported = McpToolCallResult.FromText(
                    $"Tool '{toolName}' needs additional input from the user, which requires MCP protocol version {McpProtocol.ProtocolVersion20260728} or later; this request used an earlier protocol version.");
                unsupported.IsError = true;
                return unsupported;
            }

            // Multi Round-Trip Requests: a handler may return an input-required result to ask the
            // client for more information. It flows through unchanged rather than being wrapped as
            // a tool result.
            if (result is McpInputRequiredResult inputRequired)
            {
                ValidateInputRequired(toolName, inputRequired);
                return inputRequired;
            }

            if (result is McpToolCallResult toolCallResult)
            {
                // A tool with an output schema must return structured content that conforms to it (except on errors).
                if (tool.Definition.OutputSchema != null && toolCallResult.StructuredContent == null && toolCallResult.IsError != true)
                {
                    throw new McpProtocolException(-32603, $"Tool '{toolName}' declares an output schema but returned no structured content.");
                }

                if (tool.Definition.OutputSchema != null && toolCallResult.StructuredContent != null)
                {
                    ValidateOutput(tool.Definition.OutputSchema, toolCallResult.StructuredContent, $"Tool '{toolName}' structured output");
                    RequireObjectForHandshake(tool.Definition.OutputSchema, toolCallResult.StructuredContent, statelessVersion, toolName);
                }

                return toolCallResult;
            }

            if (tool.Definition.OutputSchema != null)
            {
                ValidateOutput(tool.Definition.OutputSchema, result, $"Tool '{toolName}' output");
                RequireObjectForHandshake(tool.Definition.OutputSchema, result, statelessVersion, toolName);
                return McpToolCallResult.FromStructured(result);
            }

            if (result is string text)
            {
                return McpToolCallResult.FromText(text);
            }

            return McpToolCallResult.FromText(JsonSerializer.Serialize(result));
        }

        // Handshake-era sessions see a typeless object-only output schema as "type": "object" (McpVersionCompatibility),
        // so the structured content they receive must be an object; anything else would break the advertised schema.
        private static void RequireObjectForHandshake(object outputSchema, object structuredContent, string? statelessVersion, string toolName)
        {
            if (statelessVersion != null || !McpVersionCompatibility.IsObjectOnlyBeforeStateless(outputSchema)) return;

            // Revisions before 2025-06-18 receive neither the output schema nor structured content.
            string? negotiated = McpRequestScope.Current?.ProtocolVersion;
            if (negotiated != null && String.CompareOrdinal(negotiated, McpProtocol.ProtocolVersion20250618) < 0) return;
            JsonElement value = structuredContent is JsonElement element ? element : JsonSerializer.SerializeToElement(structuredContent);
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new McpProtocolException(-32603, $"Tool '{toolName}' structured output must be an object on protocol versions before {McpProtocol.ProtocolVersion20260728}, where its output schema is advertised with \"type\": \"object\".");
            }
        }

        // A result that violates the tool's own output schema is a server fault, reported as an internal error.
        private static void ValidateOutput(object outputSchema, object value, string context)
        {
            try
            {
                McpSchemaValidator.Validate(outputSchema, JsonSerializer.Serialize(value), context);
            }
            catch (McpProtocolException invalid)
            {
                throw new McpProtocolException(-32603, invalid.Message);
            }
        }

        // An input-required result (2026-07-28) must carry inputRequests or requestState, may ask only for elicitation,
        // sampling, or roots, and may ask only for what the client declared (else -32021, MissingRequiredClientCapability).
        private static void ValidateInputRequired(string toolName, McpInputRequiredResult result)
        {
            bool hasRequests = result.InputRequests != null && result.InputRequests.Count > 0;
            if (!hasRequests && String.IsNullOrEmpty(result.RequestState))
            {
                throw new McpProtocolException(-32603, $"Tool '{toolName}' returned an input-required result with neither inputRequests nor requestState.");
            }

            if (!hasRequests) return;

            JsonElement? declared = McpRequestScope.Current?.ClientCapabilities;
            Dictionary<string, object> missing = new Dictionary<string, object>(StringComparer.Ordinal);
            List<string> missingPaths = new List<string>();
            foreach (KeyValuePair<string, McpInputRequest> entry in result.InputRequests!)
            {
                List<string>? paths = RequiredCapabilityPaths(entry.Value, declared);
                if (paths == null)
                {
                    throw new McpProtocolException(-32603, $"Tool '{toolName}' requested input with unsupported method '{entry.Value.Method}' or elicitation mode; only elicitation/create (form or url mode), sampling/createMessage, and roots/list are allowed.");
                }

                foreach (string path in paths)
                {
                    if (McpClientCapabilityPath.IsDeclared(declared, path) || missingPaths.Contains(path)) continue;
                    missingPaths.Add(path);

                    // requiredCapabilities mirrors the ClientCapabilities shape, for example {"elicitation":{"url":{}}}.
                    Dictionary<string, object> level = missing;
                    foreach (string segment in path.Split('.'))
                    {
                        if (!level.TryGetValue(segment, out object? child) || child is not Dictionary<string, object> next)
                        {
                            next = new Dictionary<string, object>(StringComparer.Ordinal);
                            level[segment] = next;
                        }

                        level = next;
                    }
                }
            }

            if (missing.Count > 0)
            {
                throw McpProtocolException.MissingRequiredClientCapability(
                    $"Tool '{toolName}' needs the client capabilities {String.Join(", ", missingPaths)}, which the request did not declare.",
                    missing);
            }
        }

        // The client capabilities an input request needs, as dotted paths: elicitation.url for URL-mode elicitation,
        // elicitation.form for form mode when the client lists modes (an empty elicitation object means form only),
        // sampling.tools for sampling that offers tools and sampling.context for sampling that asks to include context,
        // else the method's capability. Null for other methods and for an elicitation mode other than form or url.
        private static List<string>? RequiredCapabilityPaths(McpInputRequest request, JsonElement? declared)
        {
            JsonElement parameters = request.Params == null ? default : (request.Params is JsonElement element ? element : JsonSerializer.SerializeToElement(request.Params));
            bool hasParams = parameters.ValueKind == JsonValueKind.Object;
            switch (request.Method)
            {
                case "elicitation/create":
                    string mode = hasParams && parameters.TryGetProperty("mode", out JsonElement modeElement) && modeElement.ValueKind == JsonValueKind.String
                        ? modeElement.GetString() ?? "form"
                        : "form";
                    if (mode == "url") return new List<string> { "elicitation.url" };
                    // Elicitation has two modes; any other is a malformed request the server built, not a capability.
                    if (mode != "form") return null;
                    bool listsModes = declared.HasValue && declared.Value.ValueKind == JsonValueKind.Object
                        && declared.Value.TryGetProperty("elicitation", out JsonElement elicitation)
                        && elicitation.ValueKind == JsonValueKind.Object && elicitation.EnumerateObject().Any();
                    return new List<string> { listsModes ? "elicitation.form" : "elicitation" };
                case "sampling/createMessage":
                    List<string> sampling = new List<string> { "sampling" };
                    bool offersTools = hasParams && parameters.TryGetProperty("tools", out JsonElement tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0;
                    if (offersTools) sampling.Add("sampling.tools");
                    bool includesContext = hasParams && parameters.TryGetProperty("includeContext", out JsonElement context) && context.ValueKind == JsonValueKind.String
                        && (context.GetString() == "thisServer" || context.GetString() == "allServers");
                    if (includesContext) sampling.Add("sampling.context");
                    return sampling;
                case "roots/list":
                    return new List<string> { "roots" };
                default:
                    return null;
            }
        }

        // Tool outputs must be sanitized (MCP security considerations): only McpToolException messages, which the
        // handler wrote for the model, are shown unless IncludeToolExceptionMessages is set. Details go to the log.
        private static string FormatToolError(string toolName, Exception error, bool includeMessages, Action<string>? log)
        {
            if (log != null)
            {
                try
                {
                    log($"Tool '{toolName}' threw {error.GetType().FullName}: {error.Message}");
                }
                catch
                {
                }
            }

            if (error is McpToolException) return error.Message;
            if (!includeMessages) return $"Tool '{toolName}' failed because of an internal error.";

            string message = String.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message;
            return $"Tool '{toolName}' failed: {message}";
        }

        public McpListResourcesResult ListResources(RpcParameters? args)
        {
            List<McpResource> resources;
            lock (_Lock)
            {
                resources = _Resources.Select(resource => resource.Resource).ToList();
            }

            Page<McpResource> page = PageItems(resources, GetCursor(args), resource => resource.Uri);
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

            Page<McpResourceTemplate> page = PageItems(templates, GetCursor(args), template => template.UriTemplate);
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
            McpUriParams? parameters = ParseParams<McpUriParams>(args);
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
                if (template.Pattern.TryMatch(uri, out Dictionary<string, string> variables))
                {
                    return await template.Handler(uri, variables, token).ConfigureAwait(false);
                }
            }

            // Resource not found: -32002 through 2025-11-25, -32602 from 2026-07-28.
            string? version = McpRequestScope.Current?.ProtocolVersion;
            if (version != null && McpProtocol.GetVersionInfo(version)?.Era == McpProtocolEra.Stateless)
            {
                throw McpProtocolException.InvalidParams($"Resource '{uri}' was not found.", new { uri });
            }

            throw new McpProtocolException(-32002, $"Resource '{uri}' was not found.", new { uri });
        }

        public McpListPromptsResult ListPrompts(RpcParameters? args)
        {
            List<McpPrompt> prompts;
            lock (_Lock)
            {
                prompts = _Prompts.Select(prompt => prompt.Prompt).ToList();
            }

            Page<McpPrompt> page = PageItems(prompts, GetCursor(args), prompt => prompt.Name);
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
            McpNamedArgumentsParams? request = ParseParams<McpNamedArgumentsParams>(args);
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

            if (request.Arguments != null)
            {
                JsonElement provided = JsonSerializer.SerializeToElement(request.Arguments);
                if (provided.ValueKind != JsonValueKind.Object)
                {
                    throw McpProtocolException.InvalidParams("prompts/get arguments must be a JSON object.");
                }

                foreach (JsonProperty argument in provided.EnumerateObject())
                {
                    if (argument.Value.ValueKind != JsonValueKind.String)
                    {
                        throw McpProtocolException.InvalidParams($"Prompt argument '{argument.Name}' must be a string.");
                    }
                }
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

            McpCompleteRequest? request = ParseParams<McpCompleteRequest>(args);
            if (request == null)
            {
                throw McpProtocolException.InvalidParams("completion/complete params were invalid.");
            }

            ValidateCompletionRequest(request);
            EnsureCompletionReferenceExists(request);

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

            // At most 100 values may be returned; total and hasMore describe everything the provider found.
            int available = result.Completion.Values.Count;
            result.Completion.Values = result.Completion.Values.Take(100).ToList();
            if (result.Completion.Total == null)
            {
                result.Completion.Total = available;
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
            if (!SupportsResourceSubscriptions) throw new McpProtocolException(-32601, "Method not found: resources/subscribe (resource subscriptions are not supported).");
            McpRequestScope.Current?.Session.Subscribe(uri);
            return new McpEmptyResult();
        }

        public object UnsubscribeResource(RpcParameters? args)
        {
            string uri = GetRequiredUri(args, "resources/unsubscribe");
            if (!SupportsResourceSubscriptions) throw new McpProtocolException(-32601, "Method not found: resources/unsubscribe (resource subscriptions are not supported).");
            McpRequestScope.Current?.Session.Unsubscribe(uri);
            return new McpEmptyResult();
        }

        public object SetLogLevel(RpcParameters? args)
        {
            if (!SupportsLogging) throw new McpProtocolException(-32601, "Method not found: logging/setLevel (logging is not supported).");

            McpLevelParams? parameters = ParseParams<McpLevelParams>(args);
            if (parameters == null || parameters.Level == null)
            {
                throw McpProtocolException.InvalidParams("logging/setLevel requires a level parameter.");
            }

            string level = parameters.Level;
            if (!IsValidLogLevel(level))
            {
                throw McpProtocolException.InvalidParams($"Invalid log level '{level}'.");
            }

            McpSessionState? session = McpRequestScope.Current?.Session;
            if (session != null) session.LogLevel = level;
            return new McpEmptyResult();
        }

        public object Cancelled(RpcParameters? args)
        {
            return new McpEmptyResult();
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
            }
        }

        private McpServerCapabilities BuildCapabilities(bool includeChangeNotifications = true)
        {
            // Every feature is declared whether or not anything is registered yet: a capability declared at initialize
            // cannot be added later, so tools, resources, or prompts registered afterwards would otherwise be
            // unreachable for that session. Empty lists are valid answers.
            McpServerCapabilities capabilities = new McpServerCapabilities
            {
                Tools = new McpListChangedCapability { ListChanged = includeChangeNotifications ? SupportsListChangedNotifications : null },
                Resources = new McpResourceCapability
                {
                    ListChanged = includeChangeNotifications ? SupportsListChangedNotifications : null,
                    Subscribe = includeChangeNotifications ? SupportsResourceSubscriptions : null
                },
                Prompts = new McpListChangedCapability { ListChanged = includeChangeNotifications ? SupportsListChangedNotifications : null },
                Completions = new { },
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
            if (!_ToolNamePattern.IsMatch(definition.Name))
            {
                throw new ArgumentException($"Tool name '{definition.Name}' is invalid: tool names must be 1 to 128 characters of A-Z, a-z, 0-9, underscore, hyphen, and dot.", nameof(definition));
            }

            // An invalid x-mcp-header annotation makes the whole definition invalid (clients must drop the tool).
            try
            {
                McpHeaderParameters.Extract(definition.InputSchema);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Tool '{definition.Name}' input schema: {ex.Message}", nameof(definition), ex);
            }
        }

        // Tool input and output schemas must describe JSON objects. A schema without a type gets "type": "object";
        // any other type is rejected.
        private static JsonElement NormalizeObjectSchema(object schema, string toolName, string kind)
        {
            JsonElement element = schema is JsonElement json ? json : JsonSerializer.SerializeToElement(schema);
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException($"Tool '{toolName}' {kind} schema must be a JSON object.");
            }

            // The schema must be one the validator can enforce as written: a supported dialect, well-formed keyword
            // values, and references that resolve within it (anything else would otherwise validate permissively).
            string? problem = McpSchemaValidator.CheckSchema(element);
            if (problem != null)
            {
                throw new ArgumentException($"Tool '{toolName}' {kind} schema: {problem}");
            }

            if (element.TryGetProperty("type", out JsonElement type))
            {
                // Input schemas are objects in every revision. An output schema of another type is allowed from
                // 2026-07-28; tools/list omits it for older sessions, whose schema requires an object.
                bool isObject = type.ValueKind == JsonValueKind.String && type.GetString() == "object";
                if (!isObject && kind == "input")
                {
                    throw new ArgumentException($"Tool '{toolName}' {kind} schema must have \"type\": \"object\".");
                }

                return element.Clone();
            }

            // An output schema is kept exactly as written (2026-07-28 allows any output schema); tools/list adapts it for
            // older sessions. An input schema without a type is an object schema.
            if (kind == "output") return element.Clone();

            System.Text.Json.Nodes.JsonObject withType = System.Text.Json.Nodes.JsonObject.Create(element) ?? new System.Text.Json.Nodes.JsonObject();
            System.Text.Json.Nodes.JsonObject copy = new System.Text.Json.Nodes.JsonObject { ["type"] = "object" };
            foreach (KeyValuePair<string, System.Text.Json.Nodes.JsonNode?> property in withType.ToList())
            {
                copy[property.Key] = property.Value?.DeepClone();
            }

            return JsonSerializer.SerializeToElement(copy);
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

        // Cursors are opaque to clients. Each encodes the position and key of the last item returned, so a page resumes
        // after that item even when earlier items were added or removed in between.
        private Page<T> PageItems<T>(List<T> items, string? cursor, Func<T, string> key)
        {
            int offset = 0;
            if (!String.IsNullOrEmpty(cursor))
            {
                string decoded;
                try
                {
                    decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                }
                catch (FormatException)
                {
                    throw McpProtocolException.InvalidParams($"Invalid cursor '{cursor}'.");
                }

                int separator = decoded.IndexOf(':');
                if (separator <= 0 || !Int32.TryParse(decoded.Substring(0, separator), out int position) || position < 0)
                {
                    throw McpProtocolException.InvalidParams($"Invalid cursor '{cursor}'.");
                }

                string lastKey = decoded.Substring(separator + 1);
                int found = items.FindIndex(item => StringComparer.Ordinal.Equals(key(item), lastKey));
                offset = found >= 0 ? found + 1 : Math.Min(position, items.Count);
            }

            int pageSize = Math.Max(1, PageSize);
            List<T> pageItems = items.Skip(offset).Take(pageSize).ToList();
            int nextOffset = offset + pageItems.Count;
            string? nextCursor = nextOffset < items.Count && pageItems.Count > 0
                ? Convert.ToBase64String(Encoding.UTF8.GetBytes(nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + key(pageItems[pageItems.Count - 1])))
                : null;
            return new Page<T>(pageItems, nextCursor);
        }

        private static JsonElement ParseObjectParams(RpcParameters? args, string method)
        {
            if (args == null || !args.HasValue) throw McpProtocolException.InvalidParams($"{method} requires params.");
            using (JsonDocument document = JsonDocument.Parse(args.RawJson!))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw McpProtocolException.InvalidParams($"{method} params must be a JSON object.");
                return document.RootElement.Clone();
            }
        }

        private static string? GetCursor(RpcParameters? args)
        {
            McpCursorParams? parameters = ParseParams<McpCursorParams>(args);
            return parameters?.Cursor;
        }

        private static string GetRequiredUri(RpcParameters? args, string method)
        {
            McpUriParams? parameters = ParseParams<McpUriParams>(args);
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

        // Reads typed params; a member of the wrong JSON type is invalid params (-32602), not an internal error.
        private static T? ParseParams<T>(RpcParameters? args) where T : class
        {
            if (args == null || !args.HasValue) return null;
            try
            {
                return args.Deserialize<T>();
            }
            catch (JsonException invalid)
            {
                throw McpProtocolException.InvalidParams($"Invalid params: {invalid.Message}");
            }
            catch (NotSupportedException invalid)
            {
                throw McpProtocolException.InvalidParams($"Invalid params: {invalid.Message}");
            }
        }

        private static void ValidateCompletionRequest(McpCompleteRequest request)
        {
            if (request.Ref == null || request.Argument == null)
            {
                throw McpProtocolException.InvalidParams("completion/complete requires ref and argument objects.");
            }

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

        // completion/complete must name a prompt or resource (template) this server has (-32602 otherwise).
        private void EnsureCompletionReferenceExists(McpCompleteRequest request)
        {
            lock (_Lock)
            {
                if (request.Ref.Type == "ref/prompt")
                {
                    PromptRegistration? prompt = _Prompts.FirstOrDefault(existing => StringComparer.Ordinal.Equals(existing.Prompt.Name, request.Ref.Name));
                    if (prompt == null) throw McpProtocolException.InvalidParams($"Prompt '{request.Ref.Name}' was not found.");
                    return;
                }

                bool known = _Resources.Any(existing => StringComparer.Ordinal.Equals(existing.Resource.Uri, request.Ref.Uri))
                    || _ResourceTemplates.Any(existing => StringComparer.Ordinal.Equals(existing.Template.UriTemplate, request.Ref.Uri));
                if (!known) throw McpProtocolException.InvalidParams($"Resource or resource template '{request.Ref.Uri}' was not found.");
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

        private sealed record ToolRegistration(ToolDefinition Definition, Func<RpcParameters?, CancellationToken, Task<object>> Handler);

        private sealed record ResourceRegistration(McpResource Resource, Func<string, CancellationToken, Task<McpReadResourceResult>> Handler);

        private sealed record ResourceTemplateRegistration(
            McpResourceTemplate Template,
            McpUriTemplate Pattern,
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
