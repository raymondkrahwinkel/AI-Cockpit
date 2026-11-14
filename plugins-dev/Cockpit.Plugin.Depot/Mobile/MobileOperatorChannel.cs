using System.Text.Json;
using Microsoft.Extensions.Logging;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Depot.Mobile;

internal sealed partial class MobileAssistantChannel
{
    private readonly IOperatorActionTransport? _operatorTransport;
    private IOperatorActionChannel? _operatorChannel;

    private void _StartOperatorChannel()
    {
        long generation;
        lock (_gate)
        {
            if (_disposed || _operatorChannel is not null || _operatorTransport is null)
            {
                return;
            }

            generation = _streamGeneration;
        }

        var channel = _operatorTransport.OpenChannel(_deviceId);
        var installed = false;
        lock (_gate)
        {
            if (!_disposed && _operatorChannel is null && generation == _streamGeneration)
            {
                _operatorChannel = channel;
                channel.Changed += _OnOperatorChanged;
                installed = true;
            }
        }

        if (!installed)
        {
            channel.Dispose();
        }
    }

    private void _StopOperatorChannel()
    {
        IOperatorActionChannel? channel;
        lock (_gate)
        {
            channel = _operatorChannel;
            _operatorChannel = null;
        }

        if (channel is not null)
        {
            channel.Changed -= _OnOperatorChanged;
            channel.Dispose();
        }
    }

    private void _OnOperatorChanged(object? sender, EventArgs args) => _SendPending();

    private void _SendPending()
    {
        lock (_pendingPublishGate)
        {
            _PublishPending();
        }
    }

    private void _PublishPending()
    {
        IOperatorActionChannel? channel;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            channel = _operatorChannel;
        }

        if (channel is null)
        {
            return;
        }

        var items = channel.Snapshot.Select(action => JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = action.Id,
            revision = action.Revision,
            capability = action.Capability,
            kind = action.Kind,
            target = action.Target,
            summary = action.Summary,
            title = action.Title,
            risk = action.Risk,
            sourcePlugin = action.SourcePlugin,
            decisions = action.Decisions,
            questionId = action.Kind == "question" ? action.Id : null,
            questions = action.Questions,
        }, _PendingJson)).ToList();

        var snapshotId = Guid.NewGuid().ToString("N");
        List<byte[]> batch = [];
        var size = 0;
        foreach (var encoded in items)
        {
            var item = _PendingItem(encoded);
            if (batch.Count > 0 && size + item.Length + 1 > MobileFrameCipher.MaxPlaintextBytes - _EnvelopeBytes)
            {
                _SendPendingBatch(snapshotId, batch, false);
                batch = [];
                size = 0;
            }

            batch.Add(item);
            size += item.Length + 1;
        }

        _SendPendingBatch(snapshotId, batch, true);
    }

    // A single oversized request stays visible but carries no authority; never approve an incomplete summary.
    private static byte[] _PendingItem(byte[] encoded)
    {
        if (encoded.Length <= MobileFrameCipher.MaxPlaintextBytes - _EnvelopeBytes)
        {
            return encoded;
        }

        using var document = JsonDocument.Parse(encoded);
        var item = document.RootElement;
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = item.GetProperty("id").GetString(),
            revision = item.GetProperty("revision").GetString(),
            target = item.GetProperty("target").GetString(),
            kind = "desktop",
            summary = "This request is too large for the phone. Review it on the desktop.",
            title = item.TryGetProperty("title", out var title) ? Cut(title.GetString() ?? string.Empty, Math.Min(title.GetString()?.Length ?? 0, 300)) : null,
            risk = item.TryGetProperty("risk", out var risk) ? risk.GetString() : null,
            sourcePlugin = item.TryGetProperty("sourcePlugin", out var source) ? source.GetString() : null,
            capability = string.Empty,
            decisions = Array.Empty<string>(),
        });
    }

    private readonly Lock _pendingPublishGate = new();
    private static readonly JsonSerializerOptions _PendingJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private void _SendPendingBatch(string snapshotId, IReadOnlyList<byte[]> items, bool last) =>
        _Send("operator.pending", writer =>
        {
            writer.WriteString("snapshotId", snapshotId);
            writer.WriteBoolean("last", last);
            writer.WriteStartArray("items");
            foreach (var item in items)
            {
                writer.WriteRawValue(item, skipInputValidation: true);
            }

            writer.WriteEndArray();
        });

    private async Task _RespondAsync(string? replyTo, JsonElement body)
    {
        IOperatorActionChannel? channel;
        lock (_gate)
        {
            channel = _operatorChannel;
        }

        var ok = false;
        var error = "This request is already handled or this connection has expired. Reload the list.";
        if (channel is not null && _String(body, "id") is { } actionId && _String(body, "revision") is { } revision
            && _String(body, "capability") is { } capability && _String(body, "decision") is { } decision)
        {
            try
            {
                var result = await channel.RespondAsync(actionId, revision, capability, decision).ConfigureAwait(false);
                ok = result.Ok;
                error = result.Error ?? error;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger?.LogWarning("A phone operator decision failed: {Reason}", exception.GetType().Name);
                error = "The host could not complete this decision. Reload the list.";
            }
        }

        _Send("operator.result", writer =>
        {
            writer.WriteString("replyTo", replyTo);
            writer.WriteBoolean("ok", ok);
            writer.WriteString("error", ok ? null : error);
        });
        _SendPending();
    }

    private async Task _AnswerAsync(string? replyTo, JsonElement body)
    {
        var questionId = _String(body, "questionId");
        var result = AssistantChannelSendResult.Refused("This question is already handled. Reload the list.");
        var channel = _operatorChannel;
        if (channel is not null && questionId is not null && _String(body, "revision") is { } revision
            && _String(body, "capability") is { } capability)
        {
            try
            {
                var answers = body.TryGetProperty("answers", out var groups) && groups.ValueKind == JsonValueKind.Array
                    ? groups.EnumerateArray().Select(group => (IReadOnlyList<string>)(group.ValueKind == JsonValueKind.Array
                        ? group.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString() ?? string.Empty).ToList()
                        : [])).ToList()
                    : [(IReadOnlyList<string>)_Strings(body, "chosen")];
                var answered = await channel.AnswerQuestionsAsync(questionId, revision, capability, answers).ConfigureAwait(false);
                result = answered.Ok ? AssistantChannelSendResult.Sent() : AssistantChannelSendResult.Refused(answered.Error ?? "The question was not answered.");
                if (result.Ok)
                {
                    lock (_gate)
                    {
                        if (_question?.Id == questionId)
                        {
                            _question = null;
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger?.LogWarning("A phone question answer failed: {Reason}", exception.GetType().Name);
                result = AssistantChannelSendResult.Refused("The host could not answer this question. Reload the list.");
            }
        }

        _Send("assistant.answered", writer =>
        {
            writer.WriteString("replyTo", replyTo);
            writer.WriteBoolean("ok", result.Ok);
            writer.WriteString("error", result.Error);
        });
        _SendPending();
    }
}
