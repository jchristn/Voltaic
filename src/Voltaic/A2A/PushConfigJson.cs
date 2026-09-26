namespace Voltaic.A2A
{
    using System;
    using System.Text.Json;

    /// <summary>
    /// Shared reading and writing of the flat A2A v1.0 push notification configuration fields.
    /// </summary>
    internal static class PushConfigJson
    {
        /// <summary>
        /// Reads the webhook target: the flat <c>url</c>, <c>token</c>, and <c>authentication</c> fields, or, when absent,
        /// the object under <paramref name="legacyProperty"/> (the nested shape older Voltaic versions wrote).
        /// </summary>
        internal static PushNotificationConfig ReadTarget(JsonElement root, string legacyProperty, JsonSerializerOptions options)
        {
            if (root.TryGetProperty(legacyProperty, out JsonElement nested) && nested.ValueKind == JsonValueKind.Object && !root.TryGetProperty("url", out _))
            {
                return nested.Deserialize<PushNotificationConfig>(options) ?? new PushNotificationConfig();
            }

            PushNotificationConfig target = new PushNotificationConfig
            {
                Url = String(root, "url") ?? System.String.Empty,
                Token = String(root, "token")
            };

            if (root.TryGetProperty("authentication", out JsonElement authentication) && authentication.ValueKind == JsonValueKind.Object)
            {
                target.Authentication = authentication.Deserialize<AuthenticationInfo>(options);
            }

            return target;
        }

        /// <summary>
        /// Writes the flat shape; empty optional fields are omitted.
        /// </summary>
        internal static void WriteFlat(Utf8JsonWriter writer, string? tenant, string id, string taskId, PushNotificationConfig target, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            if (!System.String.IsNullOrEmpty(tenant)) writer.WriteString("tenant", tenant);
            writer.WriteString("id", id ?? System.String.Empty);
            writer.WriteString("taskId", taskId ?? System.String.Empty);
            writer.WriteString("url", target.Url ?? System.String.Empty);
            if (!System.String.IsNullOrEmpty(target.Token)) writer.WriteString("token", target.Token);
            if (target.Authentication != null)
            {
                writer.WritePropertyName("authentication");
                JsonSerializer.Serialize(writer, target.Authentication, options);
            }

            writer.WriteEndObject();
        }

        /// <summary>
        /// Returns a string property, or null when it is absent or not a string.
        /// </summary>
        internal static string? String(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
    }
}
