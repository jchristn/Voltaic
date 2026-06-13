namespace Test.Shared
{
    using System.Text.Json;
    using Touchstone.Core;
    using Voltaic;

    public static class ComprehensiveModelSuites
    {
        public static TestSuiteDescriptor JsonRpcSerializationMatrix()
        {
            const string suiteId = "ModelApi.JsonRpc.Matrix";
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case(suiteId, "RequestSupportsStringId", "JsonRpcRequest preserves string ids", ct =>
            {
                JsonElement json = TestJson.SerializeToElement(new JsonRpcRequest { Method = "x", Id = "abc" });
                TestAssert.Equal("abc", json.GetProperty("id").GetString());
                return Task.CompletedTask;
            }));

            cases.Add(Case(suiteId, "RequestSupportsNumberId", "JsonRpcRequest preserves numeric ids", ct =>
            {
                JsonElement json = TestJson.SerializeToElement(new JsonRpcRequest { Method = "x", Id = 123 });
                TestAssert.Equal(123, json.GetProperty("id").GetInt32());
                return Task.CompletedTask;
            }));

            cases.Add(Case(suiteId, "RequestSupportsArrayParams", "JsonRpcRequest serializes array params", ct =>
            {
                JsonElement json = TestJson.SerializeToElement(new JsonRpcRequest { Method = "sum", Params = new[] { 1, 2, 3 }, Id = 1 });
                TestAssert.Equal(JsonValueKind.Array, json.GetProperty("params").ValueKind);
                TestAssert.Equal(3, json.GetProperty("params").GetArrayLength());
                return Task.CompletedTask;
            }));

            cases.Add(Case(suiteId, "RequestSupportsPrimitiveParams", "JsonRpcRequest serializes primitive params", ct =>
            {
                JsonElement json = TestJson.SerializeToElement(new JsonRpcRequest { Method = "set", Params = true, Id = 1 });
                TestAssert.True(json.GetProperty("params").GetBoolean(), "Boolean params should serialize.");
                return Task.CompletedTask;
            }));

            cases.Add(Case(suiteId, "RequestUnknownFieldsIgnored", "JsonRpcRequest ignores unknown JSON fields", ct =>
            {
                JsonRpcRequest? request = JsonSerializer.Deserialize<JsonRpcRequest>(
                    "{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"id\":5,\"extra\":\"ignored\"}");
                TestAssert.NotNull(request, "Request should deserialize.");
                TestAssert.Equal("ping", request!.Method);
                return Task.CompletedTask;
            }));

            cases.Add(Case(suiteId, "ResponseIncludesNullId", "JsonRpcResponse keeps null id for JSON-RPC errors", ct =>
            {
                string json = TestJson.Serialize(new JsonRpcResponse { Error = JsonRpcError.ParseError(), Id = null });
                TestAssert.True(json.Contains("\"id\":null"), "Response id must remain present even when null.");
                return Task.CompletedTask;
            }));

            cases.Add(Case(suiteId, "ResponseDeserializesObjectResult", "JsonRpcResponse deserializes object result as JsonElement", ct =>
            {
                JsonRpcResponse? response = JsonSerializer.Deserialize<JsonRpcResponse>(
                    "{\"jsonrpc\":\"2.0\",\"result\":{\"ok\":true},\"id\":\"r1\"}");
                TestAssert.NotNull(response, "Response should deserialize.");
                JsonElement result = (JsonElement)response!.Result!;
                TestAssert.True(result.GetProperty("ok").GetBoolean(), "Object result should be readable.");
                return Task.CompletedTask;
            }));

            cases.Add(Case(suiteId, "ErrorDataOmittedWhenNull", "JsonRpcError omits null data", ct =>
            {
                string json = TestJson.Serialize(JsonRpcError.InvalidParams());
                TestAssert.False(json.Contains("\"data\""), "Null error data should be omitted.");
                return Task.CompletedTask;
            }));

            cases.Add(Case(suiteId, "ErrorDataRoundTrips", "JsonRpcError serializes and deserializes data", ct =>
            {
                JsonRpcError error = new JsonRpcError
                {
                    Code = -32001,
                    Message = "custom",
                    Data = new { detail = "bad" }
                };

                JsonRpcError? roundTrip = JsonSerializer.Deserialize<JsonRpcError>(TestJson.Serialize(error));
                TestAssert.NotNull(roundTrip, "Error should deserialize.");
                JsonElement data = (JsonElement)roundTrip!.Data!;
                TestAssert.Equal("bad", data.GetProperty("detail").GetString());
                return Task.CompletedTask;
            }));

            foreach ((string name, Func<JsonRpcError> create, int code, string message) in new List<(string name, Func<JsonRpcError> create, int code, string message)>
            {
                ("ParseError", JsonRpcError.ParseError, -32700, "Parse error"),
                ("InvalidRequest", JsonRpcError.InvalidRequest, -32600, "Invalid request"),
                ("MethodNotFound", JsonRpcError.MethodNotFound, -32601, "Method not found"),
                ("InvalidParams", JsonRpcError.InvalidParams, -32602, "Invalid params"),
                ("InternalError", JsonRpcError.InternalError, -32603, "Internal error")
            })
            {
                cases.Add(Case(suiteId, $"ErrorFactory{name}", $"{name} creates the standard JSON-RPC error", ct =>
                {
                    JsonRpcError error = create();
                    TestAssert.Equal(code, error.Code);
                    TestAssert.Equal(message, error.Message);
                    TestAssert.Null(error.Data, "Factory errors should not include data.");
                    return Task.CompletedTask;
                }));
            }

            return new TestSuiteDescriptor(suiteId, "JSON-RPC Serialization Matrix", cases);
        }

        public static TestSuiteDescriptor McpModelSerializationMatrix()
        {
            const string suiteId = "ModelApi.Mcp.Matrix";
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>
            {
                Case(suiteId, "ProtocolSupportedVersionTrue", "McpProtocol recognizes supported versions", ct =>
                {
                    TestAssert.True(McpProtocol.IsSupportedVersion(McpProtocol.LatestProtocolVersion), "Latest version should be supported.");
                    TestAssert.True(McpProtocol.IsSupportedVersion(McpProtocol.ProtocolVersion20250326), "Previous version should be supported.");
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ProtocolSupportedVersionFalse", "McpProtocol rejects null, blank, and unknown versions", ct =>
                {
                    TestAssert.False(McpProtocol.IsSupportedVersion(null), "Null should not be treated as an explicit supported version.");
                    TestAssert.False(McpProtocol.IsSupportedVersion(""), "Blank should not be supported.");
                    TestAssert.False(McpProtocol.IsSupportedVersion("2024-01-01"), "Unknown version should not be supported.");
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ProtocolNegotiatesWhitespaceToLatest", "McpProtocol negotiates whitespace to latest", ct =>
                {
                    TestAssert.Equal(McpProtocol.LatestProtocolVersion, McpProtocol.NegotiateVersion("   "));
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ImplementationOmittedNulls", "McpImplementation omits optional nulls", ct =>
                {
                    string json = TestJson.Serialize(new McpImplementation { Name = "n", Version = "v" });
                    TestAssert.False(json.Contains("\"title\""), "Title should be omitted.");
                    TestAssert.False(json.Contains("\"description\""), "Description should be omitted.");
                    TestAssert.False(json.Contains("\"websiteUrl\""), "Website URL should be omitted.");
                    TestAssert.False(json.Contains("\"icons\""), "Icons should be omitted.");
                    return Task.CompletedTask;
                }),

                Case(suiteId, "IconAllFields", "McpIcon serializes all optional fields", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpIcon
                    {
                        Src = "icon.png",
                        MimeType = "image/png",
                        Sizes = new List<string> { "16x16", "32x32" },
                        Theme = "dark"
                    });
                    TestAssert.Equal("icon.png", json.GetProperty("src").GetString());
                    TestAssert.Equal("image/png", json.GetProperty("mimeType").GetString());
                    TestAssert.Equal(2, json.GetProperty("sizes").GetArrayLength());
                    TestAssert.Equal("dark", json.GetProperty("theme").GetString());
                    return Task.CompletedTask;
                }),

                Case(suiteId, "AnnotationsAllHints", "McpAnnotations serializes every hint", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpAnnotations
                    {
                        Title = "Read",
                        ReadOnlyHint = true,
                        DestructiveHint = false,
                        IdempotentHint = true,
                        OpenWorldHint = false
                    });
                    TestAssert.Equal("Read", json.GetProperty("title").GetString());
                    TestAssert.True(json.GetProperty("readOnlyHint").GetBoolean(), "Read-only hint should serialize.");
                    TestAssert.False(json.GetProperty("destructiveHint").GetBoolean(), "Destructive hint should serialize false.");
                    TestAssert.True(json.GetProperty("idempotentHint").GetBoolean(), "Idempotent hint should serialize.");
                    TestAssert.False(json.GetProperty("openWorldHint").GetBoolean(), "Open-world hint should serialize false.");
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ResultMeta", "McpResult serializes protocol metadata", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpResult { Meta = new Dictionary<string, object?> { ["trace"] = "abc" } });
                    TestAssert.Equal("abc", json.GetProperty("_meta").GetProperty("trace").GetString());
                    return Task.CompletedTask;
                }),

                Case(suiteId, "PaginatedResultNextCursor", "McpPaginatedResult serializes nextCursor and metadata", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpPaginatedResult
                    {
                        NextCursor = "100",
                        Meta = new Dictionary<string, object?> { ["count"] = 100 }
                    });
                    TestAssert.Equal("100", json.GetProperty("nextCursor").GetString());
                    TestAssert.Equal(100, json.GetProperty("_meta").GetProperty("count").GetInt32());
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ClientCapabilitiesAllFields", "McpClientCapabilities serializes all advertised capability fields", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpClientCapabilities
                    {
                        Experimental = new Dictionary<string, object> { ["x"] = true },
                        Roots = new McpListChangedCapability { ListChanged = true },
                        Sampling = new { supported = true },
                        Elicitation = new { supported = false }
                    });
                    TestAssert.True(json.GetProperty("experimental").GetProperty("x").GetBoolean(), "Experimental capability should serialize.");
                    TestAssert.True(json.GetProperty("roots").GetProperty("listChanged").GetBoolean(), "Roots should serialize.");
                    TestAssert.True(json.GetProperty("sampling").GetProperty("supported").GetBoolean(), "Sampling should serialize.");
                    TestAssert.False(json.GetProperty("elicitation").GetProperty("supported").GetBoolean(), "Elicitation should serialize false.");
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ServerCapabilitiesAllFields", "McpServerCapabilities serializes all capability fields", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpServerCapabilities
                    {
                        Experimental = new Dictionary<string, object> { ["draft"] = "yes" },
                        Logging = new { },
                        Completions = new { },
                        Prompts = new McpListChangedCapability { ListChanged = true },
                        Resources = new McpResourceCapability { ListChanged = true, Subscribe = true },
                        Tools = new McpListChangedCapability { ListChanged = false }
                    });
                    TestAssert.Equal("yes", json.GetProperty("experimental").GetProperty("draft").GetString());
                    TestAssert.Equal(JsonValueKind.Object, json.GetProperty("logging").ValueKind);
                    TestAssert.Equal(JsonValueKind.Object, json.GetProperty("completions").ValueKind);
                    TestAssert.True(json.GetProperty("prompts").GetProperty("listChanged").GetBoolean(), "Prompts should serialize.");
                    TestAssert.True(json.GetProperty("resources").GetProperty("subscribe").GetBoolean(), "Resource subscribe should serialize.");
                    TestAssert.False(json.GetProperty("tools").GetProperty("listChanged").GetBoolean(), "False listChanged should serialize.");
                    return Task.CompletedTask;
                }),

                Case(suiteId, "TextContentMetaAndAnnotations", "McpTextContent serializes metadata and annotations", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpTextContent
                    {
                        Text = "hello",
                        Annotations = new McpAnnotations { Title = "Greeting" },
                        Meta = new Dictionary<string, object?> { ["lang"] = "en" }
                    });
                    TestAssert.Equal("text", json.GetProperty("type").GetString());
                    TestAssert.Equal("Greeting", json.GetProperty("annotations").GetProperty("title").GetString());
                    TestAssert.Equal("en", json.GetProperty("_meta").GetProperty("lang").GetString());
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ImageContentAnnotations", "McpImageContent serializes annotations", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpImageContent
                    {
                        Data = "AA==",
                        MimeType = "image/png",
                        Annotations = new McpAnnotations { Title = "Image" }
                    });
                    TestAssert.Equal("image", json.GetProperty("type").GetString());
                    TestAssert.Equal("AA==", json.GetProperty("data").GetString());
                    TestAssert.Equal("Image", json.GetProperty("annotations").GetProperty("title").GetString());
                    return Task.CompletedTask;
                }),

                Case(suiteId, "AudioContentDefaults", "McpAudioContent has the audio discriminator", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpAudioContent { Data = "AA==", MimeType = "audio/wav" });
                    TestAssert.Equal("audio", json.GetProperty("type").GetString());
                    TestAssert.Equal("audio/wav", json.GetProperty("mimeType").GetString());
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ResourceFullMetadata", "McpResource serializes full metadata", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpResource
                    {
                        Uri = "voltaic://r",
                        Name = "r",
                        Title = "Resource",
                        Description = "Description",
                        MimeType = "text/plain",
                        Size = 42,
                        Annotations = new McpAnnotations { ReadOnlyHint = true },
                        Icons = new List<McpIcon> { new McpIcon { Src = "icon.png" } }
                    });
                    TestAssert.Equal("voltaic://r", json.GetProperty("uri").GetString());
                    TestAssert.Equal(42L, json.GetProperty("size").GetInt64());
                    TestAssert.True(json.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), "Annotations should serialize.");
                    TestAssert.Equal("icon.png", json.GetProperty("icons")[0].GetProperty("src").GetString());
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ResourceOmittedNulls", "McpResource omits nullable metadata when absent", ct =>
                {
                    string json = TestJson.Serialize(new McpResource { Uri = "voltaic://r", Name = "r" });
                    TestAssert.False(json.Contains("\"mimeType\""), "MIME type should be omitted.");
                    TestAssert.False(json.Contains("\"size\""), "Size should be omitted.");
                    TestAssert.False(json.Contains("\"annotations\""), "Annotations should be omitted.");
                    TestAssert.False(json.Contains("\"icons\""), "Icons should be omitted.");
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ResourceTemplateFullMetadata", "McpResourceTemplate serializes full metadata", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpResourceTemplate
                    {
                        UriTemplate = "voltaic://docs/{name}",
                        Name = "doc",
                        Title = "Doc",
                        Description = "Dynamic doc",
                        MimeType = "text/plain"
                    });
                    TestAssert.Equal("voltaic://docs/{name}", json.GetProperty("uriTemplate").GetString());
                    TestAssert.Equal("Doc", json.GetProperty("title").GetString());
                    return Task.CompletedTask;
                }),

                Case(suiteId, "TextResourceContentsOmittedMimeType", "McpTextResourceContents omits null MIME type", ct =>
                {
                    string json = TestJson.Serialize(new McpTextResourceContents { Uri = "voltaic://r", Text = "content" });
                    TestAssert.True(json.Contains("\"text\":\"content\""), "Text should serialize.");
                    TestAssert.False(json.Contains("\"mimeType\""), "Null MIME type should be omitted.");
                    return Task.CompletedTask;
                }),

                Case(suiteId, "BlobResourceContents", "McpBlobResourceContents serializes base64 blobs", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpBlobResourceContents
                    {
                        Uri = "voltaic://blob",
                        MimeType = "application/octet-stream",
                        Blob = "AAE="
                    });
                    TestAssert.Equal("AAE=", json.GetProperty("blob").GetString());
                    TestAssert.Equal("application/octet-stream", json.GetProperty("mimeType").GetString());
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ReadResourceResultDefaults", "McpReadResourceResult starts with an empty contents list", ct =>
                {
                    McpReadResourceResult result = new McpReadResourceResult();
                    TestAssert.NotNull(result.Contents, "Contents should be initialized.");
                    TestAssert.Equal(0, result.Contents.Count);
                    return Task.CompletedTask;
                }),

                Case(suiteId, "PromptArgumentRequiredFalse", "McpPromptArgument serializes required=false", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpPromptArgument { Name = "topic", Required = false });
                    TestAssert.False(json.GetProperty("required").GetBoolean(), "Required false should be preserved.");
                    return Task.CompletedTask;
                }),

                Case(suiteId, "PromptArgumentOmittedRequired", "McpPromptArgument omits null required", ct =>
                {
                    string json = TestJson.Serialize(new McpPromptArgument { Name = "topic" });
                    TestAssert.False(json.Contains("\"required\""), "Null Required should be omitted.");
                    return Task.CompletedTask;
                }),

                Case(suiteId, "PromptOmittedArguments", "McpPrompt omits null arguments", ct =>
                {
                    string json = TestJson.Serialize(new McpPrompt { Name = "p" });
                    TestAssert.False(json.Contains("\"arguments\""), "Null arguments should be omitted.");
                    return Task.CompletedTask;
                }),

                Case(suiteId, "PromptMessageDefaults", "McpPromptMessage defaults to user text content", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpPromptMessage());
                    TestAssert.Equal("user", json.GetProperty("role").GetString());
                    TestAssert.Equal("text", json.GetProperty("content").GetProperty("type").GetString());
                    return Task.CompletedTask;
                }),

                Case(suiteId, "GetPromptResultMetadata", "McpGetPromptResult serializes messages and metadata", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpGetPromptResult
                    {
                        Description = "desc",
                        Meta = new Dictionary<string, object?> { ["source"] = "test" },
                        Messages = new List<McpPromptMessage>
                        {
                            new McpPromptMessage { Role = "assistant", Content = new McpTextContent { Text = "ready" } }
                        }
                    });
                    TestAssert.Equal("desc", json.GetProperty("description").GetString());
                    TestAssert.Equal("test", json.GetProperty("_meta").GetProperty("source").GetString());
                    TestAssert.Equal("assistant", json.GetProperty("messages")[0].GetProperty("role").GetString());
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ListToolsResult", "McpListToolsResult serializes tools and cursor", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpListToolsResult
                    {
                        NextCursor = "2",
                        Tools = new List<ToolDefinition> { new ToolDefinition { Name = "t", Description = "d" } }
                    });
                    TestAssert.Equal("2", json.GetProperty("nextCursor").GetString());
                    TestAssert.Equal("t", json.GetProperty("tools")[0].GetProperty("name").GetString());
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ListResourcesResult", "McpListResourcesResult serializes resources and cursor", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpListResourcesResult
                    {
                        NextCursor = "2",
                        Resources = new List<McpResource> { new McpResource { Uri = "voltaic://r", Name = "r" } }
                    });
                    TestAssert.Equal("voltaic://r", json.GetProperty("resources")[0].GetProperty("uri").GetString());
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ListResourceTemplatesResult", "McpListResourceTemplatesResult serializes templates", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpListResourceTemplatesResult
                    {
                        ResourceTemplates = new List<McpResourceTemplate>
                        {
                            new McpResourceTemplate { UriTemplate = "voltaic://{id}", Name = "r" }
                        }
                    });
                    TestAssert.Equal("voltaic://{id}", json.GetProperty("resourceTemplates")[0].GetProperty("uriTemplate").GetString());
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ListPromptsResult", "McpListPromptsResult serializes prompts", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpListPromptsResult
                    {
                        Prompts = new List<McpPrompt> { new McpPrompt { Name = "p" } }
                    });
                    TestAssert.Equal("p", json.GetProperty("prompts")[0].GetProperty("name").GetString());
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ToolDefinitionOmittedOptionalFields", "ToolDefinition omits absent v0.3.0 metadata", ct =>
                {
                    string json = TestJson.Serialize(new ToolDefinition { Name = "t", Description = "d", InputSchema = new { type = "object" } });
                    TestAssert.False(json.Contains("\"title\""), "Title should be omitted.");
                    TestAssert.False(json.Contains("\"outputSchema\""), "Output schema should be omitted.");
                    TestAssert.False(json.Contains("\"annotations\""), "Annotations should be omitted.");
                    TestAssert.False(json.Contains("\"icons\""), "Icons should be omitted.");
                    TestAssert.False(json.Contains("\"_meta\""), "Metadata should be omitted.");
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ToolCallResultFromTextNull", "McpToolCallResult.FromText converts null to empty text", ct =>
                {
                    McpToolCallResult result = McpToolCallResult.FromText(null);
                    JsonElement json = TestJson.SerializeToElement(result);
                    TestAssert.Equal(String.Empty, json.GetProperty("content")[0].GetProperty("text").GetString());
                    TestAssert.False(json.TryGetProperty("structuredContent", out _), "Structured content should be omitted.");
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ToolCallResultErrorFlag", "McpToolCallResult serializes isError", ct =>
                {
                    JsonElement json = TestJson.SerializeToElement(new McpToolCallResult
                    {
                        IsError = true,
                        Content = new List<object> { new McpTextContent { Text = "failed" } }
                    });
                    TestAssert.True(json.GetProperty("isError").GetBoolean(), "isError should serialize.");
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ProtocolExceptionInvalidParams", "McpProtocolException.InvalidParams converts to JSON-RPC error", ct =>
                {
                    McpProtocolException exception = McpProtocolException.InvalidParams("bad", new { field = "name" });
                    JsonRpcError error = exception.ToJsonRpcError();
                    TestAssert.Equal(-32602, exception.Code);
                    TestAssert.Equal(-32602, error.Code);
                    TestAssert.Equal("bad", error.Message);
                    JsonElement data = TestJson.SerializeToElement(error.Data!);
                    TestAssert.Equal("name", data.GetProperty("field").GetString());
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ProtocolExceptionMethodNotFound", "McpProtocolException.MethodNotFound converts to JSON-RPC error", ct =>
                {
                    JsonRpcError error = McpProtocolException.MethodNotFound("missing").ToJsonRpcError();
                    TestAssert.Equal(-32601, error.Code);
                    TestAssert.Equal("missing", error.Message);
                    return Task.CompletedTask;
                }),

                Case(suiteId, "AuthenticationResultCustomValues", "AuthenticationResult preserves custom auth data", ct =>
                {
                    AuthenticationResult result = new AuthenticationResult
                    {
                        IsAuthenticated = true,
                        Principal = "user",
                        Claims = new Dictionary<string, string> { ["scope"] = "read" },
                        StatusCode = 403,
                        ErrorMessage = "forbidden"
                    };
                    TestAssert.True(result.IsAuthenticated, "Authentication flag should be settable.");
                    TestAssert.Equal("user", result.Principal);
                    TestAssert.Equal("read", result.Claims!["scope"]);
                    TestAssert.Equal(403, result.StatusCode);
                    TestAssert.Equal("forbidden", result.ErrorMessage);
                    return Task.CompletedTask;
                }),

                Case(suiteId, "ClientConnectionTypeEnumAllValues", "ClientConnectionTypeEnum exposes every supported connection kind", ct =>
                {
                    ClientConnectionTypeEnum[] values = Enum.GetValues<ClientConnectionTypeEnum>();
                    TestAssert.True(values.Contains(ClientConnectionTypeEnum.Stdio), "Stdio should be present.");
                    TestAssert.True(values.Contains(ClientConnectionTypeEnum.Tcp), "Tcp should be present.");
                    TestAssert.True(values.Contains(ClientConnectionTypeEnum.Http), "Http should be present.");
                    TestAssert.True(values.Contains(ClientConnectionTypeEnum.Websockets), "Websockets should be present.");
                    TestAssert.Equal(4, values.Length);
                    return Task.CompletedTask;
                }),
            };

            return new TestSuiteDescriptor(suiteId, "MCP Model Serialization Matrix", cases);
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "api", "model", "matrix" });
        }
    }
}
