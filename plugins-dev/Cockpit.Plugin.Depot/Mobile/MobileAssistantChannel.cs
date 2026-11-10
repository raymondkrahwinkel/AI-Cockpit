using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Mobile;

// AC-2227: one paired phone as a door onto the assistant's own conversation, next to Slack and Discord. The gateway is
// opened for this phone's device id alone, so the host refuses anything else. What goes over the line is decided here
// (PROTOCOL.md § 10); nothing of a message reaches a log, only its type and size.
internal sealed class MobileAssistantChannel : IDisposable
{
    public const int HistoryLimit = 50;

    // A frame's JSON around its rows: the envelope, the reply id and the flags.
    private const int _EnvelopeBytes = 512;

    private static readonly TimeSpan _Coalesce = TimeSpan.FromMilliseconds(250);

    private readonly string _deviceId;
    private readonly IAssistantChannelGateway? _gateway;
    private readonly Func<byte[], bool> _send;
    private readonly string _cockpitName;
    private readonly string? _cockpitVersion;
    private readonly ILogger? _logger;
    private readonly MobileSessionChannel? _sessions;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, AssistantChannelRow> _dirty = [];
    private readonly Dictionary<Guid, string> _prompts = [];
    private (string Id, string Text)? _question;
    private long _nextId;
    private bool _chatOpen;
    private bool _unseenAnswer;
    private bool _flushScheduled;
    private bool _disposed;

    public MobileAssistantChannel(
        string deviceId,
        IAssistantChannelGateway? gateway,
        Func<byte[], bool> send,
        string cockpitName,
        string? cockpitVersion,
        ILogger? logger = null,
        ICockpitHost? host = null)
    {
        _deviceId = deviceId;
        _gateway = gateway;
        _send = send;
        _cockpitName = cockpitName;
        _cockpitVersion = cockpitVersion;
        _logger = logger;
        _sessions = host is null ? null : new MobileSessionChannel(host, _Send, logger);
        if (gateway is not null)
        {
            gateway.RowChanged += _OnRowChanged;
            gateway.TurnEnded += _OnTurnEnded;
            gateway.ConsentPromptOpened += _OnConsentOpened;
            gateway.ConsentPromptClosed += _OnConsentClosed;
        }
    }

