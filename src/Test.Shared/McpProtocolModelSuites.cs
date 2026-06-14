namespace Test.Shared
{
    using System.Text.Json;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;
    public static class McpProtocolModelSuites
    {
        public static TestSuiteDescriptor ProtocolAndCapabilities()
        {
            const string suiteId = "McpProtocol.Models";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP Protocol and Capability Models",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "ProtocolConstants", "McpProtocol exposes current constants", ct =>
                    {
                        TestAssert.Equal("2025-11-25", McpProtocol.LatestProtocolVersion);
                        TestAssert.Equal("2025-03-26", McpProtocol.ProtocolVersion20250326);
                        TestAssert.Equal("2.0", McpProtocol.JsonRpcVersion);
                        TestAssert.Equal("MCP-Session-Id", McpProtocol.SessionIdHeader);
                        TestAssert.Equal("Mcp-Session-Id", McpProtocol.LegacySessionIdHeader);
                        TestAssert.Equal("MCP-Protocol-Version", McpProtocol.ProtocolVersionHeader);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ProtocolNegotiation", "McpProtocol negotiates supported versions", ct =>
                    {
                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, McpProtocol.NegotiateVersion(null));
                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, McpProtocol.NegotiateVersion(""));
                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, McpProtocol.NegotiateVersion(McpProtocol.LatestProtocolVersion));
                        TestAssert.Equal(McpProtocol.ProtocolVersion20250326, McpProtocol.NegotiateVersion(McpProtocol.ProtocolVersion20250326));
                        TestAssert.Throws<ArgumentException>(() => McpProtocol.NegotiateVersion("1900-01-01"), "Unsupported protocol should fail.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ImplementationSerialization", "McpImplementation serializes MCP field names", ct =>
                    {
                        McpImplementation implementation = new McpImplementation
                        {
                            Name = "server",
                            Title = "Server",
                            Version = "1.0.0",
                            Description = "Demo",
                            WebsiteUrl = "https://example.com",
                            Icons = new List<McpIcon>
                            {
                                new McpIcon
                                {
                                    Src = "https://example.com/icon.png",
                                    MimeType = "image/png",
                                    Sizes = new List<string> { "48x48" },
                                    Theme = "light"
                                }
                            }
                        };

                        string json = JsonSerializer.Serialize(implementation);
                        TestAssert.True(json.Contains("\"name\":\"server\""), "Name should be lower-case JSON.");
                        TestAssert.True(json.Contains("\"websiteUrl\":\"https://example.com\""), "Website URL should use MCP field name.");
                        TestAssert.True(json.Contains("\"icons\""), "Icons should serialize.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "CapabilitiesSerialization", "Capability models omit nulls and include enabled features", ct =>
                    {
                        McpServerCapabilities capabilities = new McpServerCapabilities
                        {
                            Tools = new McpListChangedCapability { ListChanged = true },
                            Resources = new McpResourceCapability { ListChanged = true, Subscribe = true },
                            Prompts = new McpListChangedCapability { ListChanged = false },
                            Logging = new { },
                            Completions = new { }
                        };

                        string json = JsonSerializer.Serialize(capabilities);
                        TestAssert.True(json.Contains("\"tools\""), "Tools capability should serialize.");
                        TestAssert.True(json.Contains("\"resources\""), "Resources capability should serialize.");
                        TestAssert.True(json.Contains("\"subscribe\":true"), "Subscribe capability should serialize.");
                        TestAssert.True(json.Contains("\"logging\""), "Logging capability should serialize.");
                        TestAssert.True(json.Contains("\"completions\""), "Completion capability should serialize.");
                        return Task.CompletedTask;
                    }),
                });
        }

        public static TestSuiteDescriptor ContentResourcesPromptsAndTools()
        {
            const string suiteId = "McpProtocol.Content";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP Content, Resource, Prompt, and Tool Models",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "ContentSerialization", "Content models serialize type discriminators", ct =>
                    {
                        string text = JsonSerializer.Serialize(new McpTextContent { Text = "hello" });
                        string image = JsonSerializer.Serialize(new McpImageContent { Data = "aGVsbG8=", MimeType = "image/png" });
                        string audio = JsonSerializer.Serialize(new McpAudioContent { Data = "aGVsbG8=", MimeType = "audio/wav" });
                        string embedded = JsonSerializer.Serialize(new McpEmbeddedResourceContent
                        {
                            Resource = new McpTextResourceContents { Uri = "voltaic://embedded", Text = "embedded" }
                        });
                        string link = JsonSerializer.Serialize(new McpResourceLinkContent { Uri = "voltaic://linked", Name = "linked" });

                        TestAssert.True(text.Contains("\"type\":\"text\""), "Text content type should serialize.");
                        TestAssert.True(image.Contains("\"type\":\"image\""), "Image content type should serialize.");
                        TestAssert.True(audio.Contains("\"type\":\"audio\""), "Audio content type should serialize.");
                        TestAssert.True(embedded.Contains("\"type\":\"resource\""), "Embedded resource content type should serialize.");
                        TestAssert.True(link.Contains("\"type\":\"resource_link\""), "Resource link content type should serialize.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ResourceSerialization", "Resource models serialize MCP field names", ct =>
                    {
                        McpResource resource = new McpResource
                        {
                            Uri = "file:///tmp/readme.md",
                            Name = "readme",
                            Title = "Readme",
                            Description = "Project readme",
                            MimeType = "text/markdown",
                            Size = 123
                        };
                        McpResourceTemplate template = new McpResourceTemplate
                        {
                            UriTemplate = "file:///{path}",
                            Name = "file",
                            MimeType = "text/plain"
                        };
                        McpReadResourceResult result = new McpReadResourceResult
                        {
                            Contents = new List<object>
                            {
                                new McpTextResourceContents { Uri = resource.Uri, MimeType = "text/plain", Text = "hello" },
                                new McpBlobResourceContents { Uri = "file:///tmp/blob.bin", MimeType = "application/octet-stream", Blob = "AA==" }
                            }
                        };

                        string json = JsonSerializer.Serialize(new { resource, template, result });
                        TestAssert.True(json.Contains("\"uri\":\"file:///tmp/readme.md\""), "Resource URI should serialize.");
                        TestAssert.True(json.Contains("\"uriTemplate\":\"file:///{path}\""), "Template URI should serialize.");
                        TestAssert.True(json.Contains("\"contents\""), "Read result contents should serialize.");
                        TestAssert.True(json.Contains("\"blob\":\"AA==\""), "Blob contents should serialize.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "PromptSerialization", "Prompt models serialize arguments and messages", ct =>
                    {
                        McpPrompt prompt = new McpPrompt
                        {
                            Name = "summarize",
                            Description = "Summarize a document",
                            Arguments = new List<McpPromptArgument>
                            {
                                new McpPromptArgument { Name = "document", Required = true }
                            }
                        };
                        McpGetPromptResult result = new McpGetPromptResult
                        {
                            Description = "Summary prompt",
                            Messages = new List<McpPromptMessage>
                            {
                                new McpPromptMessage { Role = "user", Content = new McpTextContent { Text = "Summarize this" } }
                            }
                        };

                        string json = JsonSerializer.Serialize(new { prompt, result });
                        TestAssert.True(json.Contains("\"arguments\""), "Prompt arguments should serialize.");
                        TestAssert.True(json.Contains("\"required\":true"), "Required prompt argument should serialize.");
                        TestAssert.True(json.Contains("\"messages\""), "Prompt result messages should serialize.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ToolDefinitionSerialization", "ToolDefinition supports v0.3.0 metadata", ct =>
                    {
                        ToolDefinition tool = new ToolDefinition
                        {
                            Name = "add",
                            Title = "Add",
                            Description = "Adds numbers",
                            InputSchema = new { type = "object" },
                            OutputSchema = new { type = "object" },
                            Annotations = new McpAnnotations { ReadOnlyHint = true },
                            Icons = new List<McpIcon> { new McpIcon { Src = "data:image/png;base64,AA==" } },
                            Meta = new Dictionary<string, object?> { ["source"] = "test" }
                        };

                        string json = JsonSerializer.Serialize(tool);
                        TestAssert.True(json.Contains("\"name\":\"add\""), "Name should use MCP JSON casing.");
                        TestAssert.True(json.Contains("\"outputSchema\""), "Output schema should serialize.");
                        TestAssert.True(json.Contains("\"readOnlyHint\":true"), "Annotations should serialize.");
                        TestAssert.True(json.Contains("\"_meta\""), "Metadata should serialize.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ToolCallResultFactories", "McpToolCallResult factories create valid result shapes", ct =>
                    {
                        McpToolCallResult text = McpToolCallResult.FromText("hello");
                        McpToolCallResult structured = McpToolCallResult.FromStructured(new { answer = 42 });

                        string textJson = JsonSerializer.Serialize(text);
                        string structuredJson = JsonSerializer.Serialize(structured);

                        TestAssert.True(textJson.Contains("\"content\""), "Text result should include content.");
                        TestAssert.True(textJson.Contains("\"text\":\"hello\""), "Text result should include text.");
                        TestAssert.True(structuredJson.Contains("\"structuredContent\""), "Structured result should include structuredContent.");
                        TestAssert.True(structuredJson.Contains("\"answer\":42"), "Structured content should include payload.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "CompletionAndUtilitySerialization", "Completion and utility models serialize MCP field names", ct =>
                    {
                        McpCompleteRequest request = new McpCompleteRequest
                        {
                            Ref = new McpCompletionReference { Type = "ref/prompt", Name = "review" },
                            Argument = new McpCompletionArgument { Name = "language", Value = "py" },
                            Context = new McpCompletionContext
                            {
                                Arguments = new Dictionary<string, string> { ["framework"] = "pytest" }
                            }
                        };
                        McpCompleteResult result = new McpCompleteResult
                        {
                            Completion = new McpCompletion
                            {
                                Values = new List<string> { "python" },
                                Total = 1,
                                HasMore = false
                            }
                        };
                        McpProgressNotification progress = new McpProgressNotification
                        {
                            ProgressToken = "abc",
                            Progress = 50,
                            Total = 100,
                            Message = "Halfway"
                        };
                        McpLogMessageNotification log = new McpLogMessageNotification
                        {
                            Level = "info",
                            Logger = "tests",
                            Data = new { ok = true }
                        };

                        string json = JsonSerializer.Serialize(new { request, result, progress, log });
                        TestAssert.True(json.Contains("\"ref\""), "Completion request should use ref field.");
                        TestAssert.True(json.Contains("\"completion\""), "Completion result should serialize.");
                        TestAssert.True(json.Contains("\"progressToken\":\"abc\""), "Progress token should serialize.");
                        TestAssert.True(json.Contains("\"logger\":\"tests\""), "Logger should serialize.");
                        return Task.CompletedTask;
                    }),
                });
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "api", "mcp", "model" });
        }
    }
}
