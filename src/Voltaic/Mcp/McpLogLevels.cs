namespace Voltaic.Mcp
{
    using System;

    /// <summary>
    /// The MCP logging levels (RFC 5424 severities, least to most severe).
    /// </summary>
    internal static class McpLogLevels
    {
        private static readonly string[] _Levels = { "debug", "info", "notice", "warning", "error", "critical", "alert", "emergency" };

        /// <summary>
        /// Returns true when <paramref name="level"/> is one of the eight MCP levels.
        /// </summary>
        internal static bool IsValid(string? level)
        {
            return level != null && Array.IndexOf(_Levels, level) >= 0;
        }

        /// <summary>
        /// Returns true when a message at <paramref name="level"/> passes a minimum of <paramref name="minimum"/>; a
        /// null minimum passes everything.
        /// </summary>
        internal static bool Passes(string level, string? minimum)
        {
            if (minimum == null) return true;
            return Array.IndexOf(_Levels, level) >= Array.IndexOf(_Levels, minimum);
        }
    }
}
