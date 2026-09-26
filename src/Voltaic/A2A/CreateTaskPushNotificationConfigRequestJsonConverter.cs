namespace Voltaic.A2A
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Serializes <see cref="CreateTaskPushNotificationConfigRequest"/> as the flat A2A v1.0
    /// <c>TaskPushNotificationConfig</c> the <c>CreateTaskPushNotificationConfig</c> method takes, and reads both that
    /// shape and the <c>{ taskId, configId, config }</c> shape Voltaic 2.1.2 and earlier wrote.
    /// </summary>
    internal sealed class CreateTaskPushNotificationConfigRequestJsonConverter : JsonConverter<CreateTaskPushNotificationConfigRequest>
    {
        public override CreateTaskPushNotificationConfigRequest? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;

            using (JsonDocument document = JsonDocument.ParseValue(ref reader))
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new JsonException("A push notification configuration request must be a JSON object.");

                PushNotificationConfig target = PushConfigJson.ReadTarget(root, "config", options);
                string? configId = PushConfigJson.String(root, "configId") ?? PushConfigJson.String(root, "id") ?? target.Id;
                if (String.IsNullOrEmpty(target.Id) && !String.IsNullOrEmpty(configId)) target.Id = configId;

                return new CreateTaskPushNotificationConfigRequest
                {
                    Tenant = PushConfigJson.String(root, "tenant"),
                    TaskId = PushConfigJson.String(root, "taskId") ?? String.Empty,
                    ConfigId = configId ?? String.Empty,
                    Config = target
                };
            }
        }

        public override void Write(Utf8JsonWriter writer, CreateTaskPushNotificationConfigRequest value, JsonSerializerOptions options)
        {
            PushNotificationConfig target = value.Config ?? new PushNotificationConfig();
            string id = !String.IsNullOrEmpty(value.ConfigId) ? value.ConfigId : target.Id ?? String.Empty;
            PushConfigJson.WriteFlat(writer, value.Tenant, id, value.TaskId, target, options);
        }
    }
}
