namespace Cockpit.Plugin.Chat;

/// <summary>Which chat service a channel belongs to. They take a different body, and that is the whole of the difference.</summary>
public enum ChatService
{
    Slack,
    Discord,
}

/// <summary>
/// A place a flow can send a message: a name the operator gives it, and the incoming webhook it posts to.
/// <para>
/// Named, so a flow says "tell releases" rather than carrying a URL around in a node's settings — where it would be
/// in the flow's stored JSON, in a backup, and on screen whenever someone opened the step. A webhook is a credential:
/// it belongs in the plugin's settings, where the backup knows to strip it.
/// </para>
/// </summary>
/// <param name="Name">What the flow calls it: "releases", "team".</param>
/// <param name="Service">Slack or Discord.</param>
/// <param name="WebhookUrl">The incoming webhook. Anyone who has it can post as you, which is why it is not in the flow.</param>
public sealed record ChatChannel(string Name, ChatService Service, string WebhookUrl)
{
    public bool IsComplete => Name.Trim().Length > 0 && WebhookUrl.Trim().Length > 0;
}
