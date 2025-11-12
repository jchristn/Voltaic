namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Represents a tool definition for MCP protocol tool discovery.
    /// </summary>
    public class ToolDefinition
    {
        /// <summary>
        /// Gets or sets the name of the tool.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the description of what the tool does.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the JSON schema for the tool's input parameters.
        /// </summary>
        public object InputSchema { get; set; } = new { };

        /// <summary>
        /// Represents a tool definition for MCP protocol tool discovery.
        /// </summary>
        public ToolDefinition()
        {

        }
    }
}
