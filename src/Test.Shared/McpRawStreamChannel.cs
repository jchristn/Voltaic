namespace Test.Shared
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A raw channel over a line-delimited stream: a TCP connection or the stdin and stdout of a stdio server process.
    /// </summary>
    internal sealed class McpRawStreamChannel : IMcpRawChannel
    {
        private readonly StreamWriter _Writer;
        private readonly BlockingCollection<string> _Received = new BlockingCollection<string>();
        private readonly SemaphoreSlim _SendLock = new SemaphoreSlim(1, 1);
        private readonly TcpClient? _Tcp;
        private readonly Process? _Process;

        private McpRawStreamChannel(string transport, StreamReader reader, StreamWriter writer, TcpClient? tcp, Process? process)
        {
            Transport = transport;
            _Writer = writer;
            _Tcp = tcp;
            _Process = process;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;
                        if (line.Length > 0) _Received.Add(line);
                    }
                }
                catch
                {
                }
                finally
                {
                    _Received.CompleteAdding();
                }
            });
        }

        public string Transport { get; }

        public string? ProtocolVersion { get; set; }

        public bool Stateless { get; set; }

        /// <summary>
        /// Connects to a TCP server that accepts newline-delimited JSON.
        /// </summary>
        public static async Task<McpRawStreamChannel> ConnectTcpAsync(int port, CancellationToken token)
        {
            TcpClient tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port, token).ConfigureAwait(false);
            NetworkStream stream = tcp.GetStream();
            StreamReader reader = new StreamReader(stream, new UTF8Encoding(false));
            StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            return new McpRawStreamChannel("tcp", reader, writer, tcp, null);
        }

        /// <summary>
        /// Starts Test.McpServer with the given arguments and talks to it over stdio.
        /// </summary>
        public static McpRawStreamChannel StartStdio(params string[] serverArguments)
        {
            return StartStdio(null, serverArguments);
        }

        /// <summary>
        /// Starts Test.McpServer with extra environment variables and the given arguments.
        /// </summary>
        public static McpRawStreamChannel StartStdio(IReadOnlyDictionary<string, string>? environment, params string[] serverArguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false)
            };

            string[] launch = McpStdioIntegrationSuites.TestServerArguments();
            foreach (string argument in launch) startInfo.ArgumentList.Add(argument);
            if (launch.Length > 0 && launch[0] == "run") startInfo.ArgumentList.Add("--");
            foreach (string argument in serverArguments) startInfo.ArgumentList.Add(argument);
            if (environment != null)
            {
                foreach (KeyValuePair<string, string> variable in environment) startInfo.Environment[variable.Key] = variable.Value;
            }

            Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Test.McpServer did not start.");
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) != null)
                    {
                    }
                }
                catch
                {
                }
            });

            process.StandardInput.AutoFlush = true;
            process.StandardInput.NewLine = "\n";
            return new McpRawStreamChannel("stdio", process.StandardOutput, process.StandardInput, null, process);
        }

        public async Task SendAsync(string json, CancellationToken token)
        {
            await _SendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await _Writer.WriteLineAsync(json).ConfigureAwait(false);
            }
            finally
            {
                _SendLock.Release();
            }
        }

        public string? Receive(TimeSpan timeout)
        {
            try
            {
                return _Received.TryTake(out string? line, timeout) ? line : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _Writer.Close();
            }
            catch
            {
            }

            _Tcp?.Dispose();
            if (_Process != null)
            {
                Task exited = _Process.WaitForExitAsync();
                if (await Task.WhenAny(exited, Task.Delay(5000)).ConfigureAwait(false) != exited && !_Process.HasExited)
                {
                    _Process.Kill(entireProcessTree: true);
                }

                _Process.Dispose();
            }

            _SendLock.Dispose();
        }
    }
}
