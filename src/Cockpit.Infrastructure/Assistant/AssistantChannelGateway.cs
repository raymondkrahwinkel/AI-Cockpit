using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Images;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Assistant;

// AC-1023: the host half of `IAssistantChannelGateway`, where a refusal is a result rather than an exception. The
// identity check in `SendAsync` and the prompt filtering further down are the security boundary itself, host-side so
// a plugin cannot skip them. AC-1379: no thread of its own — the app hands it a host that marshals to the UI thread.
public sealed class AssistantChannelGateway : IAssistantChannelGateway
{
    private readonly AssistantChannelContribution _channel;
    private readonly IAssistantSessionHost _host;
    private readonly IConsentBroker _consent;
    private readonly ILogger<AssistantChannelGateway> _logger;

    // Row identity for a plugin, which needs something stable to recognise "the same message, longer" by: one Guid per
    // row id, with a hash of the text and result last relayed so a change to anything else stays quiet. ponytail: kept
    // for the session's life, as the rows are; prune on a row removal if the upsert stream ever carries one.
    private readonly Dictionary<string, (Guid Id, int Content)> _relayedRows = new(StringComparer.Ordinal);

    private readonly HashSet<Guid> _relayedPrompts = [];

    // AC-1553: the questions relayed, by correlation id, until answered; the newest is the one a bare text reply such
    // as "2" answers, until the operator's next message (here or in the app) moves the conversation on.
    private readonly Dictionary<string, IReadOnlyList<AssistantChannelQuestion>> _questions = new(StringComparer.Ordinal);
    private string? _waiting;

    // AC-1544: where this channel's sending half is offered to other plugins, and their answers matched.
    private readonly OperatorMessageRegistry? _messages;

    private IAssistantSession? _observed;
    private bool _wasBusy;
    private bool _disposed;

    public AssistantChannelGateway(
        AssistantChannelContribution channel,
        IAssistantSessionHost host,
        IConsentBroker consent,
        ILogger<AssistantChannelGateway> logger,
        OperatorMessageRegistry? messages = null)
    {
        _channel = channel;
        _host = host;
        _consent = consent;
        _logger = logger;
        _messages = messages;

        if (channel.Messages is { } sending)
        {
            _messages?.Open(channel.Id, channel.Name, sending);
        }

        _host.PropertyChanged += _OnHostPropertyChanged;
        _consent.PromptOpened += _OnPromptOpened;
        _consent.PromptClosed += _OnPromptClosed;

        _WatchSession(_host.Session);
    }

    // The same answer the chat window's paste button gets: the running model sees images (AC-2265).
    public bool AcceptsImages => !_disposed && _host.Session is { CanPasteImages: true };

    public event EventHandler<AssistantChannelRow>? RowChanged;

    public event EventHandler? TurnEnded;

    public event EventHandler<AssistantChannelConsentPrompt>? ConsentPromptOpened;

    public event EventHandler<Guid>? ConsentPromptClosed;

    public IReadOnlyList<AssistantChannelRow> PendingQuestions
    {
        get
        {
            lock (_relayedRows)
            {
                return [.. _questions.Select(pair => new AssistantChannelRow
                {
                    Id = Guid.Parse(pair.Key), Kind = AssistantChannelRowKind.Question,
                    Text = pair.Value[0].Question, Timestamp = DateTimeOffset.UtcNow,
                    CorrelationId = pair.Key, Questions = pair.Value,
                })];
            }
        }
    }

    public async Task<AssistantChannelSendResult> AnswerQuestionsAsync(string senderUserId, string correlationId,
        IReadOnlyList<IReadOnlyList<string>> answers, CancellationToken cancellationToken = default)
    {
        if (_disposed || !_IsAllowed(senderUserId))
        {
            return AssistantChannelSendResult.Refused("This channel cannot take this answer.");
        }

        IReadOnlyList<AssistantChannelQuestion>? questions;
        lock (_relayedRows)
        {
            questions = _questions.GetValueOrDefault(correlationId);
        }

        if (questions is null || answers.Count != questions.Count
            || questions.Where((question, index) => !OperatorMessageRegistry.Takes(question, answers[index])).Any())
        {
            return AssistantChannelSendResult.Refused("That is not an answer this question takes.");
        }

        if (_Take(correlationId) is null)
        {
            return AssistantChannelSendResult.Refused("No question waits under this id.");
        }

        var text = string.Join("\n", questions.Select((question, index) => StructuredQuestionJson.AnswerText(question.Question, string.Join(", ", answers[index]))));
        return await _DeliverAsync(text, [], cancellationToken).ConfigureAwait(false) ?? AssistantChannelSendResult.Sent();
    }

