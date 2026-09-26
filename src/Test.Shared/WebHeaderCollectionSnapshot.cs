namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Specialized;

    /// <summary>
    /// A copy of the headers of one HTTP request, compared case-insensitively.
    /// </summary>
    internal sealed class WebHeaderCollectionSnapshot
    {
        private readonly Dictionary<string, string> _Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public WebHeaderCollectionSnapshot(NameValueCollection headers)
        {
            foreach (string? name in headers.AllKeys)
            {
                if (name != null) _Values[name] = headers[name] ?? String.Empty;
            }
        }

        /// <summary>
        /// Returns the header value, or null when the header was absent.
        /// </summary>
        public string? Get(string name)
        {
            return _Values.TryGetValue(name, out string? value) ? value : null;
        }
    }
}
