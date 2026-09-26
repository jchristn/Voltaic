namespace Voltaic.Mcp
{
    using System;
    using System.Diagnostics;
    using System.Runtime.InteropServices;

    /// <summary>
    /// Sends SIGTERM to a child process on Linux and macOS, the step the MCP stdio shutdown sequence asks for between
    /// closing the server's input and killing it. Windows has no SIGTERM. Thread-safe.
    /// </summary>
    internal static class McpProcessSignals
    {
        private const int SigTerm = 15;

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int Kill(int pid, int signal);

        /// <summary>
        /// Sends SIGTERM to <paramref name="process"/>. Returns false on Windows, or when the signal could not be sent.
        /// </summary>
        internal static bool TryTerminate(Process process)
        {
            if (process == null || RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
            try
            {
                return Kill(process.Id, SigTerm) == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is InvalidOperationException)
            {
                return false;
            }
        }
    }
}
