using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.Chat;

/// <summary>
/// Telling people something from a flow (#69): a message to Slack, a message to Discord. The cockpit's own Notify step
/// tells <em>you</em>, on your own screen; this tells everyone else, which is a different act and belongs in a
/// different step.
/// <para>
/// The channel is named, not spelled out: the webhook lives in the plugin's settings, so a flow that says "tell
/// releases" can be exported, shared and backed up without carrying a credential with it.
/// </para>
/// </summary>
internal static class ChatWorkflowSteps
{
    public static IEnumerable<IWorkflowStep> All(ChatSettings settings) =>
    [
        new SendStep(settings, ChatService.Slack),
        new SendStep(settings, ChatService.Discord),
    ];

    private sealed class SendStep(ChatSettings settings, ChatService service) : IWorkflowStep
    {
        public string TypeId => service == ChatService.Slack ? "chat.slack" : "chat.discord";

        public string Name => service == ChatService.Slack ? "Send to Slack" : "Send to Discord";

        public string Description => service == ChatService.Slack
            ? "Post a message to a Slack channel you configured. Name the channel; the webhook stays in the plugin's settings, where a backup knows to strip it."
            : "Post a message to a Discord channel you configured. Name the channel; the webhook stays in the plugin's settings, where a backup knows to strip it.";

        public string Icon => service == ChatService.Slack ? "💬" : "🎮";

        public string Category => "Chat";

        public IReadOnlyList<string> Parameters => ["Channel", "Message"];

        public IReadOnlyDictionary<string, string> Produces => new Dictionary<string, string>
        {
            ["channel"] = "releases",
        };

        public async Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken)
        {
            var channel = settings.Channel(context.Parameter("Channel"), service);
            var message = context.Parameter("Message");

            await new ChatClient().SendAsync(channel, message, cancellationToken);

            return new WorkflowStepResult(
                [new Dictionary<string, string> { ["channel"] = channel.Name }],
                $"Sent to {service} · {channel.Name}: {message.Trim()}");
        }
    }
}
