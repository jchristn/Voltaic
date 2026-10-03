namespace Voltaic.Mcp
{
    using System;
    using System.Diagnostics;
    using Voltaic.Core;

    /// <summary>
    /// Telemetry for one <c>tools/call</c>: the registered tool name (once known), the outcome the call reached, and
    /// stage operations (<c>validate_input</c>, <c>execute</c>, <c>validate_output</c>). Records
    /// <see cref="VoltaicTelemetryNames.McpToolDuration"/> when the call ends and names the enclosing <c>tools/call</c>
    /// server span after the tool. Not thread-safe: one instance belongs to one call.
    /// </summary>
    internal sealed class McpToolCallTelemetry
    {
        internal const string OutcomeToolError = "tool_error";
        internal const string OutcomeInvalidArguments = "invalid_arguments";
        internal const string OutcomeRateLimited = "rate_limited";
        internal const string OutcomeHandlerException = "handler_exception";
        internal const string OutcomeInputRequired = "input_required";
        internal const string OutcomeInvalidOutput = "invalid_output";
        internal const string OutcomeProtocolError = "protocol_error";

        private readonly long _Start = Stopwatch.GetTimestamp();
        private string _ToolLabel = VoltaicTelemetryNames.OtherValue;
        private string? _Outcome;
        private string? _ErrorType;

        private McpToolCallTelemetry()
        {
        }

        /// <summary>
        /// Starts tool call telemetry, or returns null when telemetry is off or nothing listens.
        /// </summary>
        internal static McpToolCallTelemetry? Begin()
        {
            if (!VoltaicTelemetry.Enabled) return null;
            if (!VoltaicInstruments.McpToolDuration.Enabled && !VoltaicInstruments.McpToolStageDuration.Enabled && !VoltaicInstruments.Source.HasListeners()) return null;
            return new McpToolCallTelemetry();
        }

        /// <summary>
        /// Gets the outcome set so far, or null while the call is on its success path.
        /// </summary>
        internal string? Outcome => _Outcome;

        /// <summary>
        /// Records the registered tool the call resolved to and names the enclosing server span after it.
        /// </summary>
        internal void SetTool(string toolName)
        {
            _ToolLabel = toolName.Length <= 128 ? toolName : VoltaicTelemetryNames.OtherValue;
            Activity? current = Activity.Current;
            if (current == null || current.Source != VoltaicInstruments.Source || current.OperationName != "tools/call") return;
            try
            {
                current.DisplayName = "tools/call " + _ToolLabel;
                if (current.IsAllDataRequested) current.SetTag(VoltaicTelemetryNames.AttrToolName, _ToolLabel);
            }
            catch (Exception)
            {
                // Best-effort.
            }
        }

        /// <summary>
        /// Sets the outcome; an error type marks the call failed.
        /// </summary>
        internal void SetOutcome(string outcome, string? errorType = null)
        {
            _Outcome = outcome;
            if (errorType != null) _ErrorType = errorType;
        }

        /// <summary>
        /// Starts a stage operation (span <c>stage:{stage}</c> and the stage duration histogram), or null.
        /// </summary>
        internal VoltaicOperation? StartStage(string stage)
        {
            TagList tags = new TagList
            {
                { VoltaicTelemetryNames.AttrToolName, _ToolLabel },
                { VoltaicTelemetryNames.AttrStage, stage }
            };

            return VoltaicOperation.Start(VoltaicInstruments.McpToolStageDuration, null, VoltaicTelemetryNames.SpanStagePrefix + stage, ActivityKind.Internal, tags);
        }

        /// <summary>
        /// Records the call's duration with its tool and outcome.
        /// </summary>
        internal void Finish()
        {
            if (!VoltaicInstruments.IsOn(VoltaicInstruments.McpToolDuration)) return;
            TagList tags = new TagList
            {
                { VoltaicTelemetryNames.AttrToolName, _ToolLabel },
                { VoltaicTelemetryNames.AttrOutcome, _Outcome ?? VoltaicTelemetryNames.OutcomeSuccess }
            };
            if (_ErrorType != null) tags.Add(VoltaicTelemetryNames.AttrErrorType, _ErrorType);
            VoltaicInstruments.Record(VoltaicInstruments.McpToolDuration, VoltaicInstruments.SecondsSince(_Start), tags);
        }
    }
}
