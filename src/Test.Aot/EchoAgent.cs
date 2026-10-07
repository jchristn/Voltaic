namespace Test.Aot
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.A2A;

    /// <summary>
    /// An A2A agent that completes each task with an artifact and a reply echoing the first text part.
    /// </summary>
    internal sealed class EchoAgent : IA2AAgentHandler
    {
        /// <inheritdoc />
        public async Task ExecuteAsync(A2ARequestContext context, A2AAgentEventQueue eventQueue, CancellationToken token)
        {
            A2ATaskUpdater updater = new A2ATaskUpdater(eventQueue, context.TaskId, context.ContextId);
            await updater.SubmitAsync(token: token).ConfigureAwait(false);
            await updater.StartAsync(token: token).ConfigureAwait(false);

            string text = context.Message.Parts.Count > 0 ? context.Message.Parts[0].Text ?? string.Empty : string.Empty;
            await updater.AddArtifactAsync(new Artifact
            {
                ArtifactId = "echo",
                Name = "Echo",
                Parts = new List<Part> { Part.FromText(text) }
            }, token: token).ConfigureAwait(false);

            Message reply = new Message
            {
                Role = Role.Agent,
                MessageId = Guid.NewGuid().ToString("N"),
                TaskId = context.TaskId,
                ContextId = context.ContextId,
                Parts = new List<Part> { Part.FromText("echo: " + text) }
            };

            await updater.CompleteAsync(reply, token).ConfigureAwait(false);
        }
    }
}
