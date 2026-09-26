namespace Test.Shared
{
    /// <summary>
    /// A pattern, an input, and whether the pattern should match it, for the regular expression suites.
    /// </summary>
    internal sealed class PatternCase
    {
        internal PatternCase(string pattern, string input, bool matches)
        {
            Pattern = pattern;
            Input = input;
            Matches = matches;
        }

        internal string Pattern { get; }

        internal string Input { get; }

        internal bool Matches { get; }
    }
}
