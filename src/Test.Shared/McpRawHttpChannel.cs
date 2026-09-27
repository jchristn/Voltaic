namespace Test.Shared
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Mcp;

    /// <summary>
    /// A raw channel over Streamable HTTP. Every message is POSTed without waiting; the messages in each response (a
    /// JSON body, or the events of an SSE stream) are queued as they arrive. The session ID from <c>initialize</c> is
    /// sent on later requests, and stateless requests get the 2026-07-28 headers.
    /// </summary>
    internal sealed class McpRawHttpChannel : IMcpRawChannel
    {
        private readonly HttpClient _Client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        private readonly string _Url;
        private readonly BlockingCollection<string> _Received = new BlockingCollection<string>();
        private readonly CancellationTokenSource _Stop = new CancellationTokenSource();
        private string? _SessionId;

        /// <summary>
        /// Creates a channel for the MCP endpoint at <paramref name="url"/>.
        /// </summary>
        public McpRawHttpChannel(string url)
        {
            _Url = url;
        }

        public string Transport => "http";

        public string? ProtocolVersion { get; set; }

        public bool Stateless { get; set; }

        public Task SendAsync(string json, CancellationToken token)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, _Url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (_SessionId != null && !Stateless) request.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, _SessionId);
            if (ProtocolVersion != null) request.Headers.TryAddWithoutValidation(McpProtocol.ProtocolVersionHeader, ProtocolVersion);
            if (Stateless) AddStatelessHeaders(request, json);

            // A session ID is issued by initialize; it is read before the response body is queued, so later sends carry it.
            bool isInitialize = json.Contains("\"initialize\"", StringComparison.Ordinal);
            Task sending = Task.Run(async () =>
            {
                try
                {
                    using HttpResponseMessage response = await _Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _Stop.Token).ConfigureAwait(false);
                    if (isInitialize && response.Headers.TryGetValues(McpProtocol.SessionIdHeader, out System.Collections.Generic.IEnumerable<string>? ids)) _SessionId = ids.FirstOrDefault();
                    string? mediaType = response.Content.Headers.ContentType?.MediaType;
                    using Stream body = await response.Content.ReadAsStreamAsync(_Stop.Token).ConfigureAwait(false);
                    using StreamReader reader = new StreamReader(body, new UTF8Encoding(false));
                    if (mediaType == "text/event-stream")
                    {
                        StringBuilder data = new StringBuilder();
                        while (true)
                        {
                            string? line = await reader.ReadLineAsync(_Stop.Token).ConfigureAwait(false);
                            if (line == null) break;
                            if (line.Length == 0)
                            {
                                if (data.Length > 0) _Received.Add(data.ToString());
                                data.Clear();
                                continue;
                            }

                            if (line.StartsWith("data:", StringComparison.Ordinal))
                            {
                                if (data.Length > 0) data.Append('\n');
                                data.Append(line.Substring(5).TrimStart(' '));
                            }
                        }

                        if (data.Length > 0) _Received.Add(data.ToString());
                    }
                    else
                    {
                        string text = await reader.ReadToEndAsync(_Stop.Token).ConfigureAwait(false);
                        if (text.Trim().Length > 0) _Received.Add(text);
                    }
                }
                catch
                {
                }
                finally
                {
                    request.Dispose();
                }
            });

            // An initialize is waited for, so the session ID is known before the next message goes out.
            return isInitialize ? sending : Task.CompletedTask;
        }

        public string? Receive(TimeSpan timeout)
        {
            return _Received.TryTake(out string? message, timeout) ? message : null;
        }

        public async ValueTask DisposeAsync()
        {
            if (_SessionId != null && !Stateless)
            {
                try
                {
                    using HttpRequestMessage delete = new HttpRequestMessage(HttpMethod.Delete, _Url);
                    delete.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, _SessionId);
                    if (ProtocolVersion != null) delete.Headers.TryAddWithoutValidation(McpProtocol.ProtocolVersionHeader, ProtocolVersion);
                    using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    using HttpResponseMessage _ = await _Client.SendAsync(delete, timeout.Token).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            _Stop.Cancel();
            _Client.Dispose();
            _Stop.Dispose();
        }

        // Mcp-Method always; Mcp-Name for the methods that name a tool, prompt, or resource.
        private static void AddStatelessHeaders(HttpRequestMessage request, string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("method", out JsonElement method) || method.ValueKind != JsonValueKind.String) return;
            string name = method.GetString()!;
            request.Headers.TryAddWithoutValidation(McpProtocol.MethodHeader, name);
            if (!root.TryGetProperty("params", out JsonElement parameters) || parameters.ValueKind != JsonValueKind.Object) return;
            string? target = name switch
            {
                "tools/call" or "prompts/get" => parameters.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                "resources/read" => parameters.TryGetProperty("uri", out JsonElement u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null,
                _ => null
            };
            if (target != null) request.Headers.TryAddWithoutValidation(McpProtocol.NameHeader, target);
        }
    }
}
