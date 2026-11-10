using System.ComponentModel;
using System.Text.Json;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Notifications;
using ModelContextProtocol.Server;

namespace Cockpit.Infrastructure.Plugins;

internal sealed class OperatorPhoneMcpTools(ISessionRegistry sessions, OperatorPhoneNotifications notifications)
{
    [McpServerTool(Name = "notify_operator_phone", ReadOnly = false, Destructive = false)]
    [Description("Sends a deliberate ping to the operator's connected phone. Sender comes from your session, never from an argument. Only title and short text: never include secrets or transcript. At most three pings per sender per minute. Reports streams accepting the ping, not device delivery. No extra consent click.")]
    public string NotifyOperatorPhone(
        [Description("Title, 1–80 characters; no secrets.")] string title,
        [Description("Short text, 1–240 characters; no secrets or transcript.")] string text,
        [Description("assistant or a live session/pane id to open on tap.")] string target,
        [Description("normal or important; important passes the app's quiet hours.")] string urgency = "normal")
    {
        if (McpRequestContext.CurrentCaller is not { Kind: McpCallerKind.Session or McpCallerKind.Assistant } caller)
        {
            return _Reply(0, "This request has no local session identity.");
        }
        var sender = caller.PaneId == AssistantIdentity.PaneId ? sessions.Assistant : sessions.Find(caller.PaneId);
        if (sender is null)
        {
            return _Reply(0, "The calling session is no longer live.");
        }
        if (!_Valid(title, 80) || !_Valid(text, 240) || urgency is not ("normal" or "important"))
        {
            return _Reply(0, "Use a title of 1–80 and text of 1–240 characters without control characters, and urgency normal or important.");
        }
        target = target == "assistant" ? AssistantIdentity.PaneId : target;
        if (!_Valid(target, 128) || (target != AssistantIdentity.PaneId && sessions.Find(target) is null))
        {
            return _Reply(0, "The target is not a live session/pane id.");
        }
        string senderName = new(sender.Title.Where(character => !char.IsControl(character)).Take(80).ToArray());
        var result = notifications.Send(new OperatorPhoneNotification(caller.PaneId, senderName, title, text, urgency, target), DateTimeOffset.UtcNow);
        return _Reply(result.Accepted, result.Error);
    }

    private static bool _Valid(string text, int maximum) => !string.IsNullOrWhiteSpace(text) && text.Length <= maximum && !text.Any(char.IsControl);
    private static string _Reply(int accepted, string? error) => JsonSerializer.Serialize(new { ok = error is null, acceptedStreams = accepted, error });
}
