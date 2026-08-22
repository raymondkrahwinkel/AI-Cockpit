using global::Discord;

namespace Cockpit.Plugin.Discord;

/// <summary>
/// The real <see cref="IDiscordChannelSink"/>: posts/edits/reacts against one Discord text channel over Discord.NET's
/// REST surface. <paramref name="resolveChannel"/> is asked fresh on every call rather than resolved once — the
/// gateway may not have the channel cached yet the moment the bridge tries to use it.
/// </summary>
internal sealed class DiscordChannelSink(Func<Task<ITextChannel?>> resolveChannel) : IDiscordChannelSink
{
    // Discord's own hard cap on a message body.
    private const int _MessageLimit = 2000;

    public async Task<ulong> PostAsync(string text, Guid? consentPromptId = null, CancellationToken cancellationToken = default)
    {
        var channel = await resolveChannel().ConfigureAwait(false) ?? throw new InvalidOperationException("The configured Discord channel is not reachable.");
        var components = consentPromptId is { } id ? _BuildConsentButtons(id) : null;
        var message = await channel.SendMessageAsync(_Truncate(text), components: components).ConfigureAwait(false);
        return message.Id;
    }

    public async Task EditAsync(ulong messageId, string text, bool keepButtons = true, CancellationToken cancellationToken = default)
    {
        var channel = await resolveChannel().ConfigureAwait(false) ?? throw new InvalidOperationException("The configured Discord channel is not reachable.");
        if (await channel.GetMessageAsync(messageId, options: new RequestOptions { CancelToken = cancellationToken }).ConfigureAwait(false) is not IUserMessage message)
        {
            return;
        }

        await message.ModifyAsync(props =>
        {
            props.Content = _Truncate(text);
            if (!keepButtons)
            {
                props.Components = new ComponentBuilder().Build();
            }
        }).ConfigureAwait(false);
    }

    public async Task AddReactionAsync(ulong messageId, string emoji, CancellationToken cancellationToken = default)
    {
        var channel = await resolveChannel().ConfigureAwait(false) ?? throw new InvalidOperationException("The configured Discord channel is not reachable.");
        if (await channel.GetMessageAsync(messageId, options: new RequestOptions { CancelToken = cancellationToken }).ConfigureAwait(false) is IUserMessage message)
        {
            await message.AddReactionAsync(new Emoji(emoji)).ConfigureAwait(false);
        }
    }

    private static MessageComponent _BuildConsentButtons(Guid promptId) =>
        new ComponentBuilder()
            .WithButton("Approve", DiscordConsentButtonId.Approve(promptId), ButtonStyle.Success)
            .WithButton("Deny", DiscordConsentButtonId.Deny(promptId), ButtonStyle.Danger)
            .Build();

    private static string _Truncate(string text) => text.Length > _MessageLimit ? text[.._MessageLimit] : text;
}
