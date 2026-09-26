namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers Voltaic's ECMA-262 regular expression engine for JSON Schema <c>pattern</c>: code-point matching
    /// (astral characters, surrogate escapes), general categories, Annex B leniencies, backreferences (forward
    /// references and per-iteration capture reset), lookarounds, lazy and counted repetition, the step budget, long
    /// inputs, and rejected syntax.
    /// </summary>
    public static class McpRegexSuites
    {
        /// <summary>
        /// Engine cases.
        /// </summary>
        public static TestSuiteDescriptor Engine()
        {
            const string suiteId = "Mcp.RegexEngine";
            return new TestSuiteDescriptor(
                suiteId,
                "ECMA-262 regular expressions for JSON Schema pattern",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "CodePointsAndCategories", "Astral characters are one character to ., negated classes, \\S, \\p{L}, quantifiers, and classes; categories follow ECMA-262 names", async ct =>
                    {
                        await AssertPatternsAsync(new[]
                        {
                            Expect(@"^\p{L}$", "\U00020000", true),
                            Expect(@"^.$", "\U0001F600", true),
                            Expect(@"^[^a]$", "\U0001F600", true),
                            Expect(@"^\S$", "\U0001F600", true),
                            Expect("^\U0001F432*$", "\U0001F432\U0001F432", true),
                            Expect("^[\U0001F600]$", "\U0001F600", true),
                            Expect(@"^[😀]$", "\U0001F600", true),
                            Expect(@"^\u{1F600}$", "\U0001F600", true),
                            Expect(@"^\p{Cased_Letter}$", "ʰ", false),
                            Expect(@"^\p{LC}$", "a", true),
                            Expect(@"^\p{Lu}+$", "ABC", true),
                            Expect(@"^\P{L}$", "1", true),
                            Expect(@"^\p{Any}$", "\U0001F600", true),
                            Expect(@"^\p{ASCII}+$", "café", false),
                            Expect(@"^\s$", "　", true),
                            Expect(@"^\d$", "١", false),
                        }, ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "ClassesAndEscapes", "Escaped backslashes and hyphens keep their meaning in classes; Annex B hyphens next to class escapes are literal; identity escapes of punctuation work", async ct =>
                    {
                        await AssertPatternsAsync(new[]
                        {
                            Expect(@"^[\\-\w]+$", "b", true),
                            Expect(@"^[\\-\w]+$", "^", false),
                            Expect(@"^[\\-\w]+$", "\\-", true),
                            Expect(@"^[\--0]$", ".", true),
                            Expect(@"^[\--0]$", "/", true),
                            Expect(@"^[\--0]$", "1", false),
                            Expect(@"^[\w-.]+$", "a-b.c", true),
                            Expect(@"^[a-z\_]+$", "a_b", true),
                            Expect(@"^[]$", "a", false),
                            Expect(@"^[^]$", "\n", true),
                            Expect(@"^\x41\cJ$", "A\n", true),
                            Expect(@"^a{,2}$", "a{,2}", true),
                        }, ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "GroupsAndBackreferences", "Forward references match empty, captures reset on every iteration, named groups (including $ in the name) and lookbehind work", async ct =>
                    {
                        await AssertPatternsAsync(new[]
                        {
                            Expect(@"^\1(a)$", "a", true),
                            Expect(@"^(?:(a)|b)+\1$", "ab", true),
                            Expect(@"^(?:(a)|b)+\1$", "aba", false),
                            Expect(@"^(?<$a>x)\k<$a>$", "xx", true),
                            Expect(@"^(a|ab)(c|bcd)(d*)$", "abcd", true),
                            Expect(@"(?<=\$)\d+$", "$42", true),
                            Expect(@"(?<=\$)\d+$", "42", false),
                            Expect(@"(?<!-)\b\d+$", "x 42", true),
                            Expect(@"^(?=.*\d)(?=.*[a-z]).{6,}$", "abc123", true),
                            Expect(@"^(?=.*\d)(?=.*[a-z]).{6,}$", "abcdef", false),
                            Expect(@"\bfoo\b", "a foo b", true),
                            Expect(@"\bfoo\b", "afoob", false),
                        }, ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "Repetition", "Lazy, counted, nested, and empty-matching repetitions follow ECMA-262", async ct =>
                    {
                        await AssertPatternsAsync(new[]
                        {
                            Expect(@"^a+?b$", "aaab", true),
                            Expect(@"^a{2,3}$", "aaaa", false),
                            Expect(@"^a{2,3}$", "aa", true),
                            Expect(@"^(a*)*$", "aaa", true),
                            Expect(@"^(a*)+b$", "aab", true),
                            Expect(@"^(?:a|bc)*?c$", "abcc", true),
                            Expect(@"^(ab){2}$", "abab", true),
                            Expect(@"^(?:x|y){3,}$", "xy", false),
                        }, ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "BudgetAndLongInputs", "Catastrophic backtracking stops within the step budget (and fails validation); very long inputs never overflow the stack", async ct =>
                    {
                        Stopwatch elapsed = Stopwatch.StartNew();
                        await AssertPatternsAsync(new[]
                        {
                            Expect(@"^(a+)+$", new string('a', 40) + "b", false),
                            Expect(@"^[a-z]*$", new string('a', 300000), true),
                            Expect(@"^(?:a|b)*$", new string('a', 100000), true),
                            Expect(@"^(.)*$", new string('z', 100000), true),
                        }, ct).ConfigureAwait(false);
                        TestAssert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30), $"The run is bounded ({elapsed.ElapsedMilliseconds} ms).");
                    }),

                    Case(suiteId, "BudgetScalesAndAbortsEverywhere", "Linear and quadratic work on long inputs fits the budget; an exhausted match fails the whole validation, even inside not", async ct =>
                    {
                        await AssertPatternsAsync(new[]
                        {
                            Expect(@"[a-z]+\d", new string('a', 3300) + " b1", true),
                            Expect(@"(\w+)\s\1", new string('x', 1500) + " " + new string('x', 1500), true),
                            Expect(@"^(?:a|b)*$", new string('a', 200000), true),
                        }, ct).ConfigureAwait(false);

                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("n", "Not a pattern", McpStrictSuites.Schema("{\"type\":\"object\",\"properties\":{\"v\":{\"not\":{\"pattern\":\"^(x+x+)+y$\"}}}}"), args => "ok")).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "n", "{\"v\":\"" + new string('x', 60) + "z\"}").ConfigureAwait(false), "An exhausted match inside not rejects instead of passing.");
                    }),

                    Case(suiteId, "GroupNamesAndQuantifierSyntax", "Group names follow ECMA-262 identifier rules; a braced quantifier with nothing to repeat is an error; deep nesting is rejected, not a crash", ct =>
                    {
                        using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, TestPorts.GetFreePort());
                        int index = 0;
                        foreach (string valid in new[] { "(?<a\u0301>x)", "(?<a\u00B7>x)", @"(?<\u0061b>x)\k<ab>", "(?<\u2118>x)", "(?<a\u0663>x)" })
                        {
                            server.RegisterTool("ok" + index++, "Valid pattern", PatternSchema(new[] { valid }), args => "x");
                        }

                        foreach (string invalid in new[] { "(?<\u2E2F>x)", "{2}", "^{2}", "a|{1}", "({2})", new string('(', 600) + "a" + new string(')', 600) })
                        {
                            TestAssert.Throws<ArgumentException>(() => server.RegisterTool("bad", "Bad pattern", PatternSchema(new[] { invalid }), args => "x"), $"Rejected: {(invalid.Length > 30 ? invalid.Substring(0, 30) + "..." : invalid)}");
                        }

                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "UnpairedSurrogatesFailValidation", "A string argument with an unpaired surrogate gets an isError result, not an internal error", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("s", "String", McpStrictSuites.Schema("{\"type\":\"object\",\"properties\":{\"s\":{\"type\":\"string\"}}}"), args => "ok")).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"s\",\"arguments\":{\"s\":\"\\ud83d\"}}}").ConfigureAwait(false);
                        JsonProbe response = McpStrictSuites.Next(client);
                        TestAssert.True(response.Has("result") && response.Get("result").Get("isError").Bool(), $"The unpaired surrogate is an input validation error: {System.Text.Json.JsonSerializer.Serialize(response.Has("error") ? response.Get("error").Get("message").String() : "result")}");
                    }),

                    Case(suiteId, "InvalidPatternsAreRejected", "Syntax that is not ECMA-262, unsupported properties, and invalid references are rejected when the tool is registered", ct =>
                    {
                        using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, TestPorts.GetFreePort());
                        string[] invalid = { @"\p{Script=Greek}", @"\p{Alphabetic}", "(?i)a", @"\q", "a**", "(?=a)*", @"\2(a)", "[z-a]", @"(?<a>x)(?<a>y)", @"\k<b>(?<a>x)", @"(?>a)", "(", "a)", @"\", @"a{3,1}", @"\A", @"\01" };
                        foreach (string pattern in invalid)
                        {
                            TestAssert.Throws<ArgumentException>(() => server.RegisterTool("bad", "Bad pattern", PatternSchema(new[] { pattern }), args => "x"), $"Rejected: {pattern}");
                        }

                        return Task.CompletedTask;
                    }),
                });
        }

        private static PatternCase Expect(string pattern, string input, bool matches)
        {
            return new PatternCase(pattern, input, matches);
        }

        // Registers one tool whose properties p0..pn carry the patterns, then checks each input against its property.
        private static async Task AssertPatternsAsync(PatternCase[] cases, CancellationToken token)
        {
            List<string> patterns = cases.Select(item => item.Pattern).Distinct(StringComparer.Ordinal).ToList();
            await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(token, s => s.RegisterTool("p", "Patterns", PatternSchema(patterns), args => "ok")).ConfigureAwait(false);
            using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, token).ConfigureAwait(false);
            foreach (PatternCase item in cases)
            {
                string property = "p" + patterns.IndexOf(item.Pattern);
                string arguments = JsonSerializer.Serialize(new Dictionary<string, string> { { property, item.Input } });
                bool rejected = await McpStrictSuites.IsErrorAsync(client, "p", arguments).ConfigureAwait(false);
                string shown = item.Input.Length > 40 ? item.Input.Substring(0, 20) + "...(" + item.Input.Length + ")" : item.Input;
                TestAssert.Equal(item.Matches, !rejected, $"/{item.Pattern}/ against '{shown}' should {(item.Matches ? "match" : "not match")}.");
            }
        }

        private static JsonElement PatternSchema(IEnumerable<string> patterns)
        {
            Dictionary<string, object> properties = new Dictionary<string, object>();
            int index = 0;
            foreach (string pattern in patterns)
            {
                properties["p" + index++] = new Dictionary<string, object> { { "type", "string" }, { "pattern", pattern } };
            }

            return JsonSerializer.SerializeToElement(new Dictionary<string, object> { { "type", "object" }, { "properties", properties } });
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "regex", "conformance" });
        }
    }
}
