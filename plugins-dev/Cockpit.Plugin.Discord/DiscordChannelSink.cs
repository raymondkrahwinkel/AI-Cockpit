using global::Discord;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Discord;

// The real `IDiscordChannelSink`: posts/edits/reacts against one Discord text channel or direct message over
// Discord.NET's REST surface. `resolveChannel` is asked fresh on every call rather than resolved once — the
// gateway may not have the channel cached yet the moment the bridge tries to use it.
internal sealed class DiscordChannelSink(Func<Task<IMessageChannel?>> resolveChannel) : IDiscordChannelSink
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Dictionary<ulong, PostedMessage> _posted = [];

    private sealed class PostedMessage
    {
        public List<ulong> Parts { get; } = [];
        public IReadOnlyList<AssistantChannelAttachment> Attachments { get; set; } = [];
        public int AttachmentPart { get; set; } = -1;
        public MessageComponent? Components { get; set; }
        public string? Warning { get; set; }
    }

    public async Task<ulong> PostAsync(string text, Guid? consentPromptId = null, DiscordAnswerButtons? answers = null, CancellationToken cancellationToken = default,
        IReadOnlyList<AssistantChannelAttachment>? attachments = null)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = await resolveChannel().ConfigureAwait(false) ?? throw new InvalidOperationException("The configured Discord channel is not reachable.");
            var state = new PostedMessage { Components = consentPromptId is { } id ? _BuildConsentButtons(id) : answers is { } options ? _BuildAnswerButtons(options) : null };
            await _UpdateAsync(channel, state, text, attachments ?? [], cancellationToken).ConfigureAwait(false);
            _posted[state.Parts[0]] = state;
            return state.Parts[0];
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task EditAsync(ulong messageId, string text, bool keepButtons = true, CancellationToken cancellationToken = default,
        IReadOnlyList<AssistantChannelAttachment>? attachments = null)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = await resolveChannel().ConfigureAwait(false) ?? throw new InvalidOperationException("The configured Discord channel is not reachable.");
            if (!_posted.TryGetValue(messageId, out var state))
            {
                state = new PostedMessage();
                state.Parts.Add(messageId);
                _posted[messageId] = state;
            }

            if (!keepButtons)
            {
                state.Components = null;
            }

            await _UpdateAsync(channel, state, text, attachments ?? state.Attachments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static async Task _UpdateAsync(IMessageChannel channel, PostedMessage state, string text,
        IReadOnlyList<AssistantChannelAttachment> attachments, CancellationToken cancellationToken)
    {
        var changed = !state.Attachments.SequenceEqual(attachments);
        if (changed)
        {
            state.Warning = null;
        }

        var oldAttachmentId = state.AttachmentPart >= 0 && state.AttachmentPart < state.Parts.Count ? state.Parts[state.AttachmentPart] : (ulong?)null;
        await _WritePartsAsync(channel, state, text, cancellationToken).ConfigureAwait(false);
        var lastId = state.Parts[^1];
        if (attachments.Count > 0 && (changed || oldAttachmentId != lastId && state.Warning is null))
        {
            var uploads = new List<FileAttachment>();
            try
            {
                if (attachments.Count > 10 || attachments.Any(file => file.Content.Length > 25 * 1024 * 1024))
                {
                    throw new InvalidOperationException("At most 10 files and 25 MB per file may be uploaded.");
                }

                uploads.AddRange(attachments.Select(file => new FileAttachment(new MemoryStream(file.Content, writable: false), file.Name)));
                if (await channel.GetMessageAsync(lastId, options: new RequestOptions { CancelToken = cancellationToken }).ConfigureAwait(false) is not IUserMessage last)
                {
                    throw new InvalidOperationException("The message for the attachments is no longer available.");
                }

                await last.ModifyAsync(properties => properties.Attachments = uploads, new RequestOptions { CancelToken = cancellationToken }).ConfigureAwait(false);
                state.AttachmentPart = state.Parts.Count - 1;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                state.Warning = "\n\n⚠️ Attachment upload failed: " + error.Message;
                state.AttachmentPart = -1;
                await _WritePartsAsync(channel, state, text, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                foreach (var upload in uploads)
                {
                    upload.Dispose();
                }
            }
        }

        if (oldAttachmentId is { } previous && (attachments.Count == 0 || previous != lastId) && state.Parts.Contains(previous)
            && await channel.GetMessageAsync(previous, options: new RequestOptions { CancelToken = cancellationToken }).ConfigureAwait(false) is IUserMessage old)
        {
            await old.ModifyAsync(properties => properties.Attachments = Array.Empty<FileAttachment>(), new RequestOptions { CancelToken = cancellationToken }).ConfigureAwait(false);
        }

        state.Attachments = attachments;
        if (attachments.Count == 0)
        {
            state.AttachmentPart = -1;
        }
    }

    private static async Task _WritePartsAsync(IMessageChannel channel, PostedMessage state, string text, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> parts;
        try
        {
            parts = DiscordMessageText.Split(text + state.Warning);
        }
        catch (ArgumentOutOfRangeException)
        {
            parts = DiscordSendDmStep.Split("⚠️ A table row exceeds Discord's message limit; showing the original text.\n\n" + text + state.Warning, 2000);
        }

        var options = new RequestOptions { CancelToken = cancellationToken };
        for (var index = 0; index < parts.Count; index++)
        {
            var components = index == parts.Count - 1 ? state.Components : null;
            if (index < state.Parts.Count)
            {
                if (await channel.GetMessageAsync(state.Parts[index], options: options).ConfigureAwait(false) is IUserMessage message)
                {
                    await message.ModifyAsync(properties =>
                    {
                        properties.Content = parts[index];
                        properties.Components = components ?? new ComponentBuilder().Build();
                    }, options).ConfigureAwait(false);
                }
            }
            else
            {
                var message = await channel.SendMessageAsync(parts[index], components: components, options: options).ConfigureAwait(false);
                state.Parts.Add(message.Id);
            }
        }

        for (var index = state.Parts.Count - 1; index >= parts.Count; index--)
        {
            await channel.DeleteMessageAsync(state.Parts[index], options).ConfigureAwait(false);
            state.Parts.RemoveAt(index);
        }
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

    private static MessageComponent _BuildAnswerButtons(DiscordAnswerButtons answers)
    {
        var builder = new ComponentBuilder();
        for (var index = 0; index < answers.Labels.Count; index++)
        {
            var label = answers.Labels[index];
            builder.WithButton(label.Length > DiscordAnswerButtons.MaxLabelLength ? label[..DiscordAnswerButtons.MaxLabelLength] : label, answers.Id(index), ButtonStyle.Secondary);
        }

        return builder.Build();
    }
}
