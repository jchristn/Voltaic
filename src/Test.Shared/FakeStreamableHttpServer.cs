namespace Test.Shared
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Net;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A scriptable Streamable HTTP MCP server for client tests. It answers <c>initialize</c> with a session, accepts
    /// notifications and client responses with 202 (recording the responses), and hands GET streams and other requests
    /// to test-supplied handlers. Request headers, initialize bodies, and <c>Last-Event-ID</c> values are recorded.
    /// </summary>
    internal sealed class FakeStreamableHttpServer : IDisposable
    {
        private readonly HttpListener _Listener = new HttpListener();
        private readonly CancellationTokenSource _TokenSource = new CancellationTokenSource();
        private int _GetCount;

        public FakeStreamableHttpServer()
        {
            Port = TestPorts.GetFreePort();
            _Listener.Prefixes.Add($"http://localhost:{Port}/");
            _Listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public int Port { get; }

        public string BaseUrl => $"http://localhost:{Port}";

        public string SessionId { get; } = "fake-session";

        /// <summary>
        /// Gets or sets whether responses carry the session header. Default is true.
        /// </summary>
        public bool IssueSession { get; set; } = true;

        /// <summary>
        /// Gets the bodies of every POSTed request other than initialize.
        /// </summary>
        public ConcurrentQueue<string> RequestBodies { get; } = new ConcurrentQueue<string>();

        /// <summary>
        /// Gets the JSON-RPC responses the client POSTed (answers to server requests).
        /// </summary>
        public ConcurrentQueue<string> ClientResponses { get; } = new ConcurrentQueue<string>();

        /// <summary>
        /// Gets the bodies of the initialize requests.
        /// </summary>
        public ConcurrentQueue<string> InitializeBodies { get; } = new ConcurrentQueue<string>();

        /// <summary>
        /// Gets the Last-Event-ID header of each GET, or an empty string when it was absent.
        /// </summary>
        public ConcurrentQueue<string> GetLastEventIds { get; } = new ConcurrentQueue<string>();

        /// <summary>
        /// Gets the headers of every POST that carried a request, keyed by nothing: one collection per request.
        /// </summary>
        public ConcurrentQueue<WebHeaderCollectionSnapshot> RequestHeaders { get; } = new ConcurrentQueue<WebHeaderCollectionSnapshot>();

        public int GetCount => Volatile.Read(ref _GetCount);

        /// <summary>
        /// Handles GET streams. Receives the zero-based GET index. Null answers 405.
        /// </summary>
        public Func<int, HttpListenerResponse, CancellationToken, Task>? GetHandler { get; set; }

        /// <summary>
        /// Handles requests other than initialize. Receives the method, the raw id JSON, and the body. Null answers
        /// every request with an empty result.
        /// </summary>
        public Func<string, string, string, HttpListenerResponse, CancellationToken, Task>? RequestHandler { get; set; }

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

        /// <summary>
        /// Writes SSE text and flushes it.
        /// </summary>
        public static async Task WriteSseAsync(HttpListenerResponse response, string text, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(token).ConfigureAwait(false);
        }

        /// <summary>
        /// Starts an SSE response.
        /// </summary>
        public static void BeginSse(HttpListenerResponse response)
        {
            response.StatusCode = 200;
            response.ContentType = "text/event-stream";
            response.SendChunked = true;
        }

        /// <summary>
        /// Writes a JSON response and closes it.
        /// </summary>
        public static async Task WriteJsonAsync(HttpListenerResponse response, string json, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            response.StatusCode = 200;
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
            response.Close();
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

                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            CancellationToken token = _TokenSource.Token;
            try
            {
                if (IssueSession) context.Response.AddHeader("Mcp-Session-Id", SessionId);

                if (context.Request.HttpMethod == "GET")
                {
                    int index = Interlocked.Increment(ref _GetCount) - 1;
                    GetLastEventIds.Enqueue(context.Request.Headers["Last-Event-ID"] ?? String.Empty);
                    if (GetHandler == null)
                    {
                        context.Response.StatusCode = 405;
                        context.Response.Close();
                        return;
                    }

                    await GetHandler(index, context.Response, token).ConfigureAwait(false);
                    context.Response.Close();
                    return;
                }

                if (context.Request.HttpMethod == "DELETE")
                {
                    context.Response.StatusCode = 200;
                    context.Response.Close();
                    return;
                }

                string body;
                using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                using JsonDocument document = JsonDocument.Parse(body);
                JsonElement root = document.RootElement;
                bool hasMethod = root.TryGetProperty("method", out JsonElement methodElement);
                bool hasId = root.TryGetProperty("id", out JsonElement id) && id.ValueKind != JsonValueKind.Null;

                if (!hasMethod)
                {
                    ClientResponses.Enqueue(body);
                    context.Response.StatusCode = 202;
                    context.Response.Close();
                    return;
                }

                if (!hasId)
                {
                    context.Response.StatusCode = 202;
                    context.Response.Close();
                    return;
                }

                RequestHeaders.Enqueue(new WebHeaderCollectionSnapshot(context.Request.Headers));
                string method = methodElement.GetString() ?? String.Empty;
                if (method == "initialize")
                {
                    InitializeBodies.Enqueue(body);
                    await WriteJsonAsync(context.Response, "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1\"}}}", token).ConfigureAwait(false);
                    return;
                }

                RequestBodies.Enqueue(body);
                if (RequestHandler != null)
                {
                    await RequestHandler(method, id.GetRawText(), body, context.Response, token).ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(context.Response, "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":{}}", token).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    context.Response.Abort();
                }
                catch
                {
                }
            }
        }
    }
}
