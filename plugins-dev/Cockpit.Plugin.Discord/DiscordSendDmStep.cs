using System.Globalization;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.Discord;

// The flow step that DMs the operator (AC-1360), AI-Hub's `user: raymond` task output. There is no recipient field:
// it only ever writes to the one account the access settings allow, so a flow cannot DM an arbitrary id.
internal sealed class DiscordSendDmStep(
    Func<AssistantChannelAccess?> access,
    Func<string, IReadOnlyList<string>, CancellationToken, Task> sendDirectMessage) : IWorkflowStep
{
    public const string MessageParameter = "Message";
    public const string MaxLengthParameter = "Max length";

    // Discord's own hard cap on a message body.
    public const int DiscordLimit = 2000;

    public string TypeId => "discord.send-dm";

    public string Name => "Send me a Discord DM";

    public string Description =>
        "Send a message to the one Discord account allowed to talk to your assistant. A long message arrives as several, cut at a line break — never truncated.";

    public string Icon => "✉";

    public string Category => "Discord";

    public IReadOnlyList<string> Parameters => [MessageParameter, MaxLengthParameter];

    // It only reaches the operator's own account, but it still sends data out of the machine.
    public WorkflowStepConsent? RequiredConsent => WorkflowStepConsent.LowRisk;

    public async Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken)
    {
        if (access() is not { Audience: AssistantChannelAudience.SingleUser, UserIds.Count: 1 } allowed)
        {
            throw new InvalidOperationException(
                "Nothing was sent: this step only writes to a single allowed account. In the Discord plugin's settings, choose \"Only this one Discord account\" and enter your user id.");
        }

        var message = context.Parameter(MessageParameter);
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new InvalidOperationException("This step has nothing to send. Open it and write the message, or {output} to send what the step before produced.");
        }

        var parts = Split(message, _MaxLength(context.Parameter(MaxLengthParameter)));
        await sendDirectMessage(allowed.UserIds.First(), parts, cancellationToken).ConfigureAwait(false);

        return WorkflowStepResult.Done(parts.Count == 1 ? "Sent as a Discord DM." : $"Sent as {parts.Count} Discord DMs.");
    }

    // Cut at the last line break that fits, else the last space, else hard (never inside a surrogate pair). Every
    // character is kept, except a part that is only whitespace: Discord refuses to send one.
    public static IReadOnlyList<string> Split(string text, int maxLength)
    {
        var parts = new List<string>();
        var rest = text;

        while (rest.Length > maxLength)
        {
            var window = rest[..maxLength];
            var cut = window.LastIndexOf('\n');
            if (cut <= 0)
            {
                cut = window.LastIndexOf(' ');
            }

            var length = cut > 0 ? cut + 1 : char.IsHighSurrogate(window[^1]) ? maxLength - 1 : maxLength;
            parts.Add(rest[..length]);
            rest = rest[length..];
        }

        parts.Add(rest);
        return parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
    }

    private static int _MaxLength(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DiscordLimit;
        }

        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxLength)
            || maxLength < 100
            || maxLength > DiscordLimit)
        {
            throw new InvalidOperationException($"\"{text.Trim()}\" is not a usable message length. Write a number from 100 to {DiscordLimit}, or leave it blank for {DiscordLimit}.");
        }

        return maxLength;
    }
}
