namespace Voltaic.Mcp
{
    using System;

    /// <summary>
    /// An ECMA-262 regular expression, the dialect JSON Schema <c>pattern</c> and <c>patternProperties</c> use, with the
    /// semantics of the <c>u</c> (Unicode) flag that JSON Schema recommends: matching works on code points, <c>.</c>
    /// excludes line terminators, <c>^</c> and <c>$</c> are the input's ends, <c>\d</c> and <c>\w</c> are ASCII,
    /// <c>\s</c> is the ECMA-262 white space set, <c>\p{...}</c> takes general categories (short or long names) and
    /// <c>Any</c>, <c>ASCII</c>, and <c>Assigned</c>, and captures are reset on every repetition. The ES2025 syntax is
    /// supported: duplicate named groups in different alternatives (<c>\k&lt;name&gt;</c> refers to the one that
    /// participated) and pattern modifiers <c>(?ims-ims:...)</c>, where <c>i</c> compares by Unicode simple case
    /// folding (and adds U+017F and U+212A to <c>\w</c>, <c>\b</c>, and <c>\B</c>), <c>m</c> lets <c>^</c> and
    /// <c>$</c> match at line terminators, and <c>s</c> lets <c>.</c> match every code point. The pattern is searched
    /// for anywhere in the input, as <c>pattern</c> requires. Immutable and thread-safe.
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

            // A pattern that starts with ^ (outside an m modifier) can only match at the start of the input.
            Anchored = program.Instructions.Count > 0 && program.Instructions[0].Op == McpRegexOp.Start && !program.Instructions[0].Multiline;
        }

        /// <summary>
        /// Gets or sets the base step budget of one match. The budget of a match on an input of n UTF-16 code units is
        /// this, plus <see cref="StepsPerInputCharacter"/> times n, plus the smaller of
        /// <see cref="QuadraticStepsPerInputCharacter"/> times n squared and <see cref="MaxQuadraticSteps"/>. Linear work
        /// always fits, quadratic work (such as <c>[a-z]+\d</c> searched through a long run of letters) fits for inputs
        /// up to about 7,000 characters, and exponential backtracking runs out. A step is one instruction, one code
        /// point scanned by a character-set loop or compared by a backreference, or one capture slot a lookaround copies
        /// or a repetition resets, so the budget bounds the running time (measured on a desktop CPU, an exhausted match
        /// took up to about 1.7 seconds at 7,000 characters and 3.5 seconds at 1 MB). An exhausted match fails
        /// validation. Default is 10,000,000.
        /// </summary>
        internal static long MaxSteps { get; set; } = 10_000_000;

        /// <summary>
        /// Gets or sets the step budget added per input character (UTF-16 code unit). Default is 200.
        /// </summary>
        internal static long StepsPerInputCharacter { get; set; } = 200;

        /// <summary>
        /// Gets or sets the step budget added per squared input character count, which lets a search do quadratic work
        /// on moderate inputs; the addition is capped at <see cref="MaxQuadraticSteps"/>. Default is 4, about twice what a
        /// search such as <c>(\w)\w*\1\d</c> spends on each pair of input positions.
        /// </summary>
        internal static long QuadraticStepsPerInputCharacter { get; set; } = 4;

        /// <summary>
        /// Gets or sets the cap on the quadratic part of the step budget, reached at about 7,071 input characters with
        /// the default <see cref="QuadraticStepsPerInputCharacter"/>. With the other defaults, a match on a 1 MB input
        /// may take at most 410,000,000 steps. Default is 200,000,000.
        /// </summary>
        internal static long MaxQuadraticSteps { get; set; } = 200_000_000;

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
            return IsMatch(text, Budget(text.Length), out long _);
        }

        /// <summary>
        /// Returns true when the pattern matches somewhere in <paramref name="input"/>, false when it does not, and null
        /// when <paramref name="maxSteps"/> ran out first; <paramref name="stepsUsed"/> reports the steps spent.
        /// </summary>
        internal bool? IsMatch(string input, long maxSteps, out long stepsUsed)
        {
            McpRegexMatcher matcher = new McpRegexMatcher(input ?? String.Empty, _CaptureCount, _RegisterCount, maxSteps);
            bool? matched = matcher.Search(_Program, Anchored);
            stepsUsed = matcher.StepsUsed;
            return matched;
        }

        /// <summary>
        /// Returns the step budget of a match on an input of <paramref name="length"/> UTF-16 code units (see
        /// <see cref="MaxSteps"/>).
        /// </summary>
        internal static long Budget(int length)
        {
            double quadratic = (double)QuadraticStepsPerInputCharacter * length * length;
            long cappedQuadratic = quadratic >= MaxQuadraticSteps ? MaxQuadraticSteps : (long)quadratic;
            return MaxSteps + StepsPerInputCharacter * length + cappedQuadratic;
        }
    }
}
