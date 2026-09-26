namespace Voltaic.A2A
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Serializes <see cref="TaskPushNotificationConfig"/> in the flat A2A v1.0 shape
    /// (<c>tenant</c>, <c>id</c>, <c>taskId</c>, <c>url</c>, <c>token</c>, <c>authentication</c>), and reads both that shape
    /// and the nested <c>pushNotificationConfig</c> shape Voltaic 2.1.2 and earlier wrote.
    /// </summary>
    internal sealed class TaskPushNotificationConfigJsonConverter : JsonConverter<TaskPushNotificationConfig>
    {
        public override TaskPushNotificationConfig? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;

            using (JsonDocument document = JsonDocument.ParseValue(ref reader))
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new JsonException("A task push notification configuration must be a JSON object.");

                PushNotificationConfig target = PushConfigJson.ReadTarget(root, "pushNotificationConfig", options);
                TaskPushNotificationConfig config = new TaskPushNotificationConfig
                {
                    Tenant = PushConfigJson.String(root, "tenant"),
                    Id = PushConfigJson.String(root, "id") ?? target.Id ?? String.Empty,
                    TaskId = PushConfigJson.String(root, "taskId") ?? String.Empty,
                    PushNotificationConfig = target
                };
                if (String.IsNullOrEmpty(target.Id) && !String.IsNullOrEmpty(config.Id)) target.Id = config.Id;
                return config;
            }
        }

        public override void Write(Utf8JsonWriter writer, TaskPushNotificationConfig value, JsonSerializerOptions options)
        {
            PushNotificationConfig target = value.PushNotificationConfig ?? new PushNotificationConfig();
            string id = !String.IsNullOrEmpty(value.Id) ? value.Id : target.Id ?? String.Empty;
            PushConfigJson.WriteFlat(writer, value.Tenant, id, value.TaskId, target, options);
        }
    }
}
