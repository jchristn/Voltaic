#pragma warning disable CS1591
namespace Voltaic.A2A
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// JSON configuration used by Voltaic's A2A implementation.
    /// </summary>
    public static class A2AJson
    {
        private static readonly Lazy<JsonSerializerOptions> _DefaultOptions = new Lazy<JsonSerializerOptions>(CreateDefaultOptions);

        /// <summary>
        /// Gets A2A JSON options using web naming, null omission, and A2A enum names.
        /// </summary>
        public static JsonSerializerOptions DefaultOptions => _DefaultOptions.Value;

        private static JsonSerializerOptions CreateDefaultOptions()
        {
            JsonSerializerOptions options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
                PropertyNameCaseInsensitive = true
            };
            options.Converters.Add(new RoleJsonConverter());
            options.Converters.Add(new TaskStateJsonConverter());
            return options;
        }
    }

    internal sealed class RoleJsonConverter : JsonConverter<Role>
    {
        public override Role Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            return value switch
            {
                "ROLE_USER" => Role.User,
                "user" => Role.User,
                "ROLE_AGENT" => Role.Agent,
                "agent" => Role.Agent,
                "ROLE_UNSPECIFIED" => Role.Unspecified,
                _ => Role.Unspecified
            };
        }

        public override void Write(Utf8JsonWriter writer, Role value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value switch
            {
                Role.User => "ROLE_USER",
                Role.Agent => "ROLE_AGENT",
                _ => "ROLE_UNSPECIFIED"
            });
        }
    }

    internal sealed class TaskStateJsonConverter : JsonConverter<TaskState>
    {
        public override TaskState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            return value switch
            {
                "TASK_STATE_SUBMITTED" => TaskState.Submitted,
                "submitted" => TaskState.Submitted,
                "TASK_STATE_WORKING" => TaskState.Working,
                "working" => TaskState.Working,
                "TASK_STATE_COMPLETED" => TaskState.Completed,
                "completed" => TaskState.Completed,
                "TASK_STATE_FAILED" => TaskState.Failed,
                "failed" => TaskState.Failed,
                "TASK_STATE_CANCELED" => TaskState.Canceled,
                "canceled" => TaskState.Canceled,
                "TASK_STATE_INPUT_REQUIRED" => TaskState.InputRequired,
                "input-required" => TaskState.InputRequired,
                "TASK_STATE_REJECTED" => TaskState.Rejected,
                "rejected" => TaskState.Rejected,
                "TASK_STATE_AUTH_REQUIRED" => TaskState.AuthRequired,
                "auth-required" => TaskState.AuthRequired,
                "TASK_STATE_UNSPECIFIED" => TaskState.Unspecified,
                _ => TaskState.Unspecified
            };
        }

        public override void Write(Utf8JsonWriter writer, TaskState value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value switch
            {
                TaskState.Submitted => "TASK_STATE_SUBMITTED",
                TaskState.Working => "TASK_STATE_WORKING",
                TaskState.Completed => "TASK_STATE_COMPLETED",
                TaskState.Failed => "TASK_STATE_FAILED",
                TaskState.Canceled => "TASK_STATE_CANCELED",
                TaskState.InputRequired => "TASK_STATE_INPUT_REQUIRED",
                TaskState.Rejected => "TASK_STATE_REJECTED",
                TaskState.AuthRequired => "TASK_STATE_AUTH_REQUIRED",
                _ => "TASK_STATE_UNSPECIFIED"
            });
        }
    }
}
