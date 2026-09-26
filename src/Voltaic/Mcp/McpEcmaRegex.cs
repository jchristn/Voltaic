namespace Voltaic.Mcp
{
    using System;

    /// <summary>
    /// An ECMA-262 regular expression, the dialect JSON Schema <c>pattern</c> and <c>patternProperties</c> use, with the
    /// semantics of the <c>u</c> (Unicode) flag that JSON Schema recommends: matching works on code points, <c>.</c>
    /// excludes line terminators, <c>^</c> and <c>$</c> are the input's ends, <c>\d</c> and <c>\w</c> are ASCII,
    /// <c>\s</c> is the ECMA-262 white space set, <c>\p{...}</c> takes general categories (short or long names) and
    /// <c>Any</c>, <c>ASCII</c>, and <c>Assigned</c>, and captures are reset on every repetition. The pattern is
    /// searched for anywhere in the input, as <c>pattern</c> requires. Immutable and thread-safe.
    /// </summary>
    internal sealed class McpEcmaRegex
    {
        private readonly McpRegexProgram _Program;
        private readonly int _CaptureCount;
        private readonly int _RegisterCount;

        private McpEcmaRegex(McpRegexProgram program, int captureCount, int registerCount)
        {
            _Program = program;
            _CaptureCount = captureCount;
            _RegisterCount = registerCount;

            // A pattern that starts with ^ can only match at the start of the input.
            Anchored = program.Instructions.Count > 0 && program.Instructions[0].Op == McpRegexOp.Start;
        }

        /// <summary>
        /// Gets the base step budget of one match. The budget of a match is this plus
        /// <see cref="StepsPerInputCharacter"/> per input character, so linear work on long inputs always fits and only
        /// catastrophic backtracking runs out. Default is 10,000,000.
        /// </summary>
        internal static long MaxSteps { get; set; } = 10_000_000;

        /// <summary>
        /// Gets the step budget added per input character. Default is 200.
        /// </summary>
        internal static long StepsPerInputCharacter { get; set; } = 200;

        internal bool Anchored { get; }

        /// <summary>
        /// Compiles a pattern.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when the pattern is not a valid ECMA-262 regular expression or uses a construct this engine does not support.</exception>
        internal static McpEcmaRegex Compile(string pattern)
        {
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            McpRegexNode root = McpRegexParser.Parse(pattern, out int captureCount);
            McpRegexCompiler compiler = new McpRegexCompiler();
            McpRegexProgram program = compiler.Compile(root, false);
            return new McpEcmaRegex(program, captureCount, compiler.RegisterCount);
        }

        /// <summary>
        /// Returns true when the pattern matches somewhere in <paramref name="input"/>, false when it does not, and null
        /// when the step budget ran out before an answer.
        /// </summary>
        internal bool? IsMatch(string input)
        {
            string text = input ?? String.Empty;
            McpRegexMatcher matcher = new McpRegexMatcher(text, _CaptureCount, _RegisterCount, MaxSteps + StepsPerInputCharacter * text.Length);
            return matcher.Search(_Program, Anchored);
        }
    }
}
