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
    /// Exercises tool input and output schema validation end to end through a real <see cref="McpHttpServer"/>:
    /// every JSON Schema keyword the validator supports is checked with arguments that must pass (the handler runs)
    /// and arguments that must fail (an <c>isError</c> tool result naming the problem, and the handler never runs).
    /// </summary>
    public static class McpSchemaValidationSuites
    {
        /// <summary>
        /// JSON Schema keyword validation cases for tool inputs and outputs.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public static TestSuiteDescriptor SchemaValidation()
        {
            // Mcp.SchemaValidation is already used by McpDiagnosticToolSuites.SchemaValidation().
            const string suiteId = "Mcp.SchemaKeywords";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP Tool Schema Keyword Validation",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "TypeArrayWithNull", "A type array such as [\"string\",\"null\"] is enforced instead of disabling validation", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"name":{"type":["string","null"]}},"required":["name"]}""",
                            new[] { """{"name":"a"}""", """{"name":null}""" },
                            new[] { """{"name":5}""", """{"name":{}}""" },
                            "arguments.name must be a JSON string or null",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "IntegerAcceptsIntegralNumbers", "integer accepts 1, 1.0, and 1e2 and rejects 1.5 and strings", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"n":{"type":"integer"}}}""",
                            new[] { """{"n":1}""", """{"n":1.0}""", """{"n":1e2}""", """{"n":-0}""" },
                            new[] { """{"n":1.5}""", """{"n":"1"}""", """{"n":1.25e1}""" },
                            "arguments.n must be a JSON integer",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "Enum", "enum restricts a value to the listed values (numbers compare by value)", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"color":{"enum":["red","green",1]}}}""",
                            new[] { """{"color":"red"}""", """{"color":1.0}""" },
                            new[] { """{"color":"blue"}""", """{"color":2}""", """{"color":null}""" },
                            "must be one of the allowed values",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "Const", "const requires an exact value, with objects compared regardless of member order", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"mode":{"const":"fast"},"shape":{"const":{"a":1,"b":[1,2]}}}}""",
                            new[] { """{"mode":"fast"}""", """{"shape":{"b":[1,2],"a":1}}""" },
                            new[] { """{"mode":"slow"}""", """{"shape":{"a":1,"b":[2,1]}}""" },
                            "must be exactly",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "NestedItems", "items validates every array element, including nested objects, and names the failing index", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"points":{"type":"array","items":{"type":"object","properties":{"x":{"type":"number"}},"required":["x"]}}}}""",
                            new[] { """{"points":[]}""", """{"points":[{"x":1},{"x":2.5}]}""" },
                            new[] { """{"points":[{"x":1},{"y":2}]}""", """{"points":[{"x":"a"}]}""", """{"points":{"x":1}}""" },
                            "arguments.points",
                            ct).ConfigureAwait(false);

                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"points":{"type":"array","items":{"type":"object","required":["x"]}}}}""",
                            new[] { """{"points":[{"x":1}]}""" },
                            new[] { """{"points":[{"x":1},{"y":2}]}""" },
                            "arguments.points[1] is missing required property 'x'",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "MinMaxItems", "minItems and maxItems bound the array length", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"tags":{"type":"array","minItems":1,"maxItems":2}}}""",
                            new[] { """{"tags":["a"]}""", """{"tags":["a","b"]}""" },
                            new[] { """{"tags":[]}""", """{"tags":["a","b","c"]}""" },
                            "arguments.tags must contain at",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "MinMaxLength", "minLength and maxLength count Unicode code points", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"code":{"type":"string","minLength":2,"maxLength":3}}}""",
                            new[] { """{"code":"ab"}""", """{"code":"abc"}""", "{\"code\":\"\\uD83D\\uDE00\\uD83D\\uDE00\"}" },
                            new[] { """{"code":"a"}""", """{"code":"abcd"}""", "{\"code\":\"\\uD83D\\uDE00\"}" },
                            "characters long",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "Pattern", "pattern is an unanchored regular expression search", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"id":{"type":"string","pattern":"^[a-z]+-[0-9]+$"},"word":{"type":"string","pattern":"cat"}}}""",
                            new[] { """{"id":"abc-12"}""", """{"word":"concatenate"}""" },
                            new[] { """{"id":"ABC-12"}""", """{"id":"abc-12x"}""", """{"word":"dog"}""" },
                            "must match the pattern",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "MinimumAndExclusiveMaximum", "minimum is inclusive and exclusiveMaximum is exclusive", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"n":{"type":"number","minimum":0,"exclusiveMaximum":10},"m":{"exclusiveMinimum":1,"maximum":2}}}""",
                            new[] { """{"n":0}""", """{"n":9.99}""", """{"m":2}""", """{"m":1.5}""" },
                            new[] { """{"n":-1}""", """{"n":10}""", """{"n":-0.001}""", """{"m":1}""", """{"m":2.01}""" },
                            "arguments.",
                            ct).ConfigureAwait(false);

                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"n":{"type":"number","minimum":0,"exclusiveMaximum":10}}}""",
                            Array.Empty<string>(),
                            new[] { """{"n":10}""" },
                            "arguments.n must be less than 10.",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "MultipleOf", "multipleOf uses exact decimal arithmetic (0.3 is a multiple of 0.1)", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"half":{"multipleOf":0.5},"tenth":{"multipleOf":0.1}}}""",
                            new[] { """{"half":1.5}""", """{"half":2}""", """{"tenth":0.3}""", """{"tenth":7}""" },
                            new[] { """{"half":1.25}""", """{"tenth":0.35}""" },
                            "must be a multiple of",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "AnyOf", "anyOf requires at least one matching subschema", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"v":{"anyOf":[{"type":"string"},{"type":"integer","minimum":0}]}}}""",
                            new[] { """{"v":"x"}""", """{"v":3}""" },
                            new[] { """{"v":-1}""", """{"v":true}""", """{"v":2.5}""" },
                            "anyOf",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "OneOf", "oneOf requires exactly one matching subschema", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"v":{"oneOf":[{"type":"integer"},{"type":"number","minimum":10}]}}}""",
                            new[] { """{"v":5}""", """{"v":10.5}""" },
                            new[] { """{"v":12}""", """{"v":"x"}""", """{"v":2.5}""" },
                            "oneOf",
                            ct).ConfigureAwait(false);

                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"v":{"oneOf":[{"type":"integer"},{"type":"number","minimum":10}]}}}""",
                            Array.Empty<string>(),
                            new[] { """{"v":12}""" },
                            "matches more than one",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "AllOf", "allOf requires every subschema to match", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"v":{"allOf":[{"type":"string"},{"minLength":3},{"pattern":"^a"}]}}}""",
                            new[] { """{"v":"abc"}""", """{"v":"abcdef"}""" },
                            new[] { """{"v":"ab"}""", """{"v":"bcd"}""", """{"v":123}""" },
                            "arguments.v",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "Not", "not rejects values that match its subschema", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"v":{"not":{"type":"null"}}}}""",
                            new[] { """{"v":1}""", """{"v":"x"}""", """{}""" },
                            new[] { """{"v":null}""" },
                            "must not match the 'not' schema",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "IfThenElse", "if/then/else applies then when the condition holds and else otherwise", async ct =>
                    {
                        const string schema = """
                            {"type":"object","properties":{"kind":{"type":"string"}},
                             "if":{"properties":{"kind":{"const":"circle"}},"required":["kind"]},
                             "then":{"required":["radius"]},
                             "else":{"required":["width"]}}
                            """;

                        await AssertSchemaAsync(
                            schema,
                            new[] { """{"kind":"circle","radius":1}""", """{"kind":"square","width":2}""" },
                            new[] { """{"kind":"circle","width":2}""" },
                            "missing required property 'radius'",
                            ct).ConfigureAwait(false);

                        await AssertSchemaAsync(
                            schema,
                            Array.Empty<string>(),
                            new[] { """{"kind":"square"}""" },
                            "missing required property 'width'",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "RefToDefs", "$ref resolves #/$defs/... and #/definitions/... local references", async ct =>
                    {
                        const string schema = """
                            {"type":"object",
                             "$defs":{"point":{"type":"object","properties":{"x":{"type":"integer"}},"required":["x"],"additionalProperties":false}},
                             "definitions":{"label":{"type":"string","maxLength":4}},
                             "properties":{"a":{"$ref":"#/$defs/point"},"b":{"$ref":"#/definitions/label"}}}
                            """;

                        await AssertSchemaAsync(
                            schema,
                            new[] { """{"a":{"x":1},"b":"abcd"}""", """{}""" },
                            new[] { """{"a":{"x":"1"}}""", """{"a":{"x":1,"y":2}}""", """{"a":{}}""", """{"b":3}""", """{"b":"abcde"}""" },
                            "arguments.",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "RecursiveRef", "A recursive $ref validates nested structures at every level", async ct =>
                    {
                        const string schema = """
                            {"$defs":{"node":{"type":"object","properties":{"value":{"type":"integer"},"children":{"type":"array","items":{"$ref":"#/$defs/node"}}},"required":["value"]}},
                             "$ref":"#/$defs/node"}
                            """;

                        await AssertSchemaAsync(
                            schema,
                            new[] { """{"value":1}""", """{"value":1,"children":[{"value":2,"children":[{"value":3}]}]}""" },
                            new[] { """{"value":1,"children":[{"value":2,"children":[{"val":3}]}]}""" },
                            "arguments.children[0].children[0] is missing required property 'value'",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "CyclicRefRejectsWithoutHanging", "A $ref cycle that never consumes input is bounded and rejects rather than hanging or passing", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","$defs":{"a":{"$ref":"#/$defs/b"},"b":{"$ref":"#/$defs/a"}},"properties":{"x":{"$ref":"#/$defs/a"}}}""",
                            new[] { """{}""" },
                            new[] { """{"x":1}""" },
                            "too complex or too deeply nested",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "AdditionalPropertiesFalseAndSchema", "additionalProperties false rejects extras; the schema form validates them", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"a":{"type":"string"}},"additionalProperties":false}""",
                            new[] { """{"a":"x"}""", """{}""" },
                            new[] { """{"a":"x","b":1}""" },
                            "has unexpected property 'b'; the schema does not allow additional properties.",
                            ct).ConfigureAwait(false);

                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"a":{"type":"string"}},"additionalProperties":{"type":"integer"}}""",
                            new[] { """{"a":"x","b":1}""", """{"a":"x"}""" },
                            new[] { """{"a":"x","b":"y"}""", """{"a":1}""" },
                            "arguments.",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "PatternProperties", "patternProperties validates matching names and exempts them from additionalProperties", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","patternProperties":{"^n_":{"type":"number"},"^s_":{"type":"string"}},"additionalProperties":false}""",
                            new[] { """{"n_a":1,"s_b":"x"}""", """{}""" },
                            new[] { """{"n_a":"x"}""", """{"s_b":2}""", """{"other":1}""" },
                            "arguments",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "UniqueItems", "uniqueItems compares items by JSON value (1 equals 1.0; member order is ignored)", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"ids":{"type":"array","uniqueItems":true}}}""",
                            new[] { """{"ids":[1,2,"1",{"a":1,"b":2},{"a":2,"b":1},[1],[2]]}""", """{"ids":[]}""" },
                            new[] { """{"ids":[1,1.0]}""", """{"ids":[{"a":1,"b":2},{"b":2,"a":1}]}""", """{"ids":["x","y","x"]}""" },
                            "must not contain duplicate items",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "Required", "required names every missing property, including when arguments are omitted", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"a":{},"b":{}},"required":["a","b"]}""",
                            new[] { """{"a":1,"b":null}""" },
                            new[] { """{"a":1}""", """{}""" },
                            "is missing required property",
                            ct).ConfigureAwait(false);

                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"a":{},"b":{}},"required":["a","b"]}""",
                            Array.Empty<string>(),
                            new[] { """{"a":1}""" },
                            "Tool 'schema-tool' arguments is missing required property 'b'.",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "BooleanSchemas", "true subschemas accept anything and false subschemas reject any value", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"any":true,"never":false}}""",
                            new[] { """{"any":{"x":[1,2]}}""", """{}""" },
                            new[] { """{"never":1}""", """{"never":null}""" },
                            "arguments.never is not allowed by the schema",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "PrefixItemsAndContains", "prefixItems validates positions, items:false closes the tuple, and contains requires a match", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","properties":{"pair":{"type":"array","prefixItems":[{"type":"string"},{"type":"integer"}],"items":false},"list":{"type":"array","contains":{"const":"x"},"maxContains":1}}}""",
                            new[] { """{"pair":["a",1]}""", """{"pair":["a"]}""", """{"list":["a","x"]}""" },
                            new[] { """{"pair":["a","b"]}""", """{"pair":["a",1,2]}""", """{"list":["a"]}""", """{"list":["x","x"]}""" },
                            "arguments.",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "ObjectCountsAndDependencies", "minProperties, maxProperties, propertyNames, and dependentRequired are enforced", async ct =>
                    {
                        await AssertSchemaAsync(
                            """{"type":"object","minProperties":1,"maxProperties":3,"propertyNames":{"pattern":"^[a-z]+$"},"dependentRequired":{"card":["cvv"]}}""",
                            new[] { """{"a":1}""", """{"card":"1","cvv":"2"}""" },
                            new[] { """{}""", """{"a":1,"b":2,"c":3,"d":4}""", """{"Bad":1}""", """{"card":"1"}""" },
                            "arguments",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "MalformedKeywordsAreIgnoredIndividually", "Malformed or unknown keywords are ignored one by one and never disable the rest of the schema", async ct =>
                    {
                        const string schema = """
                            {"type":"object","unknownKeyword":[1,2],"x-custom":{"a":1},
                             "properties":{
                               "n":{"type":"integer","minimum":"zero","maxLength":-1,"format":"weird","description":"a number","enum":"not-an-array"},
                               "s":{"type":["string","unknown-type"],"pattern":"[","title":"S","examples":["a"]},
                               "t":{"type":42,"minLength":2}},
                             "required":["n"]}
                            """;

                        await AssertSchemaAsync(
                            schema,
                            new[] { """{"n":1}""", """{"n":-5,"s":"anything"}""", """{"n":1,"t":"ab"}""" },
                            new[] { """{}""", """{"n":"x"}""", """{"n":1,"s":5}""", """{"n":1,"t":"a"}""" },
                            "arguments",
                            ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "OutputSchemaViolationIsInternalError", "Structured content that violates the output schema (with a type array) is a -32603 error; valid output succeeds", async ct =>
                    {
                        JsonElement outputSchema = Json("""{"type":"object","properties":{"total":{"type":["number","null"]}},"required":["total"]}""");
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.RegisterTool("bad-output", "Returns invalid structured output", Json("""{"type":"object"}"""), outputSchema,
                                _ => McpToolCallResult.FromStructured(new { total = "wrong" }));
                            server.RegisterTool("good-output", "Returns valid structured output", Json("""{"type":"object"}"""), outputSchema,
                                _ => McpToolCallResult.FromStructured(new { total = (double?)null }));
                        }).ConfigureAwait(false);

                        RpcResult bad = await fixture.PostMcpAsync("tools/call", new { name = "bad-output", arguments = new { } }, 1, null, ct).ConfigureAwait(false);
                        RpcResult good = await fixture.PostMcpAsync("tools/call", new { name = "good-output", arguments = new { } }, 2, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32603, bad.Error.Get("code").Int(), $"Invalid output is an internal error. Body: {bad.Body}");
                        TestAssert.True(bad.Error.Get("message").String()!.Contains("structured output.total must be a JSON number or null", StringComparison.Ordinal), $"The error names the output path. Body: {bad.Body}");
                        TestAssert.False(good.Root.Has("error"), $"Valid output succeeds. Body: {good.Body}");
                        TestAssert.False(good.Result.Has("isError") && good.Result.Get("isError").Bool(), "Valid output is not an error.");
                    }),
                });
        }

        private static async Task AssertSchemaAsync(string schemaJson, string[] validArguments, string[] invalidArguments, string expectedFragment, CancellationToken token)
        {
            const string toolName = "schema-tool";
            int invoked = 0;
            JsonElement schema = Json(schemaJson);

            await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(token, server =>
            {
                server.RegisterTool(toolName, "Schema validation test tool", schema, _ =>
                {
                    Interlocked.Increment(ref invoked);
                    return "ok";
                });
            }).ConfigureAwait(false);

            int id = 1;
            foreach (string arguments in validArguments)
            {
                RpcResult response = await fixture.PostMcpAsync("tools/call", new { name = toolName, arguments = Json(arguments) }, id++, null, token).ConfigureAwait(false);
                TestAssert.False(response.Root.Has("error"), $"Valid arguments {arguments} must not be a protocol error. Body: {response.Body}");
                TestAssert.False(response.Result.Has("isError") && response.Result.Get("isError").Bool(), $"Valid arguments {arguments} must pass validation. Body: {response.Body}");
                TestAssert.Equal("ok", response.Result.Get("content")[0].Get("text").String(), $"The handler runs for {arguments}.");
            }

            foreach (string arguments in invalidArguments)
            {
                RpcResult response = await fixture.PostMcpAsync("tools/call", new { name = toolName, arguments = Json(arguments) }, id++, null, token).ConfigureAwait(false);
                TestAssert.False(response.Root.Has("error"), $"Invalid arguments {arguments} are a tool execution error, not a protocol error. Body: {response.Body}");
                TestAssert.True(response.Result.Has("isError") && response.Result.Get("isError").Bool(), $"Invalid arguments {arguments} must be rejected with isError. Body: {response.Body}");

                string text = response.Result.Get("content")[0].Get("text").String() ?? String.Empty;
                TestAssert.True(text.Contains("arguments", StringComparison.Ordinal), $"The message for {arguments} explains the argument problem: {text}");
                TestAssert.True(text.Contains(expectedFragment, StringComparison.Ordinal), $"The message for {arguments} should contain '{expectedFragment}': {text}");
            }

            TestAssert.Equal(validArguments.Length, Volatile.Read(ref invoked), "The handler runs only for valid arguments.");
        }

        private static JsonElement Json(string json)
        {
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                return document.RootElement.Clone();
            }
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "schema" });
        }
    }
}