    // A frame from this phone that MobileFrameCipher opened. Anything that is not a message this version knows is
    // ignored, and the stream stays open (PROTOCOL.md § 6).
    public void Receive(byte[] plaintext)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        try
        {
            _Receive(plaintext);
        }
        catch (Exception exception)
        {
            _logger?.LogDebug("A phone's message could not be handled: {Reason}", exception.GetType().Name);
        }
    }

    private void _Receive(byte[] plaintext)
    {
        string? type;
        string? id;
        JsonElement body;
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            type = root.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() : null;
            id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
            body = root.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.Object ? bodyElement.Clone() : default;
        }
        catch (JsonException)
        {
            _logger?.LogDebug("A phone sent a message that is not JSON ({Bytes} bytes); ignored.", plaintext.Length);
            return;
        }

        switch (type)
        {
            case "assistant.send":
                _ = _SendToAssistantAsync(id, _String(body, "text"));
                break;
            case "assistant.history":
                _ = _HistoryAsync(id, _Int(body, "limit") ?? HistoryLimit, _String(body, "before"));
                break;
            case "assistant.chat":
                _Chat(body.ValueKind == JsonValueKind.Object && body.TryGetProperty("open", out var open) && open.ValueKind == JsonValueKind.True);
                break;
            case "cockpit.status":
                _Send("cockpit.status", writer =>
                {
                    _ReplyTo(writer, id);
                    writer.WriteString("name", _cockpitName);
                    writer.WriteString("version", _cockpitVersion);
                    writer.WriteBoolean("assistant", _gateway is not null);
                });
                break;
            case "sessions.list" when _sessions is not null:
                _sessions.List(id);
                break;
            case "session.transcript" when _sessions is not null:
                _ = _sessions.TranscriptAsync(id, _String(body, "paneId"), _Int(body, "count"), _Int(body, "since"));
                break;
            case "session.send" when _sessions is not null:
                _ = _sessions.SendAsync(id, _String(body, "paneId"), _String(body, "text"));
                break;
            default:
                _logger?.LogDebug("A phone sent a message type this Cockpit does not know; ignored.");
                break;
        }
    }

    // The phone's stream is gone: its chat counts as closed until a new stream says otherwise.
    public void StreamEnded()
    {
        lock (_gate)
        {
            _chatOpen = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _dirty.Clear();
            _prompts.Clear();
            _question = null;
        }

        if (_gateway is not null)
        {
            _gateway.RowChanged -= _OnRowChanged;
            _gateway.TurnEnded -= _OnTurnEnded;
            _gateway.ConsentPromptOpened -= _OnConsentOpened;
            _gateway.ConsentPromptClosed -= _OnConsentClosed;
            _gateway.Dispose();
        }
    }

    private async Task _SendToAssistantAsync(string? id, string? text)
    {
        AssistantChannelSendResult result;
        if (_gateway is null)
        {
            result = AssistantChannelSendResult.Refused("The assistant is not running on this Cockpit.");
        }
        else if (string.IsNullOrWhiteSpace(text))
        {
            result = AssistantChannelSendResult.Refused("The message is empty.");
        }
        else if (_OpenQuestion() is not null)
        {
            // While the assistant's question waits, the host reads a text reply as its answer (AC-1553); in v1 that is
            // decided on the desktop only, like a permission prompt.
            result = AssistantChannelSendResult.Refused("Answer the question on the desktop first.");
        }
        else
        {
            try
            {
                result = await _gateway.SendAsync(_deviceId, text, [], CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger?.LogWarning("Handing a phone's message to the assistant failed: {Reason}", exception.GetType().Name);
                result = AssistantChannelSendResult.Refused("The assistant did not take the message.");
            }
        }

        // An ignored sender hears nothing back, as on every channel.
        if (result.Ignored)
        {
            return;
        }

        _Send("assistant.sent", writer =>
        {
            _ReplyTo(writer, id);
            writer.WriteBoolean("ok", result.Ok);
            if (result.Error is { } error)
            {
                writer.WriteString("error", error);
            }
        });
    }

    // The tail of the conversation, at most HistoryLimit rows, older than `before` when given; the host reads it (AC-2238).
    private async Task _HistoryAsync(string? id, int limit, string? before)
    {
        AssistantChannelHistoryPage page = AssistantChannelHistoryPage.Empty;
        if (_gateway is not null)
        {
            try
            {
                page = await _gateway.ReadRowsAsync(Math.Clamp(limit, 1, HistoryLimit), before, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger?.LogWarning("Reading the conversation's history for a phone failed: {Reason}", exception.GetType().Name);
            }
        }

        // The host answers newest first; the phone reads a page oldest first, like the live rows.
        _SendRows("assistant.history", [.. page.Rows.Reverse()], writer =>
        {
            _ReplyTo(writer, id);
            writer.WriteBoolean("more", page.Before is not null);
        });
    }

    private void _Chat(bool open)
    {
        List<(Guid Id, string Title)> prompts;
        (string Id, string Text)? question;
        lock (_gate)
        {
            _chatOpen = open;
            if (open)
            {
                _unseenAnswer = false;
            }

            prompts = open ? [.. _prompts.Select(prompt => (prompt.Key, prompt.Value))] : [];
            question = open ? _question : null;
        }

        foreach (var (promptId, title) in prompts)
        {
            _SendConsent(promptId, title);
        }

        if (question is { } waiting)
        {
            _SendQuestion(waiting);
        }
    }

    private void _OnRowChanged(object? sender, AssistantChannelRow row)
    {
        (string Id, string Text)? asked = null;
        string? answered = null;
        bool open;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // The host clears its waiting question on the operator's next message, wherever it came from; so does this.
            if (row.Kind == AssistantChannelRowKind.UserText && _question is { } waiting)
            {
                answered = waiting.Id;
                _question = null;
            }

            if (row is { Questions.Count: > 0, CorrelationId: { } correlationId } && _question?.Id != correlationId)
            {
                answered = _question?.Id ?? answered;
                _question = asked = (correlationId, row.Questions[0].Question);
            }

            open = _chatOpen;
        }

        if (open && answered is not null)
        {
            _Send("assistant.questionClosed", writer => writer.WriteString("questionId", answered));
        }

        if (open && asked is { } question)
        {
            _SendQuestion(question);
        }

        _Queue(row);
    }

    private (string Id, string Text)? _OpenQuestion()
    {
        lock (_gate)
        {
            return _question;
        }
    }

    // Read-only, like a permission prompt: the phone shows the question and that it waits on the desktop.
    private void _SendQuestion((string Id, string Text) question) =>
        _Send("assistant.question", writer =>
        {
            writer.WriteString("questionId", question.Id);
            writer.WriteString("question", question.Text);
        });

    private void _Queue(AssistantChannelRow row)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (row.Kind == AssistantChannelRowKind.AssistantText && !_chatOpen)
            {
                _unseenAnswer = true;
            }

            if (!_chatOpen)
            {
                return;
            }

            // Streaming raises a row per delta; the phone gets the latest of each row at most four times a second.
            _dirty[row.Id] = row;
            if (_flushScheduled)
            {
                return;
            }

            _flushScheduled = true;
        }

        _ = Task.Delay(_Coalesce).ContinueWith(_ => _Flush(), TaskScheduler.Default);
    }

    private void _Flush()
    {
        List<AssistantChannelRow> rows;
        lock (_gate)
        {
            _flushScheduled = false;
            if (_disposed || !_chatOpen || _dirty.Count == 0)
            {
                _dirty.Clear();
                return;
            }

            rows = [.. _dirty.Values];
            _dirty.Clear();
        }

        _SendRows("assistant.rows", rows, _ => { });
    }

    private void _OnTurnEnded(object? sender, EventArgs e)
    {
        bool open;
        bool notify;
        lock (_gate)
        {
            open = _chatOpen;
            notify = !open && _unseenAnswer;
            _unseenAnswer = false;
        }

        if (open)
        {
            _Flush();
            _Send("assistant.turnEnded", _ => { });
        }
        else if (notify)
        {
            // Without text: only that there is something, never what (AC-2227). It goes only to a phone with an open
            // stream; a phone without one learns nothing until it connects.
            _Send("notify", writer => writer.WriteString("kind", "assistantMessage"));
        }
    }

    // Read-only in v1: the phone shows "Waiting for you on the desktop", and RespondToConsent is never called from here.
    private void _OnConsentOpened(object? sender, AssistantChannelConsentPrompt prompt)
    {
        bool open;
        lock (_gate)
        {
            _prompts[prompt.Id] = prompt.Request.Title;
            open = _chatOpen;
        }

        if (open)
        {
            _SendConsent(prompt.Id, prompt.Request.Title);
        }
    }

    private void _OnConsentClosed(object? sender, Guid promptId)
    {
        bool open;
        lock (_gate)
        {
            _prompts.Remove(promptId);
            open = _chatOpen;
        }

        if (open)
        {
            _Send("assistant.consentClosed", writer => writer.WriteString("promptId", promptId.ToString("n")));
        }
    }

    private void _SendConsent(Guid promptId, string title) =>
        _Send("assistant.consent", writer =>
        {
            writer.WriteString("promptId", promptId.ToString("n"));
            writer.WriteString("title", title);
        });

    // Rows packed into as few frames as fit, since Depot counts sends per second; `last` marks the final frame of the
    // batch, because the relay can cut a stream between frames (PROTOCOL.md § 9.3).
    private void _SendRows(string type, IReadOnlyList<AssistantChannelRow> rows, Action<Utf8JsonWriter> fields)
    {
        var budget = MobileFrameCipher.MaxPlaintextBytes - _EnvelopeBytes;
        List<byte[]> batch = [];
        var size = 0;
        foreach (var row in rows.Select(row => _Row(row, budget)))
        {
            if (batch.Count > 0 && size + row.Length + 1 > budget)
            {
                _SendBatch(type, batch, false, fields);
                batch = [];
                size = 0;
            }

            batch.Add(row);
            size += row.Length + 1;
        }

        _SendBatch(type, batch, true, fields);
    }

    private void _SendBatch(string type, List<byte[]> rows, bool last, Action<Utf8JsonWriter> fields) =>
        _Send(type, writer =>
        {
            fields(writer);
            writer.WriteBoolean("last", last);
            writer.WriteStartArray("rows");
            foreach (var row in rows)
            {
                writer.WriteRawValue(row, skipInputValidation: true);
            }

            writer.WriteEndArray();
        });

    // A row too big for a frame on its own keeps the start of its text and says it was cut.
    private static byte[] _Row(AssistantChannelRow row, int budget)
    {
        var text = row.Text;
        var result = row.ResultText;
        while (true)
        {
            var truncated = text.Length != row.Text.Length || result?.Length != row.ResultText?.Length;
            var encoded = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = row.Id.ToString("n"),
                kind = JsonNamingPolicy.CamelCase.ConvertName(row.Kind.ToString()),
                text,
                timestamp = row.Timestamp,
                toolName = row.ToolName,
                resultText = result,
                truncated,
            });
            if (encoded.Length <= budget || (text.Length == 0 && string.IsNullOrEmpty(result)))
            {
                return encoded;
            }

            text = Half(text);
            result = result is null ? null : Half(result);
        }
    }

    internal static string Half(string text) => Cut(text, text.Length / 2);

    // The first `length` chars, one fewer when that would split a surrogate pair.
    internal static string Cut(string text, int length) =>
        length > 0 && char.IsHighSurrogate(text[length - 1]) ? text[..(length - 1)] : text[..length];

    private void _Send(string type, Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteString("id", $"c{Interlocked.Increment(ref _nextId)}");
            writer.WriteStartObject("body");
            body(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var plaintext = buffer.ToArray();
        if (plaintext.Length > MobileFrameCipher.MaxPlaintextBytes)
        {
            _logger?.LogWarning("A {Type} message for a phone came out at {Bytes} bytes, over a frame; not sent.", type, plaintext.Length);
            return;
        }

        _send(plaintext);
    }

    private static void _ReplyTo(Utf8JsonWriter writer, string? id) => writer.WriteString("replyTo", id);

    private static string? _String(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? _Int(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}
