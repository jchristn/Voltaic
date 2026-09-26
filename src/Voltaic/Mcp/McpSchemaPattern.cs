namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    /// <summary>
    /// Translates an ECMA-262 regular expression (the dialect JSON Schema <c>pattern</c> uses) into a .NET pattern with
    /// the same meaning whichever .NET options run it: <c>$</c> matches only at the end of the input, <c>.</c> excludes
    /// the four ECMA-262 line terminators, <c>\d</c> and <c>\w</c> are ASCII, <c>\s</c> is the ECMA-262 white space and
    /// line terminator set, <c>\u{...}</c> is a code point, and Unicode property escapes accept the ECMA-262 general
    /// category names. Syntax that ECMA-262 does not define but .NET would give its own meaning (<c>\A</c>, <c>\Z</c>,
    /// <c>\z</c>, <c>\G</c>, <c>\a</c>, <c>\e</c>, inline options, comments, atomic and conditional groups) and script
    /// properties, which .NET does not support, are rejected with <see cref="ArgumentException"/>.
    /// </summary>
    internal static class McpSchemaPattern
    {
        private const string _Whitespace = "\\t\\n\\u000B\\f\\r \\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000\\uFEFF";
        private const string _Digits = "0-9";
        private const string _WordCharacters = "a-zA-Z0-9_";
        private const string _AnyButLineTerminator = "[^\\n\\r\\u2028\\u2029]";
        private const string _EndOfInput = "(?![\\s\\S])";
        private const string _NetOnlyEscapes = "AZzGae";

        private static readonly Dictionary<string, string> _GeneralCategories = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Letter", "L" }, { "Cased_Letter", "L" }, { "Lowercase_Letter", "Ll" }, { "Uppercase_Letter", "Lu" }, { "Titlecase_Letter", "Lt" },
            { "Modifier_Letter", "Lm" }, { "Other_Letter", "Lo" }, { "Mark", "M" }, { "Combining_Mark", "M" }, { "Nonspacing_Mark", "Mn" },
            { "Spacing_Mark", "Mc" }, { "Enclosing_Mark", "Me" }, { "Number", "N" }, { "Decimal_Number", "Nd" }, { "digit", "Nd" },
            { "Letter_Number", "Nl" }, { "Other_Number", "No" }, { "Punctuation", "P" }, { "punct", "P" }, { "Connector_Punctuation", "Pc" },
            { "Dash_Punctuation", "Pd" }, { "Open_Punctuation", "Ps" }, { "Close_Punctuation", "Pe" }, { "Initial_Punctuation", "Pi" },
            { "Final_Punctuation", "Pf" }, { "Other_Punctuation", "Po" }, { "Symbol", "S" }, { "Math_Symbol", "Sm" }, { "Currency_Symbol", "Sc" },
            { "Modifier_Symbol", "Sk" }, { "Other_Symbol", "So" }, { "Separator", "Z" }, { "Space_Separator", "Zs" }, { "Line_Separator", "Zl" },
            { "Paragraph_Separator", "Zp" }, { "Other", "C" }, { "Control", "Cc" }, { "cntrl", "Cc" }, { "Format", "Cf" }, { "Surrogate", "Cs" },
            { "Private_Use", "Co" }, { "Unassigned", "Cn" }
        };

        /// <summary>
        /// Returns the .NET equivalent of an ECMA-262 pattern.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when the pattern uses syntax that is not ECMA-262 or that .NET cannot express.</exception>
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
                        position += 2;
                    }
                    else
                    {
                        position = TranslateEscape(pattern, position, output, false);
                    }
                }
                else if (current == '[')
                {
                    position = TranslateClass(pattern, position, output);
                }
                else if (current == '(' && position + 1 < pattern.Length && pattern[position + 1] == '?')
                {
                    CheckGroup(pattern, position);
                    output.Append(current);
                    position++;
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

        // ECMA-262 groups: (?:, (?=, (?!, (?<=, (?<!, and (?<name>. Anything else is .NET-only syntax.
        private static void CheckGroup(string pattern, int position)
        {
            int next = position + 2;
            if (next >= pattern.Length) throw new ArgumentException("Incomplete group.");
            char kind = pattern[next];
            if (kind == ':' || kind == '=' || kind == '!') return;
            if (kind == '<' && next + 1 < pattern.Length)
            {
                char after = pattern[next + 1];
                if (after == '=' || after == '!' || Char.IsLetter(after) || after == '_' || after == '$') return;
            }

            throw new ArgumentException($"The group syntax '(?{kind}' is not ECMA-262.");
        }

        // Translates the escape at position (a backslash) other than the class shorthands, and returns the position
        // after it.
        private static int TranslateEscape(string pattern, int position, StringBuilder output, bool inClass)
        {
            char escaped = pattern[position + 1];
            if (_NetOnlyEscapes.IndexOf(escaped) >= 0)
            {
                throw new ArgumentException($"'\\{escaped}' is not an ECMA-262 escape.");
            }

            if (escaped == 'u' && position + 2 < pattern.Length && pattern[position + 2] == '{')
            {
                int close = pattern.IndexOf('}', position + 3);
                if (close < 0 || !Int32.TryParse(pattern.Substring(position + 3, close - position - 3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codePoint)
                    || codePoint < 0 || codePoint > 0x10FFFF)
                {
                    throw new ArgumentException("Invalid \\u{...} escape.");
                }

                if (codePoint <= 0xFFFF)
                {
                    output.Append("\\u").Append(codePoint.ToString("X4", CultureInfo.InvariantCulture));
                }
                else
                {
                    if (inClass) throw new ArgumentException("A code point above U+FFFF cannot be a character class member.");
                    string pair = Char.ConvertFromUtf32(codePoint);
                    output.Append("(?:\\u").Append(((int)pair[0]).ToString("X4", CultureInfo.InvariantCulture))
                        .Append("\\u").Append(((int)pair[1]).ToString("X4", CultureInfo.InvariantCulture)).Append(')');
                }

                return close + 1;
            }

            if ((escaped == 'p' || escaped == 'P') && position + 2 < pattern.Length && pattern[position + 2] == '{')
            {
                int close = pattern.IndexOf('}', position + 3);
                if (close < 0) throw new ArgumentException("Invalid Unicode property escape.");
                string property = pattern.Substring(position + 3, close - position - 3);
                output.Append('\\').Append(escaped).Append('{').Append(PropertyName(property)).Append('}');
                return close + 1;
            }

            output.Append('\\').Append(escaped);
            return position + 2;
        }

        // Maps an ECMA-262 Unicode property name to the .NET name: general categories by short or long name, with or
        // without gc= or General_Category=. Script properties have no .NET equivalent.
        private static string PropertyName(string property)
        {
            string name = property;
            int equals = property.IndexOf('=');
            if (equals >= 0)
            {
                string key = property.Substring(0, equals);
                name = property.Substring(equals + 1);
                if (key != "gc" && key != "General_Category")
                {
                    throw new ArgumentException($"The Unicode property '{property}' is not supported (only general categories are).");
                }
            }

            return _GeneralCategories.TryGetValue(name, out string? shortName) ? shortName : name;
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
            bool afterSet = false;
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
                    if (set == null)
                    {
                        position = TranslateEscape(pattern, position, body, true);
                        afterSet = false;
                        continue;
                    }

                    // A hyphen next to a class shorthand is a literal hyphen (ECMA-262 Annex B), never a range.
                    if (body.Length > 1 && body[body.Length - 1] == '-' && body[body.Length - 2] != '\\')
                    {
                        body.Length--;
                        body.Append("\\-");
                    }

                    if (Char.IsUpper(escaped)) negatedSets.Add(set);
                    else body.Append(set);
                    afterSet = true;
                    position += 2;
                    continue;
                }

                if (current == '-' && afterSet)
                {
                    body.Append("\\-");
                }
                else if (current == '[')
                {
                    // A literal '[' inside a class is escaped so .NET does not read '[:' or '-[' as its own syntax.
                    body.Append("\\[");
                }
                else
                {
                    body.Append(current);
                }

                afterSet = false;
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

            // Re-wrapped members must not start with '^', which would negate them.
            if (members.StartsWith("^", StringComparison.Ordinal)) members = "\\" + members;

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
