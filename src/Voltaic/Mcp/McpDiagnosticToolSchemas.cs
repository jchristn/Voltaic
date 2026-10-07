namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Input schemas of the opt-in diagnostic tools <c>echo</c> and <c>getTime</c>, as source-generated dictionaries
    /// (anonymous types cannot be serialized under Native AOT). Each call returns a new instance.
    /// </summary>
    internal static class McpDiagnosticToolSchemas
    {
        /// <summary>
        /// The <c>echo</c> input schema: one required string property, <c>message</c>.
        /// </summary>
        internal static Dictionary<string, object?> Echo()
        {
            return new Dictionary<string, object?>
            {
                { "type", "object" },
                {
                    "properties", new Dictionary<string, object?>
                    {
                        {
                            "message", new Dictionary<string, object?>
                            {
                                { "type", "string" },
                                { "description", "The message to echo back" }
                            }
                        }
                    }
                },
                { "required", new[] { "message" } }
            };
        }

        /// <summary>
        /// The <c>getTime</c> input schema: an object with no properties.
        /// </summary>
        internal static Dictionary<string, object?> GetTime()
        {
            return new Dictionary<string, object?>
            {
                { "type", "object" },
                { "properties", new Dictionary<string, object?>() },
                { "required", Array.Empty<string>() }
            };
        }
    }
}
