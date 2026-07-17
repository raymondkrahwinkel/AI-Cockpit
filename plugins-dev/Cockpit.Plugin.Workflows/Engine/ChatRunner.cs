using System.Text;
using System.Text.Json.Nodes;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// Sends a message to Slack or Discord (#69), through an incoming webhook the step carries itself. The cockpit's own
/// Notify step tells <em>you</em>, on your screen; this tells everyone else, which is a different act.
/// <para>
/// The two services differ in one thing and nothing else: Slack's webhook takes <c>{"text": …}</c> and Discord's takes
/// <c>{"content": …}</c>. Keeping that in one place rather than in two runners is why this is one class.
/// </para>
/// <para>
/// Discord refuses a message over two thousand characters outright, so a long one is cut and visibly ends in an
/// ellipsis — a step that failed because the command it was reporting on had a lot to say would fail at the worst
/// possible moment. Slack takes them, and is left alone.
/// </para>
/// <para>
/// A message the service refused fails the step. A notification nobody received is worse than an error nobody missed.
/// </para>
/// </summary>
internal sealed class ChatRunner(string typeId, bool discord) : IStepRunner
{
    /// <summary>Discord's own limit. Past it, the whole message is rejected rather than truncated for you.</summary>
    public const int DiscordLimit = 2000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public string TypeId => typeId;

    public ConsentRisk? RequiredConsent => ConsentRisk.Dangerous;

    public string ConsentAction(StepContext context)
    {
        var message = context.Resolve(context.Node.Parameters.GetValueOrDefault("Message")).Text.Trim();
        // The full webhook URL, not just its host: the path/token is what actually selects the channel, so two hooks
        // on the same host (a real one and an attacker's) must be distinguishable in the prompt.
        var webhook = context.Resolve(context.Node.Parameters.GetValueOrDefault("Webhook URL")).Text.Trim();
        return $"Post to {(discord ? "Discord" : "Slack")} {webhook}:\n{message}";
    }

    public async Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var message = context.Resolve(context.Node.Parameters.GetValueOrDefault("Message")).Text.Trim();
        if (message.Length == 0)
        {
            throw new InvalidOperationException("This step has no message to send. Open it and write one — {output} puts what the step before produced in it.");
        }

        var webhook = context.Resolve(context.Node.Parameters.GetValueOrDefault("Webhook URL")).Text.Trim();
        if (!Uri.TryCreate(webhook, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                webhook.Length == 0
                    ? "This step has no webhook. Paste the incoming-webhook URL of the channel you want to post to."
                    : $"'{webhook}' is not an https webhook URL.");
        }

        using var content = new StringContent(Body(message, discord), Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(url, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var said = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            var service = discord ? "Discord" : "Slack";

            throw new InvalidOperationException(
                said.Length > 0
                    ? $"{service} refused the message ({(int)response.StatusCode}): {said}"
                    : $"{service} refused the message ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }

        return new StepOutcome(
            [WorkflowItem.Of("message", message)],
            $"Sent to {(discord ? "Discord" : "Slack")}: {message}");
    }

    /// <summary>The body each service expects — and, for Discord, a message cut to a length it will accept.</summary>
    public static string Body(string message, bool discord)
    {
        var text = discord && message.Length > DiscordLimit
            ? string.Concat(message.AsSpan(0, DiscordLimit - 1), "…")
            : message;

        return new JsonObject { [discord ? "content" : "text"] = text }.ToJsonString();
    }
}
