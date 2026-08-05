namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Positive and negative coverage for the multi-version MCP foundation: the version registry,
    /// the version/era resolver, the protocol error factories, and the additive protocol models
    /// (discovery, MRTR, and both task shapes).
    /// </summary>
    public static class McpVersionSuites
    {
        /// <summary>
        /// Registry and resolver behavior, in both the positive and negative direction.
        /// </summary>
        /// <returns>A test suite descriptor.</returns>
        public static TestSuiteDescriptor VersionRegistryAndResolver()
        {
            const string suiteId = "McpVersion.Resolver";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP Version Registry and Resolver",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "RegistryContainsAllVersions", "Registry lists all five supported revisions", ct =>
                    {
                        IReadOnlyList<string> versions = McpProtocol.SupportedVersionStrings();
                        TestAssert.Equal(5, versions.Count, "Five revisions should be supported.");
                        TestAssert.True(versions.Contains("2024-11-05"), "2024-11-05 should be supported.");
                        TestAssert.True(versions.Contains("2025-03-26"), "2025-03-26 should be supported.");
                        TestAssert.True(versions.Contains("2025-06-18"), "2025-06-18 should be supported.");
                        TestAssert.True(versions.Contains("2025-11-25"), "2025-11-25 should be supported.");
                        TestAssert.True(versions.Contains("2026-07-28"), "2026-07-28 should be supported.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "RegistryErasAndFlags", "Registry records correct era and transport flags", ct =>
                    {
                        TestAssert.Equal(McpProtocolEra.Handshake, McpProtocol.GetEra("2024-11-05"), "2024-11-05 is handshake.");
                        TestAssert.Equal(McpProtocolEra.Handshake, McpProtocol.GetEra("2025-11-25"), "2025-11-25 is handshake.");
                        TestAssert.Equal(McpProtocolEra.Stateless, McpProtocol.GetEra("2026-07-28"), "2026-07-28 is stateless.");

                        McpProtocolVersionInfo v20241105 = McpProtocol.GetVersionInfo("2024-11-05")!;
                        TestAssert.True(v20241105.SupportsBatching, "2024-11-05 allows batching.");
                        TestAssert.False(v20241105.SupportsSessions, "2024-11-05 HTTP+SSE has no sessions.");

                        McpProtocolVersionInfo v20250618 = McpProtocol.GetVersionInfo("2025-06-18")!;
                        TestAssert.False(v20250618.SupportsBatching, "2025-06-18 removed batching.");
                        TestAssert.True(v20250618.RequiresProtocolVersionHeader, "2025-06-18 requires the version header.");

                        McpProtocolVersionInfo v20260728 = McpProtocol.GetVersionInfo("2026-07-28")!;
                        TestAssert.True(v20260728.UsesHeaderRouting, "2026-07-28 uses header routing.");
                        TestAssert.False(v20260728.SupportsSessions, "2026-07-28 is sessionless.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "NewestConstant", "Newest constant is 2026-07-28 while default handshake stays 2025-11-25", ct =>
                    {
                        TestAssert.Equal("2026-07-28", McpProtocol.NewestProtocolVersion, "Newest revision is 2026-07-28.");
                        TestAssert.Equal("2025-11-25", McpProtocol.LatestProtocolVersion, "Default handshake stays 2025-11-25 for compatibility.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ResolveHeaderWinsOverMeta", "Header value takes precedence when it matches meta", ct =>
                    {
                        McpResolvedVersion resolved = McpVersionResolver.Resolve("2025-11-25", "2025-11-25", "tools/call", false, false);
                        TestAssert.Equal("2025-11-25", resolved.Version, "Header value should be chosen.");
                        TestAssert.Equal(McpProtocolEra.Handshake, resolved.Era, "2025-11-25 is handshake.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ResolveMetaWhenNoHeader", "Body meta version is used when no header is present", ct =>
                    {
                        McpResolvedVersion resolved = McpVersionResolver.Resolve(null, "2026-07-28", "tools/call", false, true);
                        TestAssert.Equal("2026-07-28", resolved.Version, "Meta value should be chosen.");
                        TestAssert.Equal(McpProtocolEra.Stateless, resolved.Era, "2026-07-28 is stateless.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ResolveInitializeIsHandshake", "An initialize method resolves to the handshake default", ct =>
                    {
                        McpResolvedVersion resolved = McpVersionResolver.Resolve(null, null, "initialize", false, false);
                        TestAssert.Equal("2025-11-25", resolved.Version, "initialize should default to the handshake version.");
                        TestAssert.Equal(McpProtocolEra.Handshake, resolved.Era, "initialize is handshake era.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ResolveRoutingHeadersAreStateless", "Stateless routing headers resolve to 2026-07-28", ct =>
                    {
                        McpResolvedVersion resolved = McpVersionResolver.Resolve(null, null, "tools/call", false, true);
                        TestAssert.Equal("2026-07-28", resolved.Version, "Routing headers imply the stateless revision.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ResolveDefaultFallback", "No cues fall back to the handshake default", ct =>
                    {
                        McpResolvedVersion resolved = McpVersionResolver.Resolve(null, null, null, false, false);
                        TestAssert.Equal("2025-11-25", resolved.Version, "Default fallback is the handshake version.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ResolveHeaderMetaMismatchRejected", "Disagreeing header and meta versions raise -32020", ct =>
                    {
                        McpProtocolException? caught = ExpectProtocolException(
                            () => McpVersionResolver.Resolve("2026-07-28", "2025-11-25", "tools/call", false, true));
                        TestAssert.NotNull(caught, "A mismatch should throw.");
                        TestAssert.Equal(-32020, caught!.Code, "Mismatch should map to HeaderMismatch (-32020).");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ResolveUnsupportedVersionRejected", "An unknown explicit version raises -32022 with the supported list", ct =>
                    {
                        McpProtocolException? caught = ExpectProtocolException(
                            () => McpVersionResolver.Resolve("1999-01-01", null, "tools/call", false, true));
                        TestAssert.NotNull(caught, "An unsupported version should throw.");
                        TestAssert.Equal(-32022, caught!.Code, "Unsupported version should map to -32022.");
                        string data = JsonSerializer.Serialize(caught.ErrorData);
                        TestAssert.True(data.Contains("\"requested\":\"1999-01-01\""), "Error data should echo the requested version.");
                        TestAssert.True(data.Contains("2026-07-28"), "Error data should list supported versions.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "IsSupportedNegatives", "Null, blank, and unknown versions are not supported", ct =>
                    {
                        TestAssert.False(McpProtocol.IsSupportedVersion(null), "Null is unsupported.");
                        TestAssert.False(McpProtocol.IsSupportedVersion(""), "Blank is unsupported.");
                        TestAssert.False(McpProtocol.IsSupportedVersion("2024-01-01"), "Unknown is unsupported.");
                        TestAssert.Throws<ArgumentException>(() => McpProtocol.GetEra("nope"), "GetEra should reject unknown versions.");
                        return Task.CompletedTask;
                    }),
                });
        }

        /// <summary>
        /// Serialization coverage for the additive discovery, MRTR, and task models, plus the
        /// stateless-era error factories.
        /// </summary>
        /// <returns>A test suite descriptor.</returns>
        public static TestSuiteDescriptor VersionModelsAndErrors()
        {
            const string suiteId = "McpVersion.Models";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP Version Models and Errors",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "DiscoverResultSerialization", "Discover result serializes discovery fields", ct =>
                    {
                        McpDiscoverResult discover = new McpDiscoverResult
                        {
                            SupportedVersions = new List<string> { "2026-07-28" },
                            Capabilities = new McpServerCapabilities
                            {
                                Extensions = new Dictionary<string, object> { { McpProtocol.TasksExtensionId, new { } } }
                            },
                            Instructions = "Use responsibly.",
                            TtlMs = 3600000,
                            CacheScope = "public"
                        };

                        string json = JsonSerializer.Serialize(discover);
                        TestAssert.True(json.Contains("\"resultType\":\"complete\""), "Discovery uses resultType complete.");
                        TestAssert.True(json.Contains("\"supportedVersions\""), "Supported versions should serialize.");
                        TestAssert.True(json.Contains("\"ttlMs\":3600000"), "ttlMs should serialize.");
                        TestAssert.True(json.Contains("\"cacheScope\":\"public\""), "cacheScope should serialize.");
                        TestAssert.True(json.Contains("io.modelcontextprotocol/tasks"), "Extensions should serialize.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "InputRequiredSerialization", "Input-required result serializes MRTR fields", ct =>
                    {
                        McpInputRequiredResult result = new McpInputRequiredResult
                        {
                            InputRequests = new Dictionary<string, McpInputRequest>
                            {
                                { "github_login", new McpInputRequest { Method = "elicitation/create", Params = new { message = "name?" } } }
                            },
                            RequestState = "opaque-blob"
                        };

                        string json = JsonSerializer.Serialize(result);
                        TestAssert.True(json.Contains("\"resultType\":\"input_required\""), "resultType should be input_required.");
                        TestAssert.True(json.Contains("\"inputRequests\""), "inputRequests should serialize.");
                        TestAssert.True(json.Contains("elicitation/create"), "The request method should serialize.");
                        TestAssert.True(json.Contains("\"requestState\":\"opaque-blob\""), "requestState should serialize.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "TasksExtensionSerialization", "2026-07-28 task uses ttlMs and emits null ttlMs", ct =>
                    {
                        McpCreateTaskResult create = new McpCreateTaskResult
                        {
                            TaskId = "abc",
                            Status = McpTaskStatus.Working,
                            TtlMs = null,
                            PollIntervalMs = 5000
                        };

                        string json = JsonSerializer.Serialize(create);
                        TestAssert.True(json.Contains("\"resultType\":\"task\""), "CreateTaskResult uses resultType task.");
                        TestAssert.True(json.Contains("\"ttlMs\":null"), "ttlMs must serialize even when null.");
                        TestAssert.True(json.Contains("\"pollIntervalMs\":5000"), "pollIntervalMs should serialize.");
                        TestAssert.True(json.Contains("\"status\":\"working\""), "status should serialize.");

                        McpTaskAck ack = new McpTaskAck();
                        TestAssert.True(JsonSerializer.Serialize(ack).Contains("\"resultType\":\"complete\""), "Ack uses resultType complete.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "InCoreTaskSerialization", "2025-11-25 task uses ttl and pollInterval", ct =>
                    {
                        McpInCoreTask task = new McpInCoreTask
                        {
                            TaskId = "t1",
                            Status = McpTaskStatus.Working,
                            CreatedAt = "2025-11-25T10:30:00Z",
                            LastUpdatedAt = "2025-11-25T10:31:00Z",
                            Ttl = 60000,
                            PollInterval = 5000
                        };

                        string json = JsonSerializer.Serialize(task);
                        TestAssert.True(json.Contains("\"ttl\":60000"), "In-core task uses ttl, not ttlMs.");
                        TestAssert.True(json.Contains("\"pollInterval\":5000"), "In-core task uses pollInterval.");
                        TestAssert.True(json.Contains("\"createdAt\""), "createdAt should serialize.");
                        TestAssert.False(json.Contains("ttlMs"), "In-core task must not use ttlMs.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "CacheFieldsOnListAndRead", "Cache fields serialize on list and read results", ct =>
                    {
                        McpListToolsResult list = new McpListToolsResult { TtlMs = 1000, CacheScope = "private" };
                        string listJson = JsonSerializer.Serialize(list);
                        TestAssert.True(listJson.Contains("\"ttlMs\":1000"), "List result should carry ttlMs.");
                        TestAssert.True(listJson.Contains("\"cacheScope\":\"private\""), "List result should carry cacheScope.");

                        McpReadResourceResult read = new McpReadResourceResult { TtlMs = 2000, CacheScope = "public" };
                        string readJson = JsonSerializer.Serialize(read);
                        TestAssert.True(readJson.Contains("\"ttlMs\":2000"), "Read result should carry ttlMs.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ErrorFactoryCodes", "Stateless error factories carry the confirmed codes and data", ct =>
                    {
                        TestAssert.Equal(-32020, McpProtocolException.HeaderMismatch("x").Code, "HeaderMismatch is -32020.");
                        TestAssert.Equal(-32021, McpProtocolException.MissingRequiredClientCapability("x").Code, "MissingRequiredClientCapability is -32021.");

                        McpProtocolException unsupported = McpProtocolException.UnsupportedProtocolVersion("1999-01-01", McpProtocol.SupportedVersionStrings());
                        TestAssert.Equal(-32022, unsupported.Code, "UnsupportedProtocolVersion is -32022.");

                        McpProtocolException missing = McpProtocolException.MissingRequiredClientCapability("need elicitation", new { elicitation = new { } });
                        string data = JsonSerializer.Serialize(missing.ErrorData);
                        TestAssert.True(data.Contains("requiredCapabilities"), "Missing-capability data should wrap requiredCapabilities.");
                        return Task.CompletedTask;
                    }),
                });
        }

        /// <summary>
        /// End-to-end coverage of the <c>server/discover</c> RPC and cacheable list results over
        /// the HTTP transport.
        /// </summary>
        /// <returns>A test suite descriptor.</returns>
        public static TestSuiteDescriptor DiscoveryAndCaching()
        {
            const string suiteId = "McpVersion.Discovery";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP server/discover and cacheable results",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "DiscoverReturnsVersionsAndExtensions", "server/discover reports versions, extensions, and identity", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.ServerInstructions = "Weather utilities.";
                            server.AdvertiseTasksExtension = true;
                            server.RegisterTool("noop", "Does nothing", new { type = "object", properties = new { }, required = new string[] { } }, (_) => "ok");
                        }).ConfigureAwait(false);

                        RpcResult response = await fixture.PostMcpAsync("server/discover", new { }, "d1", null, ct).ConfigureAwait(false);
                        using JsonDocument json = JsonDocument.Parse(response.Body);
                        JsonElement result = json.RootElement.GetProperty("result");

                        TestAssert.Equal("complete", result.GetProperty("resultType").GetString(), "Discovery uses resultType complete.");
                        TestAssert.Equal("Weather utilities.", result.GetProperty("instructions").GetString(), "Instructions should be returned.");

                        bool sawNewest = false;
                        bool sawOldest = false;
                        foreach (JsonElement version in result.GetProperty("supportedVersions").EnumerateArray())
                        {
                            if (version.GetString() == "2026-07-28") sawNewest = true;
                            if (version.GetString() == "2024-11-05") sawOldest = true;
                        }
                        TestAssert.True(sawNewest, "Discovery should advertise 2026-07-28.");
                        TestAssert.True(sawOldest, "Discovery should advertise 2024-11-05.");

                        JsonElement extensions = result.GetProperty("capabilities").GetProperty("extensions");
                        TestAssert.True(extensions.TryGetProperty("io.modelcontextprotocol/tasks", out _), "Tasks extension should be advertised.");

                        JsonElement serverInfo = result.GetProperty("_meta").GetProperty("io.modelcontextprotocol/serverInfo");
                        TestAssert.Equal("Voltaic.Test", serverInfo.GetProperty("name").GetString(), "Server identity should be present.");
                    }),

                    Case(suiteId, "ListResultsCarryCacheHints", "tools/list carries ttlMs and cacheScope when configured", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.ListCacheTtlMs = 60000;
                            server.ListCacheScope = "public";
                            server.RegisterTool("noop", "Does nothing", new { type = "object", properties = new { }, required = new string[] { } }, (_) => "ok");
                        }).ConfigureAwait(false);

                        RpcResult response = await fixture.PostMcpAsync("tools/list", new { }, "l1", null, ct).ConfigureAwait(false);
                        using JsonDocument json = JsonDocument.Parse(response.Body);
                        JsonElement result = json.RootElement.GetProperty("result");

                        TestAssert.Equal(60000, result.GetProperty("ttlMs").GetInt64(), "List result should carry ttlMs.");
                        TestAssert.Equal("public", result.GetProperty("cacheScope").GetString(), "List result should carry cacheScope.");
                    }),
                });
        }

        private static McpProtocolException? ExpectProtocolException(Action action)
        {
            try
            {
                action();
            }
            catch (McpProtocolException ex)
            {
                return ex;
            }

            return null;
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "api", "mcp", "model", "version" });
        }
    }
}
