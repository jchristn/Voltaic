namespace Test.Shared
{
    using System.Collections.Generic;

    /// <summary>
    /// One request recorded by <see cref="WebhookReceiver"/>, with the status code it was answered with.
    /// </summary>
    internal sealed record ReceivedWebhook(string Method, string Path, Dictionary<string, string> Headers, string Body, int AnsweredStatus);
}
