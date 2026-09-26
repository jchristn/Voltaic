namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Core;

    /// <summary>
    /// Provides a TCP-based MCP (Model Context Protocol) client implementation for making remote procedure calls over a network.
    /// This class extends JsonRpcClient with MCP semantics:
    /// <list type="bullet">
    /// <item><description>Messages are newline-delimited JSON, the stdio framing MCP asks custom stream transports to reuse (set <see cref="NewlineDelimited"/> to false for Content-Length framing, which Voltaic TCP servers before 2.1.5 require).</description></item>
    /// <item><description>Connecting performs the <c>initialize</c> handshake unless <see cref="AutoInitialize"/> is false.</description></item>
    /// <item><description>A <c>ping</c> from the server is answered with <c>{}</c>, other server requests by handlers registered with <see cref="JsonRpcClient.RegisterRequestHandler"/> or with <c>-32601</c>.</description></item>
    /// <item><description>A call that times out or is cancelled sends <c>notifications/cancelled</c>.</description></item>
    /// </list>
    /// </summary>
    public class McpTcpClient : JsonRpcClient
    {
        private string _ProtocolVersion = McpProtocol.LatestProtocolVersion;
        private string _ClientName = "Voltaic.Mcp.TcpClient";
        private string _ClientVersion = "1.0.0";
        private JsonElement? _InitializeResult;

        /// <summary>
        /// Initializes a new instance of the <see cref="McpTcpClient"/> class.
        /// </summary>
        public McpTcpClient()
            : base()
        {
            AnswerPingRequests();
            NewlineFraming = true;
        }

        /// <summary>
        /// Gets or sets whether messages are newline-delimited JSON (true, the default) or use Content-Length framing
        /// (false, for Voltaic TCP servers before 2.1.5). Set before connecting.
        /// </summary>
        public bool NewlineDelimited
        {
            get => NewlineFraming;
            set => NewlineFraming = value;
        }

        /// <summary>
        /// Gets or sets the MCP protocol version requested in <c>initialize</c>; after the handshake it holds the version
        /// the server negotiated. Must be a handshake-era revision (<c>2024-11-05</c> through <c>2025-11-25</c>), since a
        /// client must request a version it can negotiate. Default is <see cref="McpProtocol.LatestProtocolVersion"/>.
        /// Setting null or whitespace restores the default.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when the value is not a handshake-era protocol version.</exception>
        public string ProtocolVersion
        {
            get => _ProtocolVersion;
            set
            {
                string version = String.IsNullOrWhiteSpace(value) ? McpProtocol.LatestProtocolVersion : value;
                if (!McpProtocol.IsHandshakeVersion(version))
                {
                    throw new ArgumentException($"'{version}' is not a handshake-era MCP protocol version that initialize can negotiate.", nameof(value));
                }

                _ProtocolVersion = version;
            }
        }

        /// <summary>
        /// Gets or sets the client name reported in <c>initialize</c>. Default is <c>Voltaic.Mcp.TcpClient</c>.
        /// </summary>
        public string ClientName
        {
            get => _ClientName;
            set => _ClientName = String.IsNullOrWhiteSpace(value) ? "Voltaic.Mcp.TcpClient" : value;
        }

        /// <summary>
        /// Gets or sets the client version reported in <c>initialize</c>. Default is <c>1.0.0</c>.
        /// </summary>
        public string ClientVersion
        {
            get => _ClientVersion;
            set => _ClientVersion = String.IsNullOrWhiteSpace(value) ? "1.0.0" : value;
        }

        /// <summary>
        /// Gets or sets whether connecting performs the MCP <c>initialize</c> handshake automatically, as the
        /// specification requires before any other request. Default is true.
        /// </summary>
        public bool AutoInitialize { get; set; } = true;

        /// <summary>
        /// Gets additional client capabilities to declare in <c>initialize</c>, merged with the capabilities implied by
        /// registered request handlers. Never null. Change it before connecting.
        /// </summary>
        public Dictionary<string, object?> ClientCapabilities { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

        /// <summary>
        /// Gets the server's <c>initialize</c> result, or null before the handshake completed.
        /// </summary>
        public JsonElement? InitializeResult => _InitializeResult;

        /// <summary>
        /// Performs the MCP <c>initialize</c> handshake and sends <c>notifications/initialized</c>. Connecting calls this
        /// automatically unless <see cref="AutoInitialize"/> is false.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that completes when the handshake is done.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the client is not connected, or the server rejects initialize or chooses a version this client cannot use.</exception>
        public async Task InitializeAsync(CancellationToken token = default)
        {
            McpInitializeOutcome outcome = await McpClientHandshake.RunAsync(
                async (parameters, ct) => McpClientHandshake.ToElement(await CallAsync<object?>("initialize", parameters, 30000, ct).ConfigureAwait(false)),
                ct => NotifyAsync("notifications/initialized", null, ct),
                _ProtocolVersion,
                _ClientName,
                _ClientVersion,
                McpClientHandshake.CapabilitiesFor(RequestDispatcher, ClientCapabilities),
                token).ConfigureAwait(false);

            _ProtocolVersion = outcome.ProtocolVersion;
            _InitializeResult = outcome.Result;
        }

        private protected override async Task<bool> OnConnectedAsync(CancellationToken token)
        {
            if (!AutoInitialize) return true;
            try
            {
                await InitializeAsync(token).ConfigureAwait(false);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private protected override void OnCallAbandoned(string method, int id, bool cancelledByCaller)
        {
            if (method == "initialize") return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await NotifyAsync("notifications/cancelled", new McpCancelledNotification
                    {
                        RequestId = id,
                        Reason = cancelledByCaller ? "The request was cancelled by the client." : "The request timed out."
                    }).ConfigureAwait(false);
                }
                catch
                {
                    // The connection may be closed.
                }
            });
        }
    }
}