    public async Task<AssistantChannelTurn> TryStartReadOnlyTurnAsync(string senderUserId, string text, CancellationToken cancellationToken = default)
    {
        if (_disposed || !_IsAllowed(senderUserId) || string.IsNullOrWhiteSpace(text))
        {
            return AssistantChannelTurn.Refused(AssistantTurnAdmission.NoAssistant);
        }

        ReadOnlyTurnReceipt receipt = await _host.TryStartReadOnlyTurnAsync(text, cancellationToken).ConfigureAwait(false);
        AssistantTurnAdmission admission = receipt.Admission switch
        {
            ReadOnlyTurnAdmission.Accepted => AssistantTurnAdmission.Accepted,
            ReadOnlyTurnAdmission.Busy => AssistantTurnAdmission.Busy,
            _ => AssistantTurnAdmission.NoAssistant,
        };
        return new AssistantChannelTurn(admission, receipt.Started, receipt.Completion);
    }

    public Task<AssistantChannelSendResult> SendAsync(
        string senderUserId,
        string text,
        CancellationToken cancellationToken = default) =>
        SendAsync(senderUserId, text, [], cancellationToken);

    public async Task<AssistantChannelSendResult> SendAsync(
        string senderUserId,
        string text,
        IReadOnlyList<byte[]> images,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return AssistantChannelSendResult.Refused("This channel is closed.");
        }

        if (!_IsAllowed(senderUserId))
        {
            return AssistantChannelSendResult.IgnoredSender();
        }

        // The host's half of the trust boundary (AC-1049): the plugin says these are images, `InboundImage` decides.
        // Before the send, so the decoding runs on the caller's thread rather than the host's.
        var (accepted, refusal) = InboundImage.NormalizeAll(images);

        // The text fallback (AC-1553): while a question waits, a reply that names its choices is that answer.
        string? waiting;
        AssistantChannelQuestion? question;
        lock (_relayedRows)
        {
            waiting = _waiting;
            question = waiting is null ? null : _questions.GetValueOrDefault(waiting)?.FirstOrDefault();
        }

        if (waiting is not null && question?.ReadReply(text) is { } chosen && _Take(waiting) is not null)
        {
            text = StructuredQuestionJson.AnswerText(question.Question, string.Join(", ", chosen));
        }
        else if (waiting is null && _messages?.Waiting(_channel.Id) is { } sent && sent.Question.ReadReply(text) is { } picked)
        {
            // AC-1544: no question of the assistant's waits, so the reply answers the newest a plugin sent here.
            return await _messages.AnswerAsync(_channel.Id, senderUserId, sent.CorrelationId, picked).ConfigureAwait(false)
                ?? AssistantChannelSendResult.Refused("No question waits under this id.");
        }

        // A message that answered nothing moves the conversation past every plugin question waiting here (AC-1544).
        _messages?.StopTakingText(_channel.Id);
        if (await _DeliverAsync(text, accepted, cancellationToken).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        return refusal is null
            ? AssistantChannelSendResult.Sent()
            : AssistantChannelSendResult.SentWithoutImages(refusal);
    }

    public async Task<AssistantChannelSendResult> AnswerAsync(
        string senderUserId,
        string correlationId,
        IReadOnlyList<string> chosen,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return AssistantChannelSendResult.Refused("This channel is closed.");
        }

        if (!_IsAllowed(senderUserId))
        {
            return AssistantChannelSendResult.IgnoredSender();
        }

        var picks = chosen.Select(pick => pick.Trim()).Where(pick => pick.Length > 0).Distinct().ToList();
        AssistantChannelQuestion? question;
        lock (_relayedRows)
        {
            question = _questions.GetValueOrDefault(correlationId)?.FirstOrDefault();
        }

