using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Infrastructure.Assistant;

// AC-1544: the channels open for a plugin's message, shared by every plugin's host, and the questions sent through
// them waiting for an answer by correlation id. An answer arrives through the channel's own gateway, which has checked
// the access list already; here it is matched to its sender's handler, once, and only from the channel it went out on.
public sealed class OperatorMessageRegistry(ILogger<OperatorMessageRegistry> logger) : ISingletonService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string Name, IOperatorMessageChannel Channel)> _channels = new(StringComparer.Ordinal);

    // ponytail: unanswered questions stay until their channel closes; bound them by age if a sender floods one.
    private readonly Dictionary<string, _Pending> _pending = new(StringComparer.Ordinal);
    private long _sent;

    public IReadOnlyList<OperatorMessageChannelInfo> Channels
    {
        get
        {
            lock (_gate)
            {
                return [.. _channels.Select(channel => new OperatorMessageChannelInfo(channel.Key, channel.Value.Name, channel.Value.Channel.Targets))];
            }
        }
    }

    public void Open(string channelId, string name, IOperatorMessageChannel channel)
    {
        lock (_gate)
        {
            _channels[channelId] = (name, channel);
        }
    }

    // Only the channel that is open under this id now: a replaced channel closing late must not take its successor along.
    public void Close(string channelId, IOperatorMessageChannel channel)
    {
        lock (_gate)
        {
            if (_channels.TryGetValue(channelId, out var open) && ReferenceEquals(open.Channel, channel))
            {
                _channels.Remove(channelId);
                foreach (var key in _pending.Where(pending => pending.Value.ChannelId == channelId).Select(pending => pending.Key).ToList())
                {
                    _pending.Remove(key);
                }
            }
        }
    }

    public async Task<OperatorMessageResult> SendAsync(
        string channelId, string targetId, OperatorMessage message, Func<OperatorMessageAnswer, Task>? onAnswer, CancellationToken cancellationToken)
    {
        IOperatorMessageChannel channel;
        lock (_gate)
        {
            if (!_channels.TryGetValue(channelId, out var open))
            {
                return OperatorMessageResult.Failed($"No channel '{channelId}' is open: its plugin is not installed, switched off or not connected.");
            }

            channel = open.Channel;
        }

        if (channel.Targets.All(target => target.Id != targetId))
        {
            return OperatorMessageResult.Failed($"The channel '{channelId}' cannot deliver to '{targetId}'.");
        }

        if (message.Question is { } question && onAnswer is not null)
        {
            message = message with { CorrelationId = message.CorrelationId ?? Guid.NewGuid().ToString("n") };
            lock (_gate)
            {
                _pending[message.CorrelationId] = new _Pending(channelId, question, onAnswer, ++_sent);
            }
        }

        try
        {
            var result = await channel.SendAsync(targetId, message, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                _Forget(message.CorrelationId);
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _Forget(message.CorrelationId);
            return OperatorMessageResult.Failed(exception.Message);
        }
    }

    // The newest question still waiting on this channel: what a bare text reply such as "2" answers.
    public (string CorrelationId, AssistantChannelQuestion Question)? Waiting(string channelId)
    {
        lock (_gate)
        {
            return _pending.Where(pending => pending.Value.ChannelId == channelId)
                .OrderBy(pending => pending.Value.Order)
                .Select(pending => ((string, AssistantChannelQuestion)?)(pending.Key, pending.Value.Question))
                .FirstOrDefault();
        }
    }

    // Null when no question of this channel waits under the id; else the answer was handed over, or refused.
    public async Task<AssistantChannelSendResult?> AnswerAsync(string channelId, string senderUserId, string correlationId, IReadOnlyList<string> chosen)
    {
        _Pending? pending;
        lock (_gate)
        {
            pending = _pending.GetValueOrDefault(correlationId);
            if (pending is null || pending.ChannelId != channelId)
            {
                return null;
            }

            if (!Takes(pending.Question, chosen))
            {
                return AssistantChannelSendResult.Refused("That is not an answer this question takes.");
            }

            _pending.Remove(correlationId);
        }

        try
        {
            await pending.OnAnswer(new OperatorMessageAnswer(correlationId, senderUserId, [.. chosen.Select(pick => pick.Trim())])).ConfigureAwait(false);
            return AssistantChannelSendResult.Sent();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The answer under {CorrelationId} on channel {ChannelId} could not be handed over.", correlationId, channelId);
            return AssistantChannelSendResult.Refused(exception.Message);
        }
    }

    // One rule for the assistant's questions and a plugin's: one pick unless several are allowed, from the list
    // unless the operator's own words are welcome.
    public static bool Takes(AssistantChannelQuestion question, IReadOnlyList<string> chosen)
    {
        var picks = chosen.Select(pick => pick.Trim()).Where(pick => pick.Length > 0).Distinct().ToList();
        var outsideTheList = !question.AllowOther && picks.Any(pick => question.Options.All(option => option.Label != pick));
        return picks.Count > 0 && (question.MultiSelect || picks.Count == 1) && !outsideTheList;
    }

    private void _Forget(string? correlationId)
    {
        if (correlationId is null)
        {
            return;
        }

        lock (_gate)
        {
            _pending.Remove(correlationId);
        }
    }

    private sealed record _Pending(string ChannelId, AssistantChannelQuestion Question, Func<OperatorMessageAnswer, Task> OnAnswer, long Order);
}
