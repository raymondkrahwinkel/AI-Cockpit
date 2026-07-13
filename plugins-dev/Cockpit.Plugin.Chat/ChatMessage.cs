using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Plugin.Chat;

/// <summary>
/// What a chat service is actually sent. Slack's incoming webhook takes <c>{"text": "…"}</c>; Discord's takes
/// <c>{"content": "…"}</c>. That is the entire difference between them, and keeping it in one small place — rather
/// than in two clients that drift apart — is why this class exists.
/// <para>
/// Both refuse an empty message, and so does this: a webhook that returns 400 for a body nobody meant to send is a
/// step that fails for a reason the operator has to go and look up. Failing here says it in words.
/// </para>
/// </summary>
internal static class ChatMessage
{
    /// <summary>Discord cuts a message at 2000 characters and Slack at 40 000. A command's output can be longer than either.</summary>
    public const int DiscordLimit = 2000;

    public static string Body(ChatService service, string message)
    {
        var text = message.Trim();
        if (text.Length == 0)
        {
            throw new InvalidOperationException("This step has no message to send. Open it and write one — {output} puts what the step before produced in it.");
        }

        if (service == ChatService.Discord && text.Length > DiscordLimit)
        {
            // Cut, and visibly: Discord rejects the whole message otherwise, and a step that failed because the
            // command it reported on was too talkative is a step that fails at the worst possible moment.
            text = string.Concat(text.AsSpan(0, DiscordLimit - 1), "…");
        }

        var body = new JsonObject
        {
            [service == ChatService.Slack ? "text" : "content"] = text,
        };

        return body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
