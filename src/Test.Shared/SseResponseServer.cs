namespace Test.Shared
{
    using System;
    using System.IO;
    using System.Net;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A minimal Streamable HTTP MCP server that answers every JSON-RPC request with an SSE stream
    /// (<c>text/event-stream</c>), as some servers (for example the official Python SDK) do. Each stream carries a
    /// progress notification, an unrelated response, and then the real response, to exercise client parsing.
    /// </summary>
    internal sealed class SseResponseServer : IDisposable
    {
        private readonly HttpListener _Listener = new HttpListener();
        private readonly CancellationTokenSource _TokenSource = new CancellationTokenSource();

        public SseResponseServer()
        {
            Port = TestPorts.GetFreePort();
            _Listener.Prefixes.Add($"http://localhost:{Port}/");
            _Listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public int Port { get; }

        public string BaseUrl => $"http://localhost:{Port}";

        public void Dispose()
        {
            _TokenSource.Cancel();
            try
            {
                _Listener.Stop();
                _Listener.Close();
            }
            catch
            {
            }

            _TokenSource.Dispose();
        }

        private async Task LoopAsync()
        {
            while (!_TokenSource.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _Listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                string body;
                using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                using JsonDocument document = JsonDocument.Parse(body);
                JsonElement root = document.RootElement;
                string method = root.GetProperty("method").GetString() ?? "";
                context.Response.AddHeader("Mcp-Session-Id", "sse-session");

                if (!root.TryGetProperty("id", out JsonElement id))
                {
                    context.Response.StatusCode = 202;
                    context.Response.Close();
                    continue;
                }

                string result = method == "initialize"
                    ? "{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"sse\",\"version\":\"1\"}}"
                    : "{\"tools\":[{\"name\":\"streamed\",\"inputSchema\":{\"type\":\"object\"}}]}";

                StringBuilder stream = new StringBuilder();
                stream.Append("event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"progressToken\":1,\"progress\":0.5}}\n\n");
                stream.Append("event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":\"someone-else\",\"result\":{}}\n\n");
                stream.Append($"event: message\ndata: {{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"result\":{result}}}\n\n");

                byte[] bytes = Encoding.UTF8.GetBytes(stream.ToString());
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                context.Response.Close();
            }
        }
    }
}
