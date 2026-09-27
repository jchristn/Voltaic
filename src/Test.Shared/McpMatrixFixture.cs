namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// The tools, resource, and prompt every server in the revision matrix serves, so one scenario runs unchanged on
    /// every transport. Also compiled into Test.McpServer (its <c>--matrix</c> mode) for the stdio transport, so it
    /// depends only on Voltaic.
    /// </summary>
    public static class McpMatrixFixture
    {
        /// <summary>
        /// The URI of the fixture resource.
        /// </summary>
        public const string ResourceUri = "matrix://one";

        /// <summary>
        /// Registers the fixture through a server's registration methods.
        /// </summary>
        /// <param name="registerTool">The server's <c>RegisterTool(ToolDefinition, handler)</c>.</param>
        /// <param name="registerResource">The server's <c>RegisterResource(uri, name, mimeType, handler)</c>.</param>
        /// <param name="registerPrompt">The server's <c>RegisterPrompt(name, description, arguments, handler)</c>.</param>
        public static void Configure(
            Action<ToolDefinition, Func<RpcParameters?, CancellationToken, Task<object>>> registerTool,
            Action<string, string, string, Func<McpReadResourceResult>> registerResource,
            Action<string, string, IEnumerable<McpPromptArgument>?, Func<RpcParameters?, McpGetPromptResult>> registerPrompt)
        {
            ArgumentNullException.ThrowIfNull(registerTool);
            ArgumentNullException.ThrowIfNull(registerResource);
            ArgumentNullException.ThrowIfNull(registerPrompt);

            // A tool with every revision-dependent field: title (2025-06-18), annotations (2025-03-26), and an output
            // schema with structured content (2025-06-18).
            registerTool(new ToolDefinition
            {
                Name = "describe",
                Title = "Describe",
                Description = "Returns its number as structured content.",
                InputSchema = Schema("{\"type\":\"object\",\"properties\":{\"n\":{\"type\":\"integer\"}},\"required\":[\"n\"]}"),
                OutputSchema = Schema("{\"type\":\"object\",\"properties\":{\"n\":{\"type\":\"integer\"}},\"required\":[\"n\"]}"),
                Annotations = new McpAnnotations { ReadOnlyHint = true }
            }, (args, token) => Task.FromResult<object>(McpToolCallResult.FromStructured(new { n = args?.GetInt64("n") ?? 0 })));

            // Runs until cancelled.
            registerTool(new ToolDefinition
            {
                Name = "slow",
                Description = "Waits until cancelled.",
                InputSchema = Schema("{\"type\":\"object\"}")
            }, async (args, token) =>
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return McpToolCallResult.FromText("never");
            });

            // Sends an info and an error log message and one progress notification before answering.
            registerTool(new ToolDefinition
            {
                Name = "report",
                Description = "Logs and reports progress.",
                InputSchema = Schema("{\"type\":\"object\"}")
            }, async (args, token) =>
            {
                McpToolCallContext context = McpToolCallContext.Current!;
                await context.LogAsync("info", "matrix info", null, token).ConfigureAwait(false);
                await context.LogAsync("error", "matrix error", null, token).ConfigureAwait(false);
                await context.ReportProgressAsync(1, 2, "halfway", token).ConfigureAwait(false);
                return McpToolCallResult.FromText("reported");
            });

            registerResource(ResourceUri, "one", "text/plain", () => new McpReadResourceResult
            {
                Contents = new List<object> { new McpTextResourceContents { Uri = ResourceUri, MimeType = "text/plain", Text = "matrix resource" } }
            });

            registerPrompt("greet", "Greets a topic.", new[] { new McpPromptArgument { Name = "topic", Required = true } }, args => new McpGetPromptResult
            {
                Messages = new List<McpPromptMessage>
                {
                    new McpPromptMessage { Role = "user", Content = new McpTextContent { Text = "Hello " + (args?.GetString("topic") ?? String.Empty) } }
                }
            });
        }

        private static JsonElement Schema(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }
}
