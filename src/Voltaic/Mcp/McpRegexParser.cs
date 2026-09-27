namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    /// <summary>
    /// Parses an ECMA-262 regular expression with the syntax of the <c>u</c> (Unicode) flag, which JSON Schema
    /// recommends for <c>pattern</c>, into a tree of <see cref="McpRegexNode"/>. Three forms that only Annex B (the
    /// non-Unicode syntax) allows are also accepted, because they are common and unambiguous: an identity escape of any
    /// character that is not an ASCII letter or digit (such as <c>\_</c>), a literal <c>{</c>, <c>}</c>, or <c>]</c>
    /// outside a quantifier or class, and a class escape at either end of a class range (<c>[\w-.]</c>, where the hyphen
    /// is literal). The ES2025 additions are supported: duplicate named groups in different alternatives, and pattern
    /// modifiers (<c>(?ims-ims:...)</c>), which the parser resolves into case-folded sets and flagged assertions and
    /// backreferences. Anything else that is not valid ECMA-262 throws <see cref="ArgumentException"/>.
    /// </summary>
    internal sealed class McpRegexParser
    {
        private readonly string _Pattern;
        private readonly List<int> _CodePoints = new List<int>();
        private readonly Dictionary<string, List<int>> _GroupNames = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        private readonly Dictionary<int, int[]> _GroupPaths = new Dictionary<int, int[]>();
        private readonly List<int> _AlternativePath = new List<int>();
        private readonly List<McpRegexNode> _Backreferences = new List<McpRegexNode>();
        private const int _MaxNesting = 500;
        private const int _ModifierIgnoreCase = 1;
        private const int _ModifierMultiline = 2;
        private const int _ModifierDotAll = 4;
        private int _Position;
        private int _CaptureCount;
        private int _Nesting;
        private int _Disjunctions;
        private bool _IgnoreCase;
        private bool _Multiline;
        private bool _DotAll;

        private McpRegexParser(string pattern)
        {
            _Pattern = pattern;
            for (int i = 0; i < pattern.Length; i++)
            {
                if (Char.IsHighSurrogate(pattern[i]) && i + 1 < pattern.Length && Char.IsLowSurrogate(pattern[i + 1]))
                {
                    _CodePoints.Add(Char.ConvertToUtf32(pattern[i], pattern[i + 1]));
                    i++;
                }
                else
                {
                    _CodePoints.Add(pattern[i]);
                }
            }
        }

        /// <summary>
        /// Gets the number of capturing groups in the parsed pattern.
        /// </summary>
        internal int CaptureCount => _CaptureCount;

        /// <summary>
        /// Parses a pattern. Returns the root node and the capture count through <paramref name="captureCount"/>.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when the pattern is not a valid ECMA-262 regular expression, uses a construct this engine does not support (a Unicode property other than a general category, Any, ASCII, or Assigned), or nests groups more than 500 deep.</exception>
        internal static McpRegexNode Parse(string pattern, out int captureCount)
        {
            McpRegexParser parser = new McpRegexParser(pattern);
            McpRegexNode root = parser.ParseDisjunction();
            if (!parser.AtEnd) throw parser.Error(parser.Peek() == ')' ? "unmatched ')'" : "unexpected character");

            foreach (McpRegexNode reference in parser._Backreferences)
            {
                if (reference.GroupName != null)
                {
                    if (!parser._GroupNames.TryGetValue(reference.GroupName, out List<int>? indexes)) throw parser.Error($"no group named '{reference.GroupName}'");
                    reference.CaptureIndex = indexes[0];
                    if (indexes.Count > 1) reference.CaptureIndexes = indexes.ToArray();
                }
                else if (reference.CaptureIndex > parser._CaptureCount)
                {
                    throw parser.Error($"backreference \\{reference.CaptureIndex} names a group that does not exist");
                }
            }

            captureCount = parser._CaptureCount;
            return root;
        }

        private bool AtEnd => _Position >= _CodePoints.Count;

        private int Peek(int offset = 0)
        {
            int index = _Position + offset;
            return index < _CodePoints.Count ? _CodePoints[index] : -1;
        }

        private int Next()
        {
            return _CodePoints[_Position++];
        }

        private bool Accept(int codePoint)
        {
            if (Peek() != codePoint) return false;
            _Position++;
            return true;
        }

        private ArgumentException Error(string reason)
        {
            return new ArgumentException($"Invalid regular expression '{_Pattern}': {reason}.");
        }

        // The disjunction inside a group or lookaround; the pattern's own disjunction does not count toward the limit.
        private McpRegexNode ParseNestedDisjunction()
        {
            // Groups nest through recursion; a bound keeps a hostile pattern from exhausting the stack.
            if (++_Nesting > _MaxNesting) throw Error($"groups are nested more than {_MaxNesting} deep");
            try
            {
                return ParseDisjunction();
            }
            finally
            {
                _Nesting--;
            }
        }

        private McpRegexNode ParseDisjunction()
        {
            // _AlternativePath holds, for every enclosing disjunction, its number and the alternative being parsed, so
            // named groups can tell whether they might both participate.
            _AlternativePath.Add(_Disjunctions++);
            _AlternativePath.Add(0);
            try
            {
                McpRegexNode first = ParseAlternative();
                if (Peek() != '|') return first;

                McpRegexNode alternation = new McpRegexNode(McpRegexNodeKind.Alternation);
                alternation.Children.Add(first);
                while (Accept('|'))
                {
                    _AlternativePath[_AlternativePath.Count - 1]++;
                    alternation.Children.Add(ParseAlternative());
                }

                return alternation;
            }
            finally
            {
                _AlternativePath.RemoveRange(_AlternativePath.Count - 2, 2);
            }
        }

        private McpRegexNode ParseAlternative()
        {
            McpRegexNode sequence = new McpRegexNode(McpRegexNodeKind.Sequence);
            while (!AtEnd && Peek() != '|' && Peek() != ')')
            {
                sequence.Children.Add(ParseTerm());
            }

            return sequence;
        }

        private McpRegexNode ParseTerm()
        {
            int start = Peek();

            // Assertions, which cannot be quantified with the u flag.
            if (start == '^') { _Position++; return new McpRegexNode(McpRegexNodeKind.Start) { Multiline = _Multiline }; }
            if (start == '$') { _Position++; return new McpRegexNode(McpRegexNodeKind.End) { Multiline = _Multiline }; }
            if (start == '\\' && (Peek(1) == 'b' || Peek(1) == 'B'))
            {
                _Position += 2;
                McpRegexNodeKind kind = Peek(-1) == 'b' ? McpRegexNodeKind.WordBoundary : McpRegexNodeKind.NotWordBoundary;
                return RejectQuantifier(new McpRegexNode(kind) { IgnoreCase = _IgnoreCase });
            }

            if (start == '(' && Peek(1) == '?' && (Peek(2) == '=' || Peek(2) == '!' || (Peek(2) == '<' && (Peek(3) == '=' || Peek(3) == '!'))))
            {
                _Position += 2;
                bool behind = Accept('<');
                bool negated = Next() == '!';
                McpRegexNode look = new McpRegexNode(McpRegexNodeKind.Lookaround) { Behind = behind, Negated = negated };
                look.Children.Add(ParseNestedDisjunction());
                if (!Accept(')')) throw Error("unterminated group");
                return RejectQuantifier(look);
            }

            int capturesBefore = _CaptureCount;
            McpRegexNode atom = ParseAtom();
            return ParseQuantifier(atom, capturesBefore);
        }

        private McpRegexNode RejectQuantifier(McpRegexNode assertion)
        {
            int next = Peek();
            if (next == '*' || next == '+' || next == '?' || (next == '{' && TryReadQuantifierBounds(out int _, out int _, out int _)))
            {
                throw Error("an assertion cannot be quantified");
            }

            return assertion;
        }

        private McpRegexNode ParseQuantifier(McpRegexNode atom, int capturesBefore)
        {
            int min;
            int max;
            int next = Peek();
            if (next == '*') { _Position++; min = 0; max = -1; }
            else if (next == '+') { _Position++; min = 1; max = -1; }
            else if (next == '?') { _Position++; min = 0; max = 1; }
            else if (next == '{' && TryReadQuantifierBounds(out min, out max, out int length)) { _Position += length; }
            else return atom;

            if (max >= 0 && min > max) throw Error("numbers out of order in a {} quantifier");
            McpRegexNode quantifier = new McpRegexNode(McpRegexNodeKind.Quantifier)
            {
                Min = min,
                Max = max,
                Greedy = !Accept('?'),
                FirstCapture = capturesBefore + 1,
                LastCapture = _CaptureCount
            };
            quantifier.Children.Add(atom);

            int after = Peek();
            if (after == '*' || after == '+' || after == '?' || (after == '{' && TryReadQuantifierBounds(out int _, out int _, out int _)))
            {
                throw Error("nothing to repeat");
            }

            return quantifier;
        }

        // Reads {n}, {n,}, or {n,m} at the current position without consuming it.
        private bool TryReadQuantifierBounds(out int min, out int max, out int length)
        {
            min = 0;
            max = 0;
            length = 0;
            int index = 1;
            if (!TryReadNumber(ref index, out min)) return false;
            if (Peek(index) == '}')
            {
                max = min;
                length = index + 1;
                return true;
            }

            if (Peek(index) != ',') return false;
            index++;
            if (Peek(index) == '}')
            {
                max = -1;
                length = index + 1;
                return true;
            }

            if (!TryReadNumber(ref index, out max) || Peek(index) != '}') return false;
            length = index + 1;
            return true;
        }

        private bool TryReadNumber(ref int index, out int value)
        {
            value = 0;
            int start = index;
            while (Peek(index) >= '0' && Peek(index) <= '9')
            {
                value = value > (Int32.MaxValue - 9) / 10 ? Int32.MaxValue : value * 10 + (Peek(index) - '0');
                index++;
            }

            return index > start;
        }

        private McpRegexNode ParseAtom()
        {
            int current = Next();
            switch (current)
            {
                case '.':
                    // Line terminators have no case variants, so i never changes what . matches; s makes it match all.
                    return Set(_DotAll ? McpRegexCharSet.All : McpRegexCharSet.LineTerminators.Complement());
                case '(':
                    return ParseGroup();
                case '[':
                    // Under i a class matches every character that canonicalizes like a member; a negated class matches
                    // the rest (ECMA-262 CharacterSetMatcher with invert).
                    McpRegexCharSet members = ParseClass(out bool negated);
                    if (_IgnoreCase) members = members.CaseClosure();
                    return Set(negated ? members.Complement() : members);
                case '\\':
                    return ParseAtomEscape();
                case '*':
                case '+':
                case '?':
                    throw Error("nothing to repeat");
                case ')':
                case '|':
                    throw Error("unexpected character");
                default:
                    // '{', '}', and ']' outside a quantifier or class are literal (Annex B), but a complete {n,m} with
                    // nothing before it is a syntax error in both ECMA-262 grammars.
                    if (current == '{')
                    {
                        _Position--;
                        bool quantifier = TryReadQuantifierBounds(out int _, out int _, out int _);
                        _Position++;
                        if (quantifier) throw Error("nothing to repeat");
                    }

                    return Set(Folded(McpRegexCharSet.Single(current)));
            }
        }

        private McpRegexNode ParseGroup()
        {
            McpRegexNode group = new McpRegexNode(McpRegexNodeKind.Group);
            bool ignoreCase = _IgnoreCase;
            bool multiline = _Multiline;
            bool dotAll = _DotAll;
            if (Accept('?'))
            {
                if (Accept(':'))
                {
                    group.CaptureIndex = 0;
                }
                else if (Accept('<'))
                {
                    string name = ParseGroupName();
                    group.CaptureIndex = ++_CaptureCount;
                    AddGroupName(name, group.CaptureIndex);
                }
                else
                {
                    group.CaptureIndex = 0;
                    ParseModifiers();
                }
            }
            else
            {
                group.CaptureIndex = ++_CaptureCount;
            }

            try
            {
                group.Children.Add(ParseNestedDisjunction());
            }
            finally
            {
                // Modifiers apply to the group's contents only.
                _IgnoreCase = ignoreCase;
                _Multiline = multiline;
                _DotAll = dotAll;
            }

            if (!Accept(')')) throw Error("unterminated group");
            return group;
        }

        // ES2025: a name may repeat only in groups that can never both participate, which is when some disjunction holds
        // them in different alternatives.
        private void AddGroupName(string name, int captureIndex)
        {
            int[] path = _AlternativePath.ToArray();
            if (_GroupNames.TryGetValue(name, out List<int>? existing))
            {
                foreach (int other in existing)
                {
                    if (MightBothParticipate(_GroupPaths[other], path)) throw Error($"duplicate group name '{name}'");
                }

                existing.Add(captureIndex);
            }
            else
            {
                _GroupNames[name] = new List<int> { captureIndex };
            }

            _GroupPaths[captureIndex] = path;
        }

        // Paths list (disjunction number, alternative index) pairs from the outermost disjunction inward. Two groups are
        // exclusive when a shared disjunction holds them in different alternatives; once the disjunction numbers differ
        // the paths share nothing deeper.
        private static bool MightBothParticipate(int[] first, int[] second)
        {
            int length = Math.Min(first.Length, second.Length);
            for (int i = 0; i + 1 < length; i += 2)
            {
                if (first[i] != second[i]) return true;
                if (first[i + 1] != second[i + 1]) return false;
            }

            return true;
        }

        // Pattern modifiers (ES2025): the flags after "(?" up to ':', as (?ims:...) or (?ims-ims:...). Each flag may
        // appear once and not on both sides, and a group with '-' needs at least one flag.
        private void ParseModifiers()
        {
            int add = ReadModifierFlags();
            int remove = 0;
            bool dash = Accept('-');
            if (dash) remove = ReadModifierFlags();
            if (!Accept(':')) throw Error("invalid group");
            if (dash && add == 0 && remove == 0) throw Error("a modifier group needs at least one flag");
            if ((add & remove) != 0) throw Error("a flag cannot be both added and removed");

            if ((add & _ModifierIgnoreCase) != 0) _IgnoreCase = true;
            if ((add & _ModifierMultiline) != 0) _Multiline = true;
            if ((add & _ModifierDotAll) != 0) _DotAll = true;
            if ((remove & _ModifierIgnoreCase) != 0) _IgnoreCase = false;
            if ((remove & _ModifierMultiline) != 0) _Multiline = false;
            if ((remove & _ModifierDotAll) != 0) _DotAll = false;
        }

        private int ReadModifierFlags()
        {
            int flags = 0;
            while (!AtEnd && Peek() != ':' && Peek() != '-')
            {
                int flag = Peek() == 'i' ? _ModifierIgnoreCase : Peek() == 'm' ? _ModifierMultiline : Peek() == 's' ? _ModifierDotAll : 0;
                if (flag == 0) throw Error("invalid group");
                if ((flags & flag) != 0) throw Error("repeated flag in a modifier group");
                flags |= flag;
                _Position++;
            }

            return flags;
        }

        // Reads a group name up to and including '>' (ECMA-262 RegExpIdentifierName): an ID_Start character, '$', or '_',
        // then ID_Continue characters, '$', ZWNJ, or ZWJ; any of them may be written as a \u escape.
        private string ParseGroupName()
        {
            StringBuilder name = new StringBuilder();
            while (!AtEnd && Peek() != '>')
            {
                int codePoint = Next();
                if (codePoint == '\\')
                {
                    if (!Accept('u')) throw Error("invalid group name");
                    codePoint = ParseUnicodeEscape();
                }

                bool first = name.Length == 0;
                bool valid = codePoint == '$' || codePoint == '_' || (first ? IsIdStart(codePoint) : (IsIdContinue(codePoint) || codePoint == 0x200C || codePoint == 0x200D));
                if (!valid) throw Error("invalid group name");
                name.Append(Char.ConvertFromUtf32(codePoint));
            }

            if (!Accept('>') || name.Length == 0) throw Error("invalid group name");
            return name.ToString();
        }

        private McpRegexNode ParseAtomEscape()
        {
            if (AtEnd) throw Error("\\ at end of pattern");
            int escaped = Peek();

            if (escaped >= '1' && escaped <= '9')
            {
                int index = 0;
                TryReadNumber(ref index, out int number);
                _Position += index;
                McpRegexNode reference = new McpRegexNode(McpRegexNodeKind.Backreference) { CaptureIndex = number, IgnoreCase = _IgnoreCase };
                _Backreferences.Add(reference);
                return reference;
            }

            if (escaped == 'k')
            {
                _Position++;
                if (!Accept('<')) throw Error("invalid named reference");
                string name = ParseGroupName();
                McpRegexNode reference = new McpRegexNode(McpRegexNodeKind.Backreference) { GroupName = name, IgnoreCase = _IgnoreCase };
                _Backreferences.Add(reference);
                return reference;
            }

            McpRegexCharSet? classEscape = TryParseClassEscape();
            if (classEscape != null) return Set(Folded(classEscape));
            return Set(Folded(McpRegexCharSet.Single(ParseCharacterEscape(false))));
        }

        // \d \D \s \S \w \W \p{...} \P{...}, or null (nothing consumed).
        private McpRegexCharSet? TryParseClassEscape()
        {
            int escaped = Peek();
            switch (escaped)
            {
                case 'd': _Position++; return McpRegexCharSet.Digits;
                case 'D': _Position++; return McpRegexCharSet.Digits.Complement();
                case 's': _Position++; return McpRegexCharSet.Whitespace;
                case 'S': _Position++; return McpRegexCharSet.Whitespace.Complement();
                case 'w': _Position++; return WordCharacters();
                case 'W': _Position++; return WordCharacters().Complement();
                case 'p':
                case 'P':
                    _Position++;
                    McpRegexCharSet property = ParseProperty();
                    return escaped == 'p' ? property : property.Complement();
                default:
                    return null;
            }
        }

        private McpRegexCharSet ParseProperty()
        {
            if (!Accept('{')) throw Error("invalid Unicode property escape");
            StringBuilder text = new StringBuilder();
            while (!AtEnd && Peek() != '}') text.Append(Char.ConvertFromUtf32(Next()));
            if (!Accept('}')) throw Error("invalid Unicode property escape");

            string property = text.ToString();
            string? value = property;
            int equals = property.IndexOf('=');
            if (equals >= 0)
            {
                string key = property.Substring(0, equals);
                value = property.Substring(equals + 1);
                if (key != "gc" && key != "General_Category")
                {
                    throw Error($"the Unicode property '{property}' is not supported (general categories, Any, ASCII, and Assigned are)");
                }
            }
            else
            {
                if (property == "Any") return McpRegexCharSet.All;
                if (property == "ASCII") return McpRegexCharSet.Range(0, 0x7F);
                if (property == "Assigned") return (McpRegexCharSet.Category("Cn") ?? McpRegexCharSet.Empty).Complement();
            }

            string? code = GeneralCategoryCode(value);
            McpRegexCharSet? set = code == null ? null : McpRegexCharSet.Category(code);
            if (set == null) throw Error($"the Unicode property '{property}' is not supported (general categories, Any, ASCII, and Assigned are)");
            return set;
        }

        // The short code of an ECMA-262 General_Category value given by its short or long name.
        private static string? GeneralCategoryCode(string name)
        {
            switch (name)
            {
                case "L": case "Letter": return "L";
                case "LC": case "Cased_Letter": return "LC";
                case "Lu": case "Uppercase_Letter": return "Lu";
                case "Ll": case "Lowercase_Letter": return "Ll";
                case "Lt": case "Titlecase_Letter": return "Lt";
                case "Lm": case "Modifier_Letter": return "Lm";
                case "Lo": case "Other_Letter": return "Lo";
                case "M": case "Mark": case "Combining_Mark": return "M";
                case "Mn": case "Nonspacing_Mark": return "Mn";
                case "Mc": case "Spacing_Mark": return "Mc";
                case "Me": case "Enclosing_Mark": return "Me";
                case "N": case "Number": return "N";
                case "Nd": case "Decimal_Number": case "digit": return "Nd";
                case "Nl": case "Letter_Number": return "Nl";
                case "No": case "Other_Number": return "No";
                case "P": case "Punctuation": case "punct": return "P";
                case "Pc": case "Connector_Punctuation": return "Pc";
                case "Pd": case "Dash_Punctuation": return "Pd";
                case "Ps": case "Open_Punctuation": return "Ps";
                case "Pe": case "Close_Punctuation": return "Pe";
                case "Pi": case "Initial_Punctuation": return "Pi";
                case "Pf": case "Final_Punctuation": return "Pf";
                case "Po": case "Other_Punctuation": return "Po";
                case "S": case "Symbol": return "S";
                case "Sm": case "Math_Symbol": return "Sm";
                case "Sc": case "Currency_Symbol": return "Sc";
                case "Sk": case "Modifier_Symbol": return "Sk";
                case "So": case "Other_Symbol": return "So";
                case "Z": case "Separator": return "Z";
                case "Zs": case "Space_Separator": return "Zs";
                case "Zl": case "Line_Separator": return "Zl";
                case "Zp": case "Paragraph_Separator": return "Zp";
                case "C": case "Other": return "C";
                case "Cc": case "Control": case "cntrl": return "Cc";
                case "Cf": case "Format": return "Cf";
                case "Cs": case "Surrogate": return "Cs";
                case "Co": case "Private_Use": return "Co";
                case "Cn": case "Unassigned": return "Cn";
                default: return null;
            }
        }

        // A CharacterEscape (the backslash already consumed and the escape not a class escape); returns its code point.
        private int ParseCharacterEscape(bool inClass)
        {
            if (AtEnd) throw Error("\\ at end of pattern");
            int escaped = Next();
            switch (escaped)
            {
                case 'f': return 0x0C;
                case 'n': return 0x0A;
                case 'r': return 0x0D;
                case 't': return 0x09;
                case 'v': return 0x0B;
                case 'b':
                    if (inClass) return 0x08;
                    throw Error("invalid escape");
                case '-':
                    return '-';
                case 'c':
                    int letter = Peek();
                    if ((letter >= 'a' && letter <= 'z') || (letter >= 'A' && letter <= 'Z'))
                    {
                        _Position++;
                        return letter % 32;
                    }

                    throw Error("invalid \\c escape");
                case '0':
                    if (IsDigit(Peek())) throw Error("octal escapes are not allowed");
                    return 0;
                case 'x':
                    return ReadHex(2);
                case 'u':
                    return ParseUnicodeEscape();
                default:
                    // With the u flag only syntax characters and '/' may be escaped; any other character that is not an
                    // ASCII letter or digit is accepted too (Annex B identity escape).
                    if ((escaped >= 'a' && escaped <= 'z') || (escaped >= 'A' && escaped <= 'Z') || IsDigit(escaped))
                    {
                        throw Error($"'\\{Char.ConvertFromUtf32(escaped)}' is not a valid escape");
                    }

                    return escaped;
            }
        }

        // \uXXXX, \uXXXX\uXXXX (a surrogate pair), or \u{X...} (the 'u' already consumed).
        private int ParseUnicodeEscape()
        {
            if (Accept('{'))
            {
                int value = 0;
                int digits = 0;
                while (!AtEnd && Peek() != '}')
                {
                    int digit = HexValue(Next());
                    if (digit < 0) throw Error("invalid \\u{...} escape");
                    value = value * 16 + digit;
                    digits++;
                    if (value > McpRegexCharSet.MaxCodePoint) throw Error("\\u{...} escape out of range");
                }

                if (!Accept('}') || digits == 0) throw Error("invalid \\u{...} escape");
                return value;
            }

            int unit = ReadHex(4);
            if (unit >= 0xD800 && unit <= 0xDBFF && Peek() == '\\' && Peek(1) == 'u')
            {
                int save = _Position;
                _Position += 2;
                if (!(Peek() == '{'))
                {
                    int low = ReadHexOrNegative(4);
                    if (low >= 0xDC00 && low <= 0xDFFF) return Char.ConvertToUtf32((char)unit, (char)low);
                }

                _Position = save;
            }

            return unit;
        }

        private int ReadHex(int count)
        {
            int value = ReadHexOrNegative(count);
            if (value < 0) throw Error("invalid hexadecimal escape");
            return value;
        }

        private int ReadHexOrNegative(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int digit = HexValue(Peek());
                if (digit < 0) return -1;
                _Position++;
                value = value * 16 + digit;
            }

            return value;
        }

        private static int HexValue(int codePoint)
        {
            if (codePoint >= '0' && codePoint <= '9') return codePoint - '0';
            if (codePoint >= 'a' && codePoint <= 'f') return codePoint - 'a' + 10;
            if (codePoint >= 'A' && codePoint <= 'F') return codePoint - 'A' + 10;
            return -1;
        }

        // ECMA-262 WordCharacters, which under i with the u flag also holds U+017F and U+212A.
        private McpRegexCharSet WordCharacters()
        {
            return _IgnoreCase ? McpRegexCharSet.WordCharactersIgnoreCase : McpRegexCharSet.WordCharacters;
        }

        // A set as an atom matches under i: closed over case folding.
        private McpRegexCharSet Folded(McpRegexCharSet set)
        {
            return _IgnoreCase ? set.CaseClosure() : set;
        }

        // A character class (the '[' already consumed): its members, before case folding and negation.
        private McpRegexCharSet ParseClass(out bool negated)
        {
            negated = Accept('^');
            McpRegexCharSet members = McpRegexCharSet.Empty;
            while (true)
            {
                if (AtEnd) throw Error("unterminated character class");
                if (Accept(']')) break;

                McpRegexClassAtom first = ParseClassAtom();
                if (Peek() == '-' && Peek(1) != ']' && Peek(1) != -1)
                {
                    _Position++;
                    McpRegexClassAtom second = ParseClassAtom();
                    if (first.Set != null || second.Set != null)
                    {
                        // A class escape at either end: the hyphen is literal (Annex B).
                        members = members.Union(first.ToSet()).Union(McpRegexCharSet.Single('-')).Union(second.ToSet());
                        continue;
                    }

                    if (first.CodePoint > second.CodePoint) throw Error("range out of order in character class");
                    members = members.Union(McpRegexCharSet.Range(first.CodePoint, second.CodePoint));
                    continue;
                }

                members = members.Union(first.ToSet());
            }

            return members;
        }

        private McpRegexClassAtom ParseClassAtom()
        {
            int current = Next();
            if (current != '\\') return new McpRegexClassAtom(current, null);
            McpRegexCharSet? classEscape = TryParseClassEscape();
            if (classEscape != null) return new McpRegexClassAtom(-1, classEscape);
            return new McpRegexClassAtom(ParseCharacterEscape(true), null);
        }

        private static McpRegexNode Set(McpRegexCharSet set)
        {
            return new McpRegexNode(McpRegexNodeKind.CharSet) { Set = set };
        }

        private static bool IsDigit(int codePoint)
        {
            return codePoint >= '0' && codePoint <= '9';
        }

        // Unicode ID_Start: letters, letter numbers, and Other_ID_Start, without Pattern_Syntax and Pattern_White_Space.
        private static bool IsIdStart(int codePoint)
        {
            if (codePoint < 0 || IsPatternSyntaxOrWhiteSpace(codePoint)) return false;
            if (codePoint == 0x1885 || codePoint == 0x1886 || codePoint == 0x2118 || codePoint == 0x212E || codePoint == 0x309B || codePoint == 0x309C) return true;
            return IsLetter(codePoint);
        }

        // Unicode ID_Continue: ID_Start, marks, decimal digits, connector punctuation, and Other_ID_Continue.
        private static bool IsIdContinue(int codePoint)
        {
            if (IsIdStart(codePoint)) return true;
            if (codePoint < 0 || IsPatternSyntaxOrWhiteSpace(codePoint)) return false;
            if (codePoint == 0x00B7 || codePoint == 0x0387 || (codePoint >= 0x1369 && codePoint <= 0x1371) || codePoint == 0x19DA || codePoint == 0x200C || codePoint == 0x200D || codePoint == 0x30FB || codePoint == 0xFF65) return true;
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
            return category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.SpacingCombiningMark
                || category == UnicodeCategory.DecimalDigitNumber || category == UnicodeCategory.ConnectorPunctuation;
        }

        // Pattern_Syntax and Pattern_White_Space (stable Unicode properties), which identifiers never contain.
        private static bool IsPatternSyntaxOrWhiteSpace(int codePoint)
        {
            return (codePoint >= 0x21 && codePoint <= 0x2F) || (codePoint >= 0x3A && codePoint <= 0x40) || (codePoint >= 0x5B && codePoint <= 0x5E)
                || codePoint == 0x60 || (codePoint >= 0x7B && codePoint <= 0x7E) || (codePoint >= 0xA1 && codePoint <= 0xA7) || codePoint == 0xA9
                || codePoint == 0xAB || codePoint == 0xAC || codePoint == 0xAE || codePoint == 0xB0 || codePoint == 0xB1 || codePoint == 0xB6
                || codePoint == 0xBB || codePoint == 0xBF || codePoint == 0xD7 || codePoint == 0xF7 || (codePoint >= 0x2010 && codePoint <= 0x2027)
                || (codePoint >= 0x2030 && codePoint <= 0x203E) || (codePoint >= 0x2041 && codePoint <= 0x2053) || (codePoint >= 0x2055 && codePoint <= 0x205E)
                || (codePoint >= 0x2190 && codePoint <= 0x245F) || (codePoint >= 0x2500 && codePoint <= 0x2775) || (codePoint >= 0x2794 && codePoint <= 0x2BFF)
                || (codePoint >= 0x2E00 && codePoint <= 0x2E7F) || (codePoint >= 0x3001 && codePoint <= 0x3003) || (codePoint >= 0x3008 && codePoint <= 0x3020)
                || codePoint == 0x3030 || codePoint == 0xFD3E || codePoint == 0xFD3F || codePoint == 0xFE45 || codePoint == 0xFE46
                || (codePoint >= 0x09 && codePoint <= 0x0D) || codePoint == 0x20 || codePoint == 0x85 || codePoint == 0x200E || codePoint == 0x200F
                || codePoint == 0x2028 || codePoint == 0x2029;
        }

        private static bool IsLetter(int codePoint)
        {
            if (codePoint < 0) return false;
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
            return category == UnicodeCategory.UppercaseLetter || category == UnicodeCategory.LowercaseLetter || category == UnicodeCategory.TitlecaseLetter
                || category == UnicodeCategory.ModifierLetter || category == UnicodeCategory.OtherLetter || category == UnicodeCategory.LetterNumber;
        }
    }
}
