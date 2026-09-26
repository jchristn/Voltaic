namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// Translates an ECMA-262 regular expression (the dialect JSON Schema <c>pattern</c> uses) into a .NET pattern with
    /// the same meaning whichever .NET options run it: <c>$</c> matches only at the end of the input, <c>.</c> excludes
    /// the four ECMA-262 line terminators, <c>\d</c> and <c>\w</c> are ASCII, and <c>\s</c> is the ECMA-262 white
    /// space and line terminator set.
    /// </summary>
    internal static class McpSchemaPattern
    {
        private const string _Whitespace = "\\t\\n\\u000B\\f\\r \\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000\\uFEFF";
        private const string _Digits = "0-9";
        private const string _WordCharacters = "a-zA-Z0-9_";
        private const string _AnyButLineTerminator = "[^\\n\\r\\u2028\\u2029]";
        private const string _EndOfInput = "(?![\\s\\S])";

        /// <summary>
        /// Returns the .NET equivalent of an ECMA-262 pattern.
        /// </summary>
        internal static string Translate(string pattern)
        {
            StringBuilder output = new StringBuilder(pattern.Length + 16);
            int position = 0;
            while (position < pattern.Length)
            {
                char current = pattern[position];
                if (current == '\\' && position + 1 < pattern.Length)
                {
                    char escaped = pattern[position + 1];
                    string? set = SetFor(escaped);
                    if (set != null)
                    {
                        output.Append(Char.IsUpper(escaped) ? "[^" : "[").Append(set).Append(']');
                    }
                    else
                    {
                        output.Append(current).Append(escaped);
                    }

                    position += 2;
                }
                else if (current == '[')
                {
                    position = TranslateClass(pattern, position, output);
                }
                else if (current == '.')
                {
                    output.Append(_AnyButLineTerminator);
                    position++;
                }
                else if (current == '$')
                {
                    output.Append(_EndOfInput);
                    position++;
                }
                else
                {
                    output.Append(current);
                    position++;
                }
            }

            return output.ToString();
        }

        // Returns the class body for a shorthand escape (\d, \w, \s and their negations), or null for other escapes.
        private static string? SetFor(char escaped)
        {
            switch (escaped)
            {
                case 'd':
                case 'D':
                    return _Digits;
                case 'w':
                case 'W':
                    return _WordCharacters;
                case 's':
                case 'S':
                    return _Whitespace;
                default:
                    return null;
            }
        }

        // Translates the character class starting at position (a '['), appends it, and returns the position after it.
        // In ECMA-262 a ']' right after '[' or '[^' ends the class, so '[]' matches nothing and '[^]' matches anything.
        private static int TranslateClass(string pattern, int position, StringBuilder output)
        {
            int start = position;
            position++;
            bool negated = position < pattern.Length && pattern[position] == '^';
            if (negated) position++;

            StringBuilder body = new StringBuilder();
            List<string> negatedSets = new List<string>();
            bool closed = false;
            while (position < pattern.Length)
            {
                char current = pattern[position];
                if (current == ']')
                {
                    closed = true;
                    position++;
                    break;
                }

                if (current == '\\' && position + 1 < pattern.Length)
                {
                    char escaped = pattern[position + 1];
                    string? set = SetFor(escaped);
                    if (set == null) body.Append(current).Append(escaped);
                    else if (Char.IsUpper(escaped)) negatedSets.Add(set);
                    else body.Append(set);
                    position += 2;
                    continue;
                }

                // A literal '[' inside a class is escaped so .NET does not read '[:' or '-[' as its own syntax.
                if (current == '[') body.Append("\\[");
                else body.Append(current);
                position++;
            }

            if (!closed)
            {
                // Unterminated: leave it for the regular expression parser to reject.
                output.Append(pattern, start, pattern.Length - start);
                return pattern.Length;
            }

            string members = body.ToString();
            if (negatedSets.Count == 0)
            {
                if (members.Length == 0) output.Append(negated ? "[\\s\\S]" : "(?!)");
                else output.Append(negated ? "[^" : "[").Append(members).Append(']');
                return position;
            }

            if (!negated)
            {
                // [X\S] is X or not-whitespace: one alternative per part.
                List<string> alternatives = new List<string>();
                if (members.Length > 0) alternatives.Add("[" + members + "]");
                foreach (string set in negatedSets) alternatives.Add("[^" + set + "]");
                output.Append("(?:").Append(String.Join("|", alternatives)).Append(')');
                return position;
            }

            // [^X\S] is not X and in every negated shorthand's set: lookaheads for all but the last, which consumes.
            output.Append("(?:");
            if (members.Length > 0) output.Append("(?![").Append(members).Append("])");
            for (int i = 0; i < negatedSets.Count - 1; i++) output.Append("(?=[").Append(negatedSets[i]).Append("])");
            output.Append('[').Append(negatedSets[negatedSets.Count - 1]).Append("])");
            return position;
        }
    }
}
