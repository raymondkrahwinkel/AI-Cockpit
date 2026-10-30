using Microsoft.Extensions.AI;

namespace Cockpit.Infrastructure.Sessions;

internal sealed class LocalReasoningChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(_WithReasoning(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(_WithReasoning(messages), options, cancellationToken);

    private static IEnumerable<ChatMessage> _WithReasoning(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            var reasoning = string.Concat(message.Contents.OfType<TextReasoningContent>().Select(content => content.Text));
            if (message.Role != ChatRole.Assistant || reasoning.Length == 0)
            {
                yield return message;
                continue;
            }

            var copy = message.Clone();
            copy.RawRepresentation = null;
            copy.Contents =
            [
                new TextContent($"<think>{reasoning}</think>{message.Text}"),
                .. message.Contents.Where(content => content is not TextContent and not TextReasoningContent),
            ];
            yield return copy;
        }
    }
}
