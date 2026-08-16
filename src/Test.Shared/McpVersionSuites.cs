namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
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

                    Case(suiteId, "RegistryFlagsForRemainingRevisions", "Registry records transport flags for 2025-03-26 and 2025-11-25", ct =>
                    {
                        McpProtocolVersionInfo v20250326 = McpProtocol.GetVersionInfo("2025-03-26")!;
                        TestAssert.Equal(McpProtocolEra.Handshake, v20250326.Era, "2025-03-26 is handshake.");
                        TestAssert.True(v20250326.SupportsBatching, "2025-03-26 still allows batching.");
                        TestAssert.True(v20250326.SupportsSessions, "2025-03-26 Streamable HTTP uses sessions.");
                        TestAssert.False(v20250326.UsesHeaderRouting, "2025-03-26 does not use stateless header routing.");

                        McpProtocolVersionInfo v20251125 = McpProtocol.GetVersionInfo("2025-11-25")!;
                        TestAssert.Equal(McpProtocolEra.Handshake, v20251125.Era, "2025-11-25 is handshake.");
                        TestAssert.False(v20251125.SupportsBatching, "2025-11-25 does not allow batching.");
                        TestAssert.True(v20251125.RequiresProtocolVersionHeader, "2025-11-25 requires the version header.");
                        TestAssert.False(v20251125.UsesHeaderRouting, "2025-11-25 does not use stateless header routing.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "GetVersionInfoNullAndBlank", "GetVersionInfo returns null for null, blank, and unknown versions", ct =>
                    {
                        TestAssert.Null(McpProtocol.GetVersionInfo(null), "Null version has no registry entry.");
                        TestAssert.Null(McpProtocol.GetVersionInfo(""), "Blank version has no registry entry.");
                        TestAssert.Null(McpProtocol.GetVersionInfo("   "), "Whitespace version has no registry entry.");
                        TestAssert.Null(McpProtocol.GetVersionInfo("2024-01-01"), "Unknown version has no registry entry.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "VersionInfoRejectsNullOrEmptyVersion", "McpProtocolVersionInfo rejects a null or empty version string", ct =>
                    {
                        TestAssert.Throws<ArgumentNullException>(
                            () => new McpProtocolVersionInfo(null!, McpProtocolEra.Handshake, false, false, false, false),
                            "A null version should be rejected.");
                        TestAssert.Throws<ArgumentNullException>(
                            () => new McpProtocolVersionInfo(string.Empty, McpProtocolEra.Handshake, false, false, false, false),
                            "An empty version should be rejected.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ForVersionPositiveAndNegative", "ForVersion resolves a supported version and rejects an unsupported one", ct =>
                    {
                        McpResolvedVersion resolved = McpVersionResolver.ForVersion("2026-07-28");
                        TestAssert.Equal("2026-07-28", resolved.Version, "ForVersion should echo the supported version.");
                        TestAssert.Equal(McpProtocolEra.Stateless, resolved.Era, "2026-07-28 resolves to the stateless era.");

                        TestAssert.Throws<ArgumentException>(
                            () => McpVersionResolver.ForVersion("1999-01-01"),
                            "ForVersion should reject an unsupported version.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ResolveSessionHeaderIsHandshake", "A session-id header alone resolves to the handshake default", ct =>
                    {
                        McpResolvedVersion resolved = McpVersionResolver.Resolve(null, null, null, true, false);
                        TestAssert.Equal("2025-11-25", resolved.Version, "A session header implies the handshake version.");
                        TestAssert.Equal(McpProtocolEra.Handshake, resolved.Era, "A session header implies the handshake era.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ResolveInitializedNotificationIsHandshake", "A notifications/initialized method resolves to the handshake default", ct =>
                    {
                        McpResolvedVersion resolved = McpVersionResolver.Resolve(null, null, "notifications/initialized", false, false);
                        TestAssert.Equal("2025-11-25", resolved.Version, "notifications/initialized should default to the handshake version.");
                        TestAssert.Equal(McpProtocolEra.Handshake, resolved.Era, "notifications/initialized is handshake era.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ResolveMetaOnlyUnsupportedRejected", "An unsupported version carried only in body meta raises -32022", ct =>
                    {
                        McpProtocolException? caught = ExpectProtocolException(
                            () => McpVersionResolver.Resolve(null, "1999-01-01", "tools/call", false, true));
                        TestAssert.NotNull(caught, "An unsupported meta version should throw.");
                        TestAssert.Equal(-32022, caught!.Code, "Unsupported meta version should map to -32022.");
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

                    Case(suiteId, "ErrorFactoryNullDataAndMapping", "Error factories omit null data, empty supported lists, and map to JSON-RPC errors", ct =>
                    {
                        // MissingRequiredClientCapability with no capabilities carries no data payload.
                        McpProtocolException missingNoData = McpProtocolException.MissingRequiredClientCapability("nope");
                        TestAssert.Null(missingNoData.ErrorData, "Missing-capability data should be null when no capabilities are supplied.");

                        // UnsupportedProtocolVersion tolerates a null supported list by emitting an empty array.
                        McpProtocolException unsupportedNull = McpProtocolException.UnsupportedProtocolVersion("2000-01-01", null!);
                        string unsupportedData = JsonSerializer.Serialize(unsupportedNull.ErrorData);
                        TestAssert.True(unsupportedData.Contains("\"supported\":[]"), "A null supported list should serialize as an empty array.");
                        TestAssert.True(unsupportedData.Contains("\"requested\":\"2000-01-01\""), "Error data should echo the requested version.");

                        // ToJsonRpcError propagates code, message, and data.
                        JsonRpcError mismatchError = McpProtocolException.HeaderMismatch("boom").ToJsonRpcError();
                        TestAssert.Equal(-32020, mismatchError.Code, "ToJsonRpcError should preserve the code.");
                        TestAssert.Equal("boom", mismatchError.Message, "ToJsonRpcError should preserve the message.");
                        TestAssert.Null(mismatchError.Data, "A header mismatch carries no data.");

                        JsonRpcError unsupportedError = unsupportedNull.ToJsonRpcError();
                        TestAssert.Equal(-32022, unsupportedError.Code, "ToJsonRpcError should preserve the -32022 code.");
                        TestAssert.NotNull(unsupportedError.Data, "The unsupported-version error should carry data.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "GeneralErrorFactoryCodes", "General MCP error factories carry their standard JSON-RPC codes", ct =>
                    {
                        TestAssert.Equal(-32602, McpProtocolException.InvalidParams("x").Code, "InvalidParams is -32602.");
                        TestAssert.Equal(-32602, McpProtocolException.ValidationError("x").Code, "ValidationError maps to invalid-params.");
                        TestAssert.Equal(-32602, McpProtocolException.UnsupportedVersion("2000-01-01").Code, "UnsupportedVersion maps to invalid-params.");
                        TestAssert.Equal(-32602, McpProtocolException.InvalidCursor("c").Code, "InvalidCursor is -32602.");
                        TestAssert.Equal(-32602, McpProtocolException.InvalidSession("s").Code, "InvalidSession is -32602.");
                        TestAssert.Equal(-32601, McpProtocolException.MethodNotFound("m").Code, "MethodNotFound is -32601.");
                        TestAssert.Equal(-32800, McpProtocolException.CancelledRequest().Code, "CancelledRequest is -32800.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "DiscoverResultOmitsOptionalFields", "A minimal discover result omits capabilities, instructions, and cache hints", ct =>
                    {
                        McpDiscoverResult discover = new McpDiscoverResult
                        {
                            SupportedVersions = new List<string> { "2026-07-28" }
                        };

                        string json = JsonSerializer.Serialize(discover);
                        TestAssert.True(json.Contains("\"resultType\":\"complete\""), "resultType should still serialize.");
                        TestAssert.True(json.Contains("\"supportedVersions\""), "supportedVersions should serialize.");
                        TestAssert.False(json.Contains("capabilities"), "Null capabilities should be omitted.");
                        TestAssert.False(json.Contains("instructions"), "Null instructions should be omitted.");
                        TestAssert.False(json.Contains("ttlMs"), "Null ttlMs should be omitted.");
                        TestAssert.False(json.Contains("cacheScope"), "Null cacheScope should be omitted.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "InputRequiredOmitsNullFields", "An empty input-required result and null request params omit their optional fields", ct =>
                    {
                        McpInputRequiredResult empty = new McpInputRequiredResult();
                        string emptyJson = JsonSerializer.Serialize(empty);
                        TestAssert.True(emptyJson.Contains("\"resultType\":\"input_required\""), "resultType should always serialize.");
                        TestAssert.False(emptyJson.Contains("inputRequests"), "Null inputRequests should be omitted.");
                        TestAssert.False(emptyJson.Contains("requestState"), "Null requestState should be omitted.");

                        McpInputRequest request = new McpInputRequest { Method = "roots/list" };
                        string requestJson = JsonSerializer.Serialize(request);
                        TestAssert.True(requestJson.Contains("\"method\":\"roots/list\""), "The request method should serialize.");
                        TestAssert.False(requestJson.Contains("params"), "Null params should be omitted.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "TaskStatusVariantsSerialization", "Completed, failed, and input-required tasks serialize their status-specific payloads", ct =>
                    {
                        McpTask completed = new McpTask { TaskId = "c1", Status = McpTaskStatus.Completed, Result = new { content = "done" } };
                        string completedJson = JsonSerializer.Serialize(completed);
                        TestAssert.True(completedJson.Contains("\"status\":\"completed\""), "Completed status should serialize.");
                        TestAssert.True(completedJson.Contains("\"result\":"), "A completed task should carry its result.");
                        TestAssert.True(completedJson.Contains("\"ttlMs\":null"), "ttlMs must serialize even when null.");
                        TestAssert.False(completedJson.Contains("inputRequests"), "A completed task should omit inputRequests.");
                        TestAssert.False(completedJson.Contains("\"error\""), "A completed task should omit error.");

                        McpTask failed = new McpTask { TaskId = "f1", Status = McpTaskStatus.Failed, Error = new { code = -32000, message = "boom" } };
                        string failedJson = JsonSerializer.Serialize(failed);
                        TestAssert.True(failedJson.Contains("\"status\":\"failed\""), "Failed status should serialize.");
                        TestAssert.True(failedJson.Contains("\"error\":"), "A failed task should carry its error.");
                        TestAssert.False(failedJson.Contains("\"result\""), "A failed task should omit result.");

                        McpTask inputRequired = new McpTask
                        {
                            TaskId = "i1",
                            Status = McpTaskStatus.InputRequired,
                            InputRequests = new Dictionary<string, McpInputRequest>
                            {
                                { "login", new McpInputRequest { Method = "elicitation/create" } }
                            }
                        };
                        string inputJson = JsonSerializer.Serialize(inputRequired);
                        TestAssert.True(inputJson.Contains("\"status\":\"input_required\""), "Input-required status should serialize.");
                        TestAssert.True(inputJson.Contains("\"inputRequests\":"), "An input-required task should carry its input requests.");
                        TestAssert.False(inputJson.Contains("\"error\""), "An input-required task should omit error.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "InCoreTaskWrappersSerialization", "In-core task wrappers serialize the task, augmentation, and paginated list shapes", ct =>
                    {
                        McpCreateInCoreTaskResult create = new McpCreateInCoreTaskResult
                        {
                            Task = new McpInCoreTask { TaskId = "ic1", Status = McpTaskStatus.Working }
                        };
                        string createJson = JsonSerializer.Serialize(create);
                        TestAssert.True(createJson.Contains("\"task\":"), "Create result should wrap the task.");
                        TestAssert.True(createJson.Contains("\"taskId\":\"ic1\""), "The wrapped task id should serialize.");

                        McpTaskAugmentation augmentation = new McpTaskAugmentation { Ttl = 30000 };
                        TestAssert.True(JsonSerializer.Serialize(augmentation).Contains("\"ttl\":30000"), "Augmentation should serialize ttl.");
                        TestAssert.False(JsonSerializer.Serialize(new McpTaskAugmentation()).Contains("ttl"), "A null augmentation ttl should be omitted.");

                        McpListTasksResult list = new McpListTasksResult
                        {
                            NextCursor = "cursor-1",
                            Tasks = new List<McpInCoreTask> { new McpInCoreTask { TaskId = "ic2", Status = McpTaskStatus.Completed } }
                        };
                        string listJson = JsonSerializer.Serialize(list);
                        TestAssert.True(listJson.Contains("\"tasks\":["), "List result should carry the tasks array.");
                        TestAssert.True(listJson.Contains("\"nextCursor\":\"cursor-1\""), "List result should carry the paging cursor.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "UpdateTaskParamsSerialization", "tasks/update params serialize the task id and input responses", ct =>
                    {
                        McpUpdateTaskParams update = new McpUpdateTaskParams
                        {
                            TaskId = "t9",
                            InputResponses = new Dictionary<string, object?> { { "login", new { token = "abc" } } }
                        };

                        string json = JsonSerializer.Serialize(update);
                        TestAssert.True(json.Contains("\"taskId\":\"t9\""), "taskId should serialize.");
                        TestAssert.True(json.Contains("\"inputResponses\":"), "inputResponses should serialize.");
                        TestAssert.True(json.Contains("\"token\":\"abc\""), "The nested response payload should serialize.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ListResultsCacheFieldPresenceAndOmission", "Resource and prompt list results carry cache hints when set and omit them when null", ct =>
                    {
                        McpListPromptsResult withHints = new McpListPromptsResult
                        {
                            NextCursor = "n1",
                            TtlMs = 500,
                            CacheScope = "private"
                        };
                        string withHintsJson = JsonSerializer.Serialize(withHints);
                        TestAssert.True(withHintsJson.Contains("\"nextCursor\":\"n1\""), "nextCursor and cache hints should coexist.");
                        TestAssert.True(withHintsJson.Contains("\"ttlMs\":500"), "Prompt list should carry ttlMs.");
                        TestAssert.True(withHintsJson.Contains("\"cacheScope\":\"private\""), "Prompt list should carry cacheScope.");

                        McpListResourcesResult noHints = new McpListResourcesResult();
                        string noHintsJson = JsonSerializer.Serialize(noHints);
                        TestAssert.False(noHintsJson.Contains("ttlMs"), "Null ttlMs should be omitted from a list result.");
                        TestAssert.False(noHintsJson.Contains("cacheScope"), "Null cacheScope should be omitted from a list result.");
                        TestAssert.False(noHintsJson.Contains("nextCursor"), "Null nextCursor should be omitted from a list result.");
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
                        McpDiscoverResult? result = RpcResponseHelpers.ResultAs<McpDiscoverResult>(response.Body);
                        TestAssert.NotNull(result, "Discovery result should deserialize.");

                        TestAssert.Equal("complete", result!.ResultType, "Discovery uses resultType complete.");
                        TestAssert.Equal("Weather utilities.", result.Instructions, "Instructions should be returned.");
                        TestAssert.True(result.SupportedVersions.Contains("2026-07-28"), "Discovery should advertise 2026-07-28.");
                        TestAssert.True(result.SupportedVersions.Contains("2024-11-05"), "Discovery should advertise 2024-11-05.");

                        TestAssert.NotNull(result.Capabilities, "Capabilities should be present.");
                        TestAssert.NotNull(result.Capabilities!.Extensions, "Extensions should be present.");
                        TestAssert.True(result.Capabilities.Extensions!.ContainsKey("io.modelcontextprotocol/tasks"), "Tasks extension should be advertised.");

                        TestAssert.NotNull(result.Meta, "Server _meta should be present.");
                        McpImplementation? serverInfo = ServerInfoFrom(result.Meta!);
                        TestAssert.NotNull(serverInfo, "Server identity should be present.");
                        TestAssert.Equal("Voltaic.Test", serverInfo!.Name, "Server identity name should match.");
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
                        McpListToolsResult? result = RpcResponseHelpers.ResultAs<McpListToolsResult>(response.Body);
                        TestAssert.NotNull(result, "List result should deserialize.");
                        TestAssert.Equal(60000L, result!.TtlMs, "List result should carry ttlMs.");
                        TestAssert.Equal("public", result.CacheScope, "List result should carry cacheScope.");
                    }),
                });
        }

        private static McpImplementation? ServerInfoFrom(Dictionary<string, object?> meta)
        {
            if (!meta.TryGetValue("io.modelcontextprotocol/serverInfo", out object? value) || value == null)
            {
                return null;
            }

            return JsonSerializer.Deserialize<McpImplementation>(JsonSerializer.Serialize(value));
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
