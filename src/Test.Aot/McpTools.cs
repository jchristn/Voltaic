namespace Test.Aot
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Registers the same tools, resource, and prompt on every MCP server. The input schemas use each AOT-safe
    /// representation: a <see cref="JsonObject"/>, a <see cref="JsonElement"/>, and a dictionary.
    /// </summary>
    internal static class McpTools
    {
        /// <summary>
        /// Registers the tools, resource, and prompt through the server's own methods.
        /// </summary>
        internal static void Register(
            Action<string, string, object, Func<RpcParameters?, object>> registerTool,
            Action<string, string, string, Func<McpReadResourceResult>> registerResource,
            Action<string, string, IEnumerable<McpPromptArgument>?, Func<RpcParameters?, McpGetPromptResult>> registerPrompt)
        {
            JsonObject addSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["a"] = new JsonObject { ["type"] = "number" },
                    ["b"] = new JsonObject { ["type"] = "number" }
                },
                ["required"] = new JsonArray("a", "b")
            };

            registerTool("add", "Adds two numbers", addSchema, args =>
            {
                AddArguments arguments = args?.Deserialize<AddArguments>() ?? new AddArguments();
                return McpToolCallResult.FromStructured(new SumResult { Total = arguments.A + arguments.B });
            });

            using (JsonDocument greetSchema = JsonDocument.Parse("""{"type":"object","properties":{"name":{"type":"string","pattern":"^\\p{L}+$"}},"required":["name"],"additionalProperties":false}"""))
            {
                registerTool("greet", "Greets a name made of letters", greetSchema.RootElement.Clone(), args =>
                    new Greeting { Message = "Hello, " + (args?.GetString("name") ?? string.Empty) });
            }

            Dictionary<string, object?> statusSchema = new Dictionary<string, object?>
            {
                { "type", "object" },
                { "properties", new Dictionary<string, object?>() }
            };

            registerTool("status", "Returns a status line", statusSchema, _ => McpToolCallResult.FromText("ok"));

            registerTool("unregistered", "Returns a type no resolver knows", statusSchema, _ => new UnregisteredResult { Value = 1 });

            registerResource("aot://status", "status", "text/plain", () => new McpReadResourceResult
            {
                Contents = new List<object> { new McpTextResourceContents { Uri = "aot://status", MimeType = "text/plain", Text = "running" } }
            });

            registerPrompt("summarize", "Summarizes a topic", new[] { new McpPromptArgument { Name = "topic", Required = true } }, args =>
                new McpGetPromptResult
                {
                    Messages = new List<McpPromptMessage>
                    {
                        new McpPromptMessage { Role = "user", Content = new McpTextContent { Text = "Summarize " + (args?.GetString("topic") ?? string.Empty) } }
                    }
                });
        }
    }
}
