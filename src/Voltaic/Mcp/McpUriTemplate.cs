namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Matches URIs against an RFC 6570 URI template (the resource template format MCP uses) and extracts the variable
    /// values. Supports simple (<c>{var}</c>), reserved (<c>{+var}</c>), fragment (<c>{#var}</c>), label
    /// (<c>{.var}</c>), path-segment (<c>{/var}</c>), path-parameter (<c>{;var}</c>), and query (<c>{?a,b}</c>,
    /// <c>{&amp;a}</c>) expressions, with several variables per expression and the <c>:n</c> prefix and <c>*</c> explode
    /// modifiers (an exploded variable receives its raw text, separators included). Variable names may contain dots and
    /// percent-encoded characters. Values are percent-decoded. Thread-safe.
    /// </summary>
    internal sealed class McpUriTemplate
    {
        // RFC 6570 varname: varchar *( ["."] varchar ), where varchar is ALPHA / DIGIT / "_" / pct-encoded.
        private static readonly Regex _VariableName = new Regex("^(?:[A-Za-z0-9_]|%[0-9A-Fa-f]{2})+(?:\\.(?:[A-Za-z0-9_]|%[0-9A-Fa-f]{2})+)*$", RegexOptions.CultureInvariant);
        private readonly Regex _Pattern;
        private readonly Dictionary<string, string> _Groups = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<List<string>> _QueryExpressions = new List<List<string>>();
        private readonly List<string> _QueryGroups = new List<string>();

        /// <summary>
        /// Parses a template.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when the template has an unterminated expression or an invalid variable name.</exception>
        internal McpUriTemplate(string template)
        {
            if (String.IsNullOrEmpty(template)) throw new ArgumentNullException(nameof(template));
            StringBuilder pattern = new StringBuilder("^");
            int groupIndex = 0;

            for (int i = 0; i < template.Length; i++)
            {
                if (template[i] != '{')
                {
                    pattern.Append(Regex.Escape(template[i].ToString()));
                    continue;
                }

                int end = template.IndexOf('}', i + 1);
                if (end < 0) throw new ArgumentException($"Resource template '{template}' has an unterminated expression.", nameof(template));

                string expression = template.Substring(i + 1, end - i - 1);
                i = end;
                if (expression.Length == 0) throw new ArgumentException($"Resource template '{template}' has an empty expression.", nameof(template));

                char op = "+#./;?&".IndexOf(expression[0]) >= 0 ? expression[0] : '\0';
                string list = op == '\0' ? expression : expression.Substring(1);
                List<string> names = new List<string>();
                List<bool> exploded = new List<bool>();
                foreach (string spec in list.Split(','))
                {
                    exploded.Add(spec.EndsWith("*", StringComparison.Ordinal));
                    string name = spec.TrimEnd('*');
                    int colon = name.IndexOf(':');
                    if (colon >= 0) name = name.Substring(0, colon);
                    if (!_VariableName.IsMatch(name)) throw new ArgumentException($"Invalid resource template variable '{spec}'.", nameof(template));
                    names.Add(name);
                }

                if (op == '?' || op == '&')
                {
                    // Query parameters may appear in any order; the whole query part is captured and parsed after matching.
                    string group = "q" + groupIndex++;
                    _QueryExpressions.Add(names);
                    _QueryGroups.Add(group);
                    pattern.Append("(?<").Append(group).Append(">(?:[?&][^#]*)?)");
                    continue;
                }

                for (int n = 0; n < names.Count; n++)
                {
                    string group = "v" + groupIndex++;
                    _Groups[group] = names[n];
                    // Reserved (+) and fragment (#) values may contain reserved characters, commas included, so they are
                    // matched lazily; an exploded variable receives the raw text of all its items, separators included.
                    bool explode = exploded[n];
                    string value = op switch
                    {
                        '+' => ".*?",
                        '#' => ".*?",
                        '.' => explode ? "[^/?#]*" : "[^/?#.,]*",
                        '/' => explode ? "[^?#]*" : "[^/?#,]*",
                        ';' => explode ? "[^/?#]*" : "[^;/?#,]*",
                        _ => explode ? "[^/?#]+" : "[^/?#,&=]+"
                    };

                    string capture = "(?<" + group + ">" + value + ")";
                    string separator = n == 0
                        ? op switch { '#' => "#", '.' => "\\.", '/' => "/", ';' => ";", _ => "" }
                        : op switch { '.' => "\\.", '/' => "/", ';' => ";", _ => "," };

                    if (op == ';')
                    {
                        pattern.Append("(?:").Append(separator).Append(Regex.Escape(names[n])).Append("(?:=").Append(capture).Append(")?)?");
                    }
                    else if (op == '\0' || op == '+')
                    {
                        pattern.Append(n == 0 ? capture : "(?:" + separator + capture + ")?");
                    }
                    else
                    {
                        pattern.Append("(?:").Append(separator).Append(capture).Append(")?");
                    }
                }
            }

            pattern.Append('$');
            _Pattern = new Regex(pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Returns true when <paramref name="uri"/> matches, with the extracted variables.
        /// </summary>
        internal bool TryMatch(string uri, out Dictionary<string, string> variables)
        {
            variables = new Dictionary<string, string>(StringComparer.Ordinal);
            Match match;
            try
            {
                match = _Pattern.Match(uri);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }

            if (!match.Success) return false;

            foreach (KeyValuePair<string, string> group in _Groups)
            {
                Group captured = match.Groups[group.Key];
                if (captured.Success && captured.Length > 0) variables[group.Value] = Uri.UnescapeDataString(captured.Value);
            }

            for (int q = 0; q < _QueryExpressions.Count; q++)
            {
                Group query = match.Groups[_QueryGroups[q]];
                if (!query.Success || query.Length == 0) continue;

                foreach (string pair in query.Value.TrimStart('?', '&').Split('&'))
                {
                    if (pair.Length == 0) continue;
                    int equals = pair.IndexOf('=');
                    string name = Uri.UnescapeDataString(equals < 0 ? pair : pair.Substring(0, equals));
                    string value = equals < 0 ? String.Empty : Uri.UnescapeDataString(pair.Substring(equals + 1));
                    if (_QueryExpressions[q].Contains(name)) variables[name] = value;
                }
            }

            return true;
        }
    }
}
