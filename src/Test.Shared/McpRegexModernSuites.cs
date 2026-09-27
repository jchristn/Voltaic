namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers the ES2025 additions to Voltaic's ECMA-262 regular expression engine for JSON Schema <c>pattern</c>:
    /// pattern modifiers (<c>i</c> with Unicode simple case folding, <c>m</c>, <c>s</c>, their scoping and syntax
    /// errors), duplicate named groups, the group nesting limit, and the quadratic part of the step budget. Every
    /// expectation was checked against Node.js 24 (V8); where V8's modifier form disagrees with its own flag form
    /// (<c>/^(?i:\u017F)$/u</c> against <c>s</c>), the case follows ECMA-262 and the flag form.
    /// </summary>
    public static class McpRegexModernSuites
    {
        /// <summary>
        /// ES2025 cases.
        /// </summary>
        public static TestSuiteDescriptor Cases()
        {
            const string suiteId = "Mcp.RegexModern";
            return new TestSuiteDescriptor(
                suiteId,
                "ES2025 regular expression modifiers, duplicate named groups, nesting, and budget",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "ModifierSyntax", "(?ims-ims:...) is accepted; a repeated flag, a flag on both sides, (?-:...), an unknown flag, and (?i) without a colon are syntax errors", ct =>
                    {
                        using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, TestPorts.GetFreePort());
                        int index = 0;
                        foreach (string valid in new[] { "(?i:a)", "(?-i:a)", "(?i-:a)", "(?ims:a)", "(?ms-i:a)", "(?-ims:a)", "(?i:(?m:(?s:a)))", "(?<=(?i:a))b" })
                        {
                            server.RegisterTool("ok" + index++, "Valid pattern", PatternSchema(new[] { valid }), args => "x");
                        }

                        foreach (string invalid in new[] { "(?ii:a)", "(?i-i:a)", "(?-:a)", "(?x:a)", "(?I:a)", "(?g:a)", "(?i)a", "(?-i-s:a)", "(?ims-ims:a)", "(?i-mi:a)", "(?mm:a)", "(?i:a" })
                        {
                            TestAssert.Throws<ArgumentException>(() => server.RegisterTool("bad", "Bad pattern", PatternSchema(new[] { invalid }), args => "x"), $"Rejected: {invalid}");
                        }

                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "IgnoreCaseLiteralsAndClasses", "The i modifier compares by simple case folding: ſ/s/S, K/k/K, ß/ẞ, Σσς, ǅ, supplementary pairs; dotless and dotted I stay apart; classes, negated classes, and ranges fold; the scope ends with the group", async ct =>
                    {
                        await AssertPatternsAsync(new[]
                        {
                            Expect(@"^(?i:s)$", "\u017F", true),
                            Expect(@"^(?i:S)$", "\u017F", true),
                            Expect(@"^(?i:\u017F)$", "s", true),
                            Expect(@"^(?i:k)$", "\u212A", true),
                            Expect(@"^(?i:\u212A)$", "K", true),
                            Expect(@"^(?i:ß)$", "\u1E9E", true),
                            Expect(@"^(?i:ß)$", "ss", false),
                            Expect(@"^(?i:σ)$", "ς", true),
                            Expect(@"^(?i:ς)$", "Σ", true),
                            Expect(@"^(?i:ǅ)$", "ǆ", true),
                            Expect(@"^(?i:ǅ)$", "Ǆ", true),
                            Expect(@"^(?i:i)$", "ı", false),
                            Expect(@"^(?i:i)$", "İ", false),
                            Expect(@"^(?i:I)$", "i", true),
                            Expect(@"^(?i:\u{10400})$", "\U00010428", true),
                            Expect(@"^(?i:[\u{10400}-\u{10410}])$", "\U00010430", true),
                            Expect(@"^(?i:[a-z])$", "\u212A", true),
                            Expect(@"^(?i:[^a-z])$", "K", false),
                            Expect(@"^(?i:[^a-z])$", "\u017F", false),
                            Expect(@"^(?i:[^s])$", "S", false),
                            Expect(@"^[^s]$", "S", true),
                            Expect(@"^(?i:a)b$", "AB", false),
                            Expect(@"^(?i:a)b$", "Ab", true),
                            Expect(@"^(?i:a(?-i:b)c)$", "AbC", true),
                            Expect(@"^(?i:a(?-i:b)c)$", "ABC", false),
                            Expect(@"^(?-i:a(?i:b))$", "aB", true),
                            Expect(@"^(?i:\p{Lu})$", "a", true),
                            Expect(@"^\p{Lu}$", "a", false),
                        }, ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "IgnoreCaseWordCharacters", "Under i, \\w, \\W, \\b, and \\B treat U+017F and U+212A as word characters; outside i they do not", async ct =>
                    {
                        await AssertPatternsAsync(new[]
                        {
                            Expect(@"^(?i:\w)$", "\u017F", true),
                            Expect(@"^\w$", "\u017F", false),
                            Expect(@"^(?i:\W)$", "\u212A", false),
                            Expect(@"^\W$", "\u212A", true),
                            Expect(@"^(?i:[^\W])$", "\u017F", true),
                            Expect(@"(?i:\b)", "\u017F", true),
                            Expect(@"\b", "\u017F", false),
                            Expect(@"^(?i:.\B.)$", "a\u212A", true),
                            Expect(@"^.\B.$", "a\u212A", false),
                            Expect(@"(?i:\bk\b)", "\u212Ak", false),
                        }, ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "IgnoreCaseBackreferences", "A backreference inside an i scope compares by case folding, forward and inside lookbehind; one outside the scope compares exactly", async ct =>
                    {
                        await AssertPatternsAsync(new[]
                        {
                            Expect(@"^(?i:(.)\1)$", "S\u017F", true),
                            Expect(@"^(.)(?i:\1)$", "kK", true),
                            Expect(@"^(?i:(.))\1$", "aA", false),
                            Expect(@"^(?i:(.)(?-i:\1))$", "aA", false),
                            Expect(@"^(?i:(..)\1)$", "\U00010400\u03C3\U00010428\u03C2", true),
                            Expect(@"(?i:(?<=\1(.)))b$", "Aab", true),
                            Expect(@"(?<=(?i:\1)(.))$", "Ss", true),
                            Expect(@"(?<=\1(.))$", "Ss", false),
                        }, ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "MultilineAndDotAll", "m lets ^ and $ match at LF, CR, U+2028, and U+2029 (not U+0085), also in lookbehind; s lets . match line terminators; both are scoped", async ct =>
                    {
                        await AssertPatternsAsync(new[]
                        {
                            Expect(@"(?m:^b)", "a\nb", true),
                            Expect(@"^b", "a\nb", false),
                            Expect(@"(?m:a$)", "a\rb", true),
                            Expect(@"a$", "a\rb", false),
                            Expect(@"(?m:^b)", "a\u2028b", true),
                            Expect(@"(?m:^b)", "a\u2029b", true),
                            Expect(@"(?m:^b)", "a\u0085b", false),
                            Expect(@"(?m:(?<=^)b)", "a\nb", true),
                            Expect(@"(?m:^)(?-m:^)b", "a\nb", false),
                            Expect(@"^(?s:.)$", "\n", true),
                            Expect(@"^.$", "\n", false),
                            Expect(@"^(?s:.+)$", "a\r\u2028b", true),
                            Expect(@"^(?s:a.(?-s:.))$", "a\n\n", false),
                            Expect(@"^(?im-s:^a.$)", "x\nA\n", false),
                            Expect(@"(?im-s:^a.$)", "x\nAb\n", true),
                        }, ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "DuplicateNamedGroups", "A name may repeat in different alternatives and \\k refers to the group that participated; in one alternative, nested, or outside the disjunction it is a syntax error", async ct =>
                    {
                        await AssertPatternsAsync(new[]
                        {
                            Expect(@"^(?:(?<a>x)|(?<a>y))\k<a>$", "xx", true),
                            Expect(@"^(?:(?<a>x)|(?<a>y))\k<a>$", "yy", true),
                            Expect(@"^(?:(?<a>x)|(?<a>y))\k<a>$", "xy", false),
                            Expect(@"^(?:(?<a>x)|(?<a>y)|z)\k<a>$", "z", true),
                            Expect(@"^(?:(?:(?<a>x)|(?<a>y))\k<a>)+$", "xxyyxx", true),
                            Expect(@"^(?:(?:(?<a>x)|(?<a>y))\k<a>)+$", "xxyx", false),
                            Expect(@"^(?<a>x)\k<a>$|^(?<a>y)\k<a>$", "yy", true),
                            Expect(@"^(?i:(?:(?<a>s)|(?<a>k))\k<a>)$", "S\u017F", true),
                            Expect(@"(?<=(?:(?<a>x)|(?<a>y))\k<a>)z", "yyz", true),
                        }, ct).ConfigureAwait(false);

                        using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, TestPorts.GetFreePort());
                        server.RegisterTool("ok0", "Valid pattern", PatternSchema(new[] { "(?<a>x)|(?<a>y)|(?<a>z)" }), args => "x");
                        server.RegisterTool("ok1", "Valid pattern", PatternSchema(new[] { "(?:(?<a>x)|(?<a>y))|(?<a>z)" }), args => "x");

                        // (?<a>x)(?:c|(?<a>d)) and (?<a>(?<a>x)|b) are ES2025 syntax errors (both groups might participate),
                        // although V8 in Node.js 24 accepts them.
                        foreach (string invalid in new[] { "(?<a>x)(?<a>y)", "(?<a>x)(?<a>y)|z", "(?:(?<a>x)|(?<a>y))(?<a>z)", "(?<a>x)(?:c|(?<a>d))", "(?<a>(?<a>x)|b)", "(?:(?<a>x)|(?<a>y))\\k<b>" })
                        {
                            TestAssert.Throws<ArgumentException>(() => server.RegisterTool("bad", "Bad pattern", PatternSchema(new[] { invalid }), args => "x"), $"Rejected: {invalid}");
                        }
                    }),

                    Case(suiteId, "NestingLimit", "Exactly 500 nested groups (or lookaheads) are accepted and match; 501 are rejected", async ct =>
                    {
                        string groups500 = new string('(', 500) + "a" + new string(')', 500);
                        string lookaheads500 = String.Concat(Enumerable.Repeat("(?=", 500)) + "a" + new string(')', 500) + "a";
                        await AssertPatternsAsync(new[]
                        {
                            Expect("^" + groups500 + "$", "a", true),
                            Expect("^" + groups500 + "$", "b", false),
                            Expect("^" + lookaheads500 + "$", "a", true),
                        }, ct).ConfigureAwait(false);

                        using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, TestPorts.GetFreePort());
                        foreach (string invalid in new[] { new string('(', 501) + "a" + new string(')', 501), String.Concat(Enumerable.Repeat("(?:", 501)) + "a" + new string(')', 501) })
                        {
                            TestAssert.Throws<ArgumentException>(() => server.RegisterTool("bad", "Bad pattern", PatternSchema(new[] { invalid }), args => "x"), "501 nested groups are rejected.");
                        }
                    }),

                    Case(suiteId, "QuadraticBudget", "Quadratic searches on 5,000 characters fit the budget, matching or not; exponential backtracking still exhausts it", async ct =>
                    {
                        string letters = new string('a', 5000);
                        await AssertPatternsAsync(new[]
                        {
                            Expect(@"[a-z]+\d", letters + " b1", true),
                            Expect(@"\w+@x", letters + " b@x", true),
                            Expect(@"\w+\s", letters + "! b ", true),
                            Expect(@"(\w)\w*\1\d", letters + "!xx1", true),
                        }, ct).ConfigureAwait(false);

                        // Inside not, a pattern that finds no match passes, and an exhausted match fails validation.
                        string schema = "{\"type\":\"object\",\"properties\":{"
                            + "\"a\":{\"not\":{\"pattern\":\"[a-z]+\\\\d\"}},"
                            + "\"b\":{\"not\":{\"pattern\":\"\\\\w+@x\"}},"
                            + "\"c\":{\"not\":{\"pattern\":\"\\\\w+\\\\s\"}},"
                            + "\"d\":{\"not\":{\"pattern\":\"(\\\\w)\\\\w*\\\\1\\\\d\"}},"
                            + "\"e\":{\"not\":{\"pattern\":\"(a+)+$\"}}}}";
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("n", "Not a pattern", McpStrictSuites.Schema(schema), args => "ok")).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        foreach (string property in new[] { "a", "b", "c", "d" })
                        {
                            string arguments = JsonSerializer.Serialize(new Dictionary<string, string> { { property, letters } });
                            TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "n", arguments).ConfigureAwait(false), $"Property {property}: the quadratic search finishes without a match.");
                        }

                        string exponential = JsonSerializer.Serialize(new Dictionary<string, string> { { "e", new string('a', 40) + "!" } });
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "n", exponential).ConfigureAwait(false), "(a+)+$ against 40 a's and '!' exhausts the budget.");
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
                string shownPattern = item.Pattern.Length > 60 ? item.Pattern.Substring(0, 30) + "...(" + item.Pattern.Length + ")" : item.Pattern;
                string shown = item.Input.Length > 40 ? item.Input.Substring(0, 20) + "...(" + item.Input.Length + ")" : item.Input;
                TestAssert.Equal(item.Matches, !rejected, $"/{shownPattern}/ against '{shown}' should {(item.Matches ? "match" : "not match")}.");
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