        // AC-1544: not one of the assistant's, so perhaps a question another plugin sent through this channel.
        if (question is null)
        {
            return (_messages is null ? null : await _messages.AnswerAsync(_channel.Id, senderUserId, correlationId, picks).ConfigureAwait(false))
                ?? AssistantChannelSendResult.Refused("No question waits under this id.");
        }

        if (!OperatorMessageRegistry.Takes(question, picks))
        {
            return AssistantChannelSendResult.Refused("That is not an answer this question takes.");
        }

        if (_Take(correlationId) is null)
        {
            return AssistantChannelSendResult.Refused("No question waits under this id.");
        }

        var answer = StructuredQuestionJson.AnswerText(question.Question, string.Join(", ", picks));
        return await _DeliverAsync(answer, [], cancellationToken).ConfigureAwait(false) ?? AssistantChannelSendResult.Sent();
    }

    private bool _IsAllowed(string senderUserId)
    {
        if (_channel.Access.IsAllowed(senderUserId))
        {
            return true;
        }

        // AC-1074: Information, not Debug — `FileLoggerProvider` drops everything below it, so the Debug this
        // used to be could never reach the log an operator debugging "nothing comes in" actually reads.
        _logger.LogInformation(
            "Channel {ChannelId} ({ChannelName}) ignored a message from {SenderUserId}: not on the access list.",
            _channel.Id,
            _channel.Name,
            senderUserId);
        return false;
    }

    // A question is answered once: the first answer takes it, so a second click or a late reply cannot answer again.
    private IReadOnlyList<AssistantChannelQuestion>? _Take(string correlationId)
    {
        lock (_relayedRows)
        {
            if (!_questions.Remove(correlationId, out var question))
            {
                return null;
            }

            if (_waiting == correlationId)
            {
                _waiting = null;
            }

            return question;
        }
    }

    // Null when the message reached the assistant, else the refusal to hand back.
    private async Task<AssistantChannelSendResult?> _DeliverAsync(string text, IReadOnlyList<byte[]> images, CancellationToken cancellationToken)
    {
        try
        {
            await _host.SendAsync(text, images, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return AssistantChannelSendResult.Refused(exception.Message);
        }
    }


    public void RespondToConsent(Guid promptId, ConsentOutcome outcome, bool remember = false)
    {
        lock (_relayedPrompts)
        {
            if (!_relayedPrompts.Contains(promptId))
            {
                return;
            }
        }

        _consent.Respond(promptId, outcome, remember);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_channel.Messages is { } sending)
        {
            _messages?.Close(_channel.Id, sending);
        }

        _host.PropertyChanged -= _OnHostPropertyChanged;
        _consent.PromptOpened -= _OnPromptOpened;
        _consent.PromptClosed -= _OnPromptClosed;
        _WatchSession(null);
    }

    // ── transcript ─────────────────────────────────────────────────────────────────────────────────────────────

    private void _OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IAssistantSessionHost.Session) && !_disposed)
        {
            _WatchSession(_host.Session);
        }
    }

    private void _WatchSession(IAssistantSession? next)
    {
        lock (_relayedRows)
        {
            if (ReferenceEquals(_observed, next))
            {
                return;
            }

            if (_observed is not null)
            {
                _observed.RowUpserted -= _OnRowUpserted;
                _observed.StateChanged -= _OnStateChanged;
            }

            _relayedRows.Clear();
            _questions.Clear();
            _waiting = null;
            _observed = next;

            if (next is null)
            {
                return;
            }

            next.RowUpserted += _OnRowUpserted;
            next.StateChanged += _OnStateChanged;
        }

        // Taken as already said, never replayed. Read outside the lock, since the pane answers on its UI thread; a row
        // that arrived in between was relayed already and keeps its identity.
        var baseline = next.Rows;
        var busyNow = next.IsBusy;
        lock (_relayedRows)
        {
            if (!ReferenceEquals(_observed, next))
            {
                return;
            }

            foreach (var row in baseline)
            {
                _relayedRows.TryAdd(row.Id, (Guid.NewGuid(), _Content(row)));
                if (row.Kind == "Question" && row.IsPendingBrokerAnswer && StructuredQuestionJson.Parse(row.InputJson) is { Count: > 0 } pending)
                {
                    var correlation = _relayedRows[row.Id].Id.ToString("n");
                    _questions[correlation] = pending;
                    _waiting = correlation;
                }
            }

            _wasBusy = busyNow;
        }
    }

    // Busy to idle is a turn ending; a successful one adds no row, so this is the only signal a channel gets (AC-2084).
    private void _OnStateChanged(object? sender, bool contextFillMoved)
    {
        var session = _observed;
        if (_disposed || session is null)
        {
            return;
        }

        var busy = session.IsBusy;
        bool ended;
        lock (_relayedRows)
        {
            ended = _wasBusy && !busy;
            _wasBusy = busy;
        }

        if (ended)
        {
            TurnEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    // A row's first version is a row arriving; any later one is the same row changing — including a row that was
    // already there when the channel joined, which is watched from here on but never replayed.
    private void _OnRowUpserted(TranscriptRowUpsert upsert)
    {
        // AC-1518: a sub-agent's own row stays inside its anchor here, as it did when it came inside the anchor's upsert.
        if (_disposed || upsert.Row.ParentRowId is not null)
        {
            return;
        }

        var row = upsert.Row;

        // A Question row is only the assistant's own card (AddQuestion); a native AskUserQuestion is a permission row.
        var questions = row.Kind == "Question" ? StructuredQuestionJson.Parse(row.InputJson) : [];
        Guid id;
        IReadOnlyList<AssistantChannelQuestion>? asked;
        lock (_relayedRows)
        {
            // The operator's own next message answers or moves past every question asked before it, here or in the app.
            if (row.Kind == "UserText")
            {
                _questions.Clear();
                _waiting = null;
            }

            if (_relayedRows.TryGetValue(row.Id, out var relayed))
            {
                if (relayed.Content == _Content(row))
                {
                    return;
                }

                id = relayed.Id;
            }
            else
            {
                id = Guid.NewGuid();
            }

            _relayedRows[row.Id] = (id, _Content(row));

            // A card keeps all its questions so structured channels can answer each one independently.
            if (questions.Count > 0 && (row.IsPendingBrokerAnswer || (relayed.Id == Guid.Empty && upsert.Version <= 1)))
            {
                _questions[id.ToString("n")] = questions;
                _waiting = id.ToString("n");
            }
            else if (row.Kind == "Question" && !row.IsPendingBrokerAnswer && upsert.Version > 1)
            {
                _questions.Remove(id.ToString("n"));
                if (_waiting == id.ToString("n"))
                {
                    _waiting = null;
                }
            }

            asked = _questions.GetValueOrDefault(id.ToString("n"));
        }

        RowChanged?.Invoke(this, _ToChannelRow(row, id, _ChannelText(row), upsert.Version > 1, asked));
    }

    private static AssistantChannelRow _ToChannelRow(TranscriptSnapshotEntry row, Guid id, string text, bool isUpdate, IReadOnlyList<AssistantChannelQuestion>? asked) => new()
    {
        Id = id,
        Kind = _Kind(row.Kind),
        Text = text,
        Attachments = row.ReplyAttachments?.Select(file => new AssistantChannelAttachment(file.Name, file.Content, file.IsImage)).ToArray() ?? [],
        Timestamp = row.Timestamp,
        ToolName = row.ToolName,
        ResultText = row.ResultText,
        IsUpdate = isUpdate,
        Questions = asked ?? [],
        CorrelationId = asked is null ? null : id.ToString("n"),
    };

    private static int _Content(TranscriptSnapshotEntry row) =>
        HashCode.Combine(row.Text, row.ResultText, row.ReplyAttachments, row.InputJson, row.IsPendingBrokerAnswer);

    private string _ChannelText(TranscriptSnapshotEntry row) =>
        row.StartsInsideTable && _observed is not null ? _TableText(row, _observed.Rows.TakeWhile(previous => previous.Id != row.Id)) : row.Text;

    // A table cut across rows gets the opening row's header back, so a row read alone still makes sense.
    private static string _TableText(TranscriptSnapshotEntry row, IEnumerable<TranscriptSnapshotEntry> preceding)
    {
        var opening = preceding.LastOrDefault(previous => previous.Kind == "AssistantText" && !previous.StartsInsideTable && previous.EndsInsideTable);
        return opening is null ? row.Text : string.Join('\n', opening.Text.Split('\n').Take(2)) + "\n" + row.Text;
    }

    // AC-2238: a plugin's plain bound on a page, so a careless limit cannot turn one call into the whole conversation.
    private const int _MaxPage = 200;

    // The session's own rows are the transcript, repainted from its log on a resume, so this reads after a restart too
    // and touches no file. One snapshot per call; the cost is that snapshot, not a scan of the log per page.
    public Task<AssistantChannelHistoryPage> ReadRowsAsync(int limit, string? before, CancellationToken cancellationToken = default)
    {
        if (_disposed || limit < 1 || _observed is not { } session)
        {
            return Task.FromResult(AssistantChannelHistoryPage.Empty);
        }

        var rows = session.Rows;
        var end = rows.Count;
        lock (_relayedRows)
        {
            if (before is not null)
            {
                end = Guid.TryParse(before, out var cursor) ? _IndexOf(rows, cursor) : -1;
                if (end < 0)
                {
                    return Task.FromResult(AssistantChannelHistoryPage.Empty);
                }
            }

            var start = Math.Max(0, end - Math.Min(limit, _MaxPage));
            var page = new List<AssistantChannelRow>(end - start);
            for (var index = end - 1; index >= start; index--)
            {
                var row = rows[index];
                if (!_relayedRows.TryGetValue(row.Id, out var relayed))
                {
                    relayed = (Guid.NewGuid(), _Content(row));
                    _relayedRows[row.Id] = relayed;
                }

                var text = row.StartsInsideTable ? _TableText(row, rows.Take(index)) : row.Text;
                page.Add(_ToChannelRow(row, relayed.Id, text, false, null));
            }

            return Task.FromResult(new AssistantChannelHistoryPage(page, start > 0 ? page[^1].Id.ToString("n") : null));
        }
    }

    // Newest first: a page back usually starts near the end. Under the _relayedRows lock.
    private int _IndexOf(IReadOnlyList<TranscriptSnapshotEntry> rows, Guid id)
    {
        for (var index = rows.Count - 1; index >= 0; index--)
        {
            if (_relayedRows.TryGetValue(rows[index].Id, out var relayed) && relayed.Id == id)
            {
                return index;
            }
        }

        return -1;
    }

    // The row kinds as a snapshot spells them — `TranscriptEntryKind`'s names.
    private static AssistantChannelRowKind _Kind(string kind) => kind switch
    {
        "UserText" => AssistantChannelRowKind.UserText,
        "ToolUse" => AssistantChannelRowKind.ToolUse,
        "ToolResult" => AssistantChannelRowKind.ToolResult,
        "Thinking" => AssistantChannelRowKind.Thinking,
        "Question" => AssistantChannelRowKind.Question,
        "TurnCompleted" => AssistantChannelRowKind.TurnCompleted,
        "Error" => AssistantChannelRowKind.Error,
        "Divider" => AssistantChannelRowKind.Divider,
        _ => AssistantChannelRowKind.AssistantText,
    };

    // ── consent ────────────────────────────────────────────────────────────────────────────────────────────────

    private void _OnPromptOpened(object? sender, ConsentPrompt prompt)
    {
        // The assistant's own, or a workflow's that no session owns ("Ask me first", AC-1360), whose Action carries
        // the literal step. Everything else stays off: the other end would approve work it was never shown.
        if (_disposed || !_IsRelayable(prompt.Request.Source))
        {
            return;
        }

        lock (_relayedPrompts)
        {
            _relayedPrompts.Add(prompt.Id);
        }

        ConsentPromptOpened?.Invoke(this, new AssistantChannelConsentPrompt(prompt.Id, prompt.Request, prompt.CanRemember));
    }

    // Least privilege: only the Workflows plugin opts in for now. PluginId is stamped by the host, so another plugin
    // cannot pass its prompt off as a workflow's.
    private const string _WorkflowsPluginId = "workflows";

    private static bool _IsRelayable(ConsentSource source) =>
        source.PaneId == AssistantIdentity.PaneId || (source.PaneId is null && source.PluginId == _WorkflowsPluginId);

    private void _OnPromptClosed(object? sender, Guid promptId)
    {
        lock (_relayedPrompts)
        {
            if (!_relayedPrompts.Remove(promptId))
            {
                return;
            }
        }

        if (!_disposed)
        {
            ConsentPromptClosed?.Invoke(this, promptId);
        }
    }
}
