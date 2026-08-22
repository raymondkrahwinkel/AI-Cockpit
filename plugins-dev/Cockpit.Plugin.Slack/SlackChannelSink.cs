using SlackNet;
using SlackNet.Blocks;
using SlackNet.WebApi;

namespace Cockpit.Plugin.Slack;

// The real `ISlackChannelSink`: posts/edits/reacts against one Slack channel over SlackNet's Web API.
internal sealed class SlackChannelSink(ISlackApiClient api, string channelId) : ISlackChannelSink
{
    // Slack's own hard cap on a message body.
    private const int _MessageLimit = 40000;

    public async Task<string> PostAsync(string text, Guid? consentPromptId = null, CancellationToken cancellationToken = default)
    {
        var message = new Message
        {
            Channel = channelId,
            Text = _Truncate(text),
            Blocks = consentPromptId is { } id ? _BuildConsentButtons(id) : [],
        };

        var response = await api.Chat.PostMessage(message, cancellationToken).ConfigureAwait(false);
        return response.Ts;
    }

    public async Task EditAsync(string messageTs, string text, bool keepButtons = true, CancellationToken cancellationToken = default)
    {
        await api.Chat.Update(new MessageUpdate
        {
            ChannelId = channelId,
            Ts = messageTs,
            Text = _Truncate(text),
            // Leave as null to leave existing blocks alone (MessageUpdate's own rule) — stripping only happens
            // when keepButtons is false.
            Blocks = keepButtons ? null : [],
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task AddReactionAsync(string messageTs, string emoji, CancellationToken cancellationToken = default) =>
        api.Reactions.AddToMessage(emoji, channelId, messageTs, cancellationToken);

    private static IList<Block> _BuildConsentButtons(Guid promptId) =>
    [
        new ActionsBlock
        {
            Elements =
            [
                new Button { Text = "Approve", ActionId = SlackConsentButtonId.Approve(promptId), Style = ButtonStyle.Primary },
                new Button { Text = "Deny", ActionId = SlackConsentButtonId.Deny(promptId), Style = ButtonStyle.Danger },
            ],
        },
    ];

    private static string _Truncate(string text) => text.Length > _MessageLimit ? text[.._MessageLimit] : text;
}
