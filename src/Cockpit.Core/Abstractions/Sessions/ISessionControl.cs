using System.Collections.Specialized;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// What an SDK session pane steers its session through (AC-1449): the start and stop, the live switches, the turn gate
/// and its queue, the recorded transcript and the clocks. Members run and signals arrive on the thread named through
/// <see cref="PumpOn"/>; only the clock signals may come from elsewhere. Kept apart from <see cref="ISessionHandle"/>,
/// which every pane kind carries, because a TTY pane or a plain terminal has none of this to offer.
/// </summary>
public interface ISessionControl : IAsyncDisposable
{
    /// <summary>
    /// Whether this control can start a session at all; false for one built without a backend (the design-time graph).
    /// </summary>
    bool CanLaunch { get; }

    /// <summary>
    /// True from the moment a start creates the session until it is stopped, whether or not it came up.
    /// </summary>
    bool IsAttached { get; }

    /// <summary>
    /// True while the session is up and can carry a turn.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Starts the session; null when this control cannot launch, which still arms the login poll and the pre-approvals.
    /// A start that throws leaves the session attached.
    /// </summary>
    Task<SessionLaunched?> StartAsync(SessionStart start);

    /// <summary>
    /// Stops reporting the session's events, ahead of <see cref="StopAsync"/>, so the consumer can flush what it has.
    /// </summary>
    void StopListening();

    /// <summary>
    /// Stops the session. <see cref="IsAttached"/> reads false before the first await.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Names the thread the session's events are folded and reported on: <paramref name="post"/> runs an action there.
    /// With <paramref name="postAfterWindow"/>, events go there in batches a window apart, otherwise one post per event.
    /// </summary>
    void PumpOn(Action<Action> post, Action<Action>? postAfterWindow = null);

    /// <summary>
    /// Whether events wait in a batch the consumer's thread has not drained yet.
    /// </summary>
    bool HasPumpedWork { get; }

    /// <summary>
    /// Folds what the batch still holds, on the consumer's thread, ahead of a stop.
    /// </summary>
    void FlushPumped();

    /// <summary>
    /// Breaks off the turn in flight; the turn that ends because of it is not reported as a failure.
    /// </summary>
    Task InterruptAsync();

    /// <summary>
    /// Switches the running session's permission mode, and records it once the switch took.
    /// </summary>
    Task SetPermissionModeAsync(string mode);

    /// <summary>
    /// Switches the running session's model.
    /// </summary>
    Task SetModelAsync(string? model);

    /// <summary>
    /// Switches the running session's thinking budget.
    /// </summary>
    Task SetMaxThinkingTokensAsync(int maxThinkingTokens);

    /// <summary>
    /// Switches one of the provider's own live options, as <see cref="SessionLaunched.LiveOptions"/> declared it.
    /// </summary>
    Task SetLiveOptionAsync(string key, string value);

    /// <summary>
    /// Turns allowing every tool call without asking on or off, for a provider that asks per call.
    /// </summary>
    Task SetAutoApproveToolsAsync(bool autoApprove);

    /// <summary>
    /// Asks the provider to summarise the conversation and carry on in it (AC-664).
    /// </summary>
    Task CompactContextAsync();

    /// <summary>
    /// Answers the permission prompt for <paramref name="toolUseId"/>, with the operator's answers when it asked any (AC-715).
    /// </summary>
    Task RespondToPermissionAsync(string toolUseId, bool allow, string? answersJson);

    /// <summary>
    /// Allows the tool call for <paramref name="toolUseId"/> and every later one like it within <paramref name="scope"/>.
    /// </summary>
    Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string inputJson, PermissionRuleScope scope);

    /// <summary>
    /// The operator answered the last open prompt, so the session stops flagging itself (AC-1324).
    /// </summary>
    void ClearNeedsAttention();

    /// <summary>
    /// The tools the last start allowed without asking (AC-215).
    /// </summary>
    IReadOnlyCollection<string> PreApprovedTools { get; }

    /// <summary>
    /// Whether the last start allowed every tool without asking (AC-215).
    /// </summary>
    bool PreApprovesAllTools { get; }

    /// <summary>
    /// True while a turn is in flight.
    /// </summary>
    bool IsBusy { get; set; }

    /// <summary>
    /// Why no new turn may start (AC-1321), else null. Lifting it while no turn runs sends what was queued behind it.
    /// </summary>
    string? TurnsHeldBecause { get; set; }

    /// <summary>
    /// The turn in flight is the session's last (T10): when it completes, what was queued behind it stays queued.
    /// </summary>
    bool EndsAfterThisTurn { get; set; }

    /// <summary>
    /// Sends the whole queue as one follow-up turn instead of one turn per queued prompt (AC-145).
    /// </summary>
    bool CombineQueued { get; set; }

    /// <summary>
    /// What the consumer knows of the provider before the session says so itself (AC-1319).
    /// </summary>
    SessionCapabilities? Capabilities { get; set; }

    /// <summary>
    /// The text a prompt leaves with, read when it leaves rather than when it was queued (AC-935).
    /// </summary>
    Func<QueuedPrompt, string> OutgoingText { get; set; }

    /// <summary>
    /// The prompts waiting behind the turn in flight, in the order they will go.
    /// </summary>
    IReadOnlyList<QueuedPrompt> Queue { get; }

    /// <summary>
    /// Raised for every change to <see cref="Queue"/>, describing it as a collection change.
    /// </summary>
    event NotifyCollectionChangedEventHandler? QueueChanged;

    /// <summary>
    /// Puts <paramref name="prompt"/> at the end of the queue without sending anything.
    /// </summary>
    void Enqueue(QueuedPrompt prompt);

    /// <summary>
    /// Takes <paramref name="prompt"/> out of the queue; false when it was no longer there.
    /// </summary>
    bool Withdraw(QueuedPrompt prompt);

    /// <summary>
    /// Empties the queue without sending anything.
    /// </summary>
    void ClearQueue();

    /// <summary>
    /// Queues <paramref name="prompt"/> behind a turn in flight, unless the session takes input mid-turn (AC-739);
    /// otherwise sends it now. A send that fails is reported through <see cref="TurnFailedToStart"/>.
    /// </summary>
    Task SubmitAsync(QueuedPrompt prompt, bool takesMidTurnInput = false);

    /// <summary>
    /// Sends <paramref name="prompt"/> now, where nothing can await it; disposal waits for it instead.
    /// </summary>
    void DispatchInBackground(QueuedPrompt prompt);

    /// <summary>
    /// Sends a turn the caller already marked busy and whose failure it handles itself (AC-410).
    /// </summary>
    Task SendPromptAsync(string prompt);

    /// <summary>
    /// Starts the live state over, for a conversation that starts over in the same pane (AC-564).
    /// </summary>
    void ResetLiveState();

    /// <summary>
    /// The session's usage reading, else what a session on the same credential last published (AC-775).
    /// </summary>
    SessionStatusFeed? ReadUsageStatus(ProviderConfig? config);

    /// <summary>
    /// The rows as they stand, for a consumer that missed some of their upserts.
    /// </summary>
    IReadOnlyList<TranscriptSnapshotEntry> Rows { get; }

    /// <summary>
    /// Whether this session's rows are recorded at all.
    /// </summary>
    bool RecordsTranscript { get; }

    /// <summary>
    /// Records a row the consumer formed or changed itself, so the rows and the record stay one transcript.
    /// </summary>
    void RecordRow(TranscriptSnapshotEntry row);

    /// <summary>
    /// Ends the streaming of the reply in progress, for a cleared context that starts a new conversation (AC-564).
    /// </summary>
    void ResetTranscriptStreaming();

    /// <summary>
    /// The rows this pane recorded; null when the record is there but could not be read (AC-1090).
    /// </summary>
    Task<IReadOnlyList<TranscriptSnapshotEntry>?> LoadRecordedTranscriptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The rows the consumer repainted, held so later changes version them; never published or recorded again.
    /// </summary>
    void SeedTranscript(IReadOnlyList<TranscriptSnapshotEntry> rows);

    /// <summary>
    /// Puts this pane's record aside, so a new conversation starts a new one (AC-947).
    /// </summary>
    Task ArchiveRecordedTranscriptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// False once <see cref="StopPolling"/> ran; a clock signal posted just before still arrives, and asks this first.
    /// </summary>
    bool IsPolling { get; }

    /// <summary>
    /// Stops the login and usage clocks, for a pane that is closing.
    /// </summary>
    void StopPolling();

    /// <summary>
    /// Raises <see cref="SignOfLifeDue"/> once, after <paramref name="delay"/> (AC-598), in place of any earlier arm.
    /// </summary>
    void RestartSignOfLife(TimeSpan delay);

    /// <summary>
    /// Disarms the sign-of-life clock.
    /// </summary>
    void StopSignOfLife();

    /// <summary>
    /// Whether <paramref name="arm"/>, as <see cref="SignOfLifeDue"/> carried it, is still the current one.
    /// </summary>
    bool IsCurrentSignOfLife(int arm);

    /// <summary>
    /// Raised when a start creates the session, before it is up: the moment its working life starts (AC-251).
    /// </summary>
    event Action<DateTimeOffset>? Started;

    /// <summary>
    /// Raised whenever <see cref="IsBusy"/> changes.
    /// </summary>
    event Action? BusyChanged;

    /// <summary>
    /// Raised before a turn's send, so whatever the consumer echoes lands ahead of anything the turn produces.
    /// </summary>
    event Action<QueuedPrompt>? TurnStarting;

    /// <summary>
    /// Raised when a turn never left; any mail it would have carried is already given back.
    /// </summary>
    event Action<QueuedPrompt, Exception>? TurnFailedToStart;

    /// <summary>
    /// Raised with the agent mail a turn carried into the context (AC-394).
    /// </summary>
    event Action<AgentInboxTurnNotice>? MailDelivered;

    /// <summary>
    /// Raised by the login clock with whether the profile is still logged in (AC-713); may come from any thread.
    /// </summary>
    event Action<bool>? LoginChecked;

    /// <summary>
    /// Raised by the usage clock when an idle session should re-read its usage (AC-761); may come from any thread.
    /// </summary>
    event Action? UsageCatchUpDue;

    /// <summary>
    /// Raised by the sign-of-life clock with the arm it belongs to (AC-598); may come from any thread.
    /// </summary>
    event Action<int>? SignOfLifeDue;

    /// <summary>
    /// Raised with what folding one of the session's events left, after that event's other signals (AC-1438).
    /// </summary>
    event Action<TranscriptFold>? Folded;

    /// <summary>
    /// Raised for every row a fold or a recorded row changed, in the order they changed.
    /// </summary>
    event Action<TranscriptRowUpsert>? RowUpserted;

    /// <summary>
    /// Raised with the new live state whenever it changes (AC-1437).
    /// </summary>
    event Action<SessionLiveState>? LiveStateChanged;

    /// <summary>
    /// Raised when a turn ends, completed or broken off by a session error (AC-1437).
    /// </summary>
    event Action<SessionTurnEnd>? TurnEnded;

    /// <summary>
    /// Raised for every tool call or tool result, a sub-agent's included (AC-215).
    /// </summary>
    event Action? ToolProgressed;

    /// <summary>
    /// Raised when the provider gives its own verdict on one background task (AC-1057).
    /// </summary>
    event Action<SessionBackgroundTaskNotice>? BackgroundTaskNotified;

    /// <summary>
    /// Raised for each chunk of text the session produces for the operator (AC-1415).
    /// </summary>
    event Action<string>? OutputTextProduced;

    /// <summary>
    /// Raised when the session's agent completes a top-level tool call (AC-1415).
    /// </summary>
    event Action<SessionToolCall>? ToolActivityProduced;
}

/// <summary>
/// Makes the control for one pane (AC-1449).
/// </summary>
public interface ISessionControlFactory
{
    /// <summary>
    /// A control for the pane whose id <paramref name="paneId"/> reads; read on every turn, since a pane adopts its id
    /// after it is built.
    /// </summary>
    ISessionControl Create(Func<string> paneId);
}

// AC-1449: what a start settled, read once rather than asked of a running session later.
public sealed record SessionLaunched(
    bool IsRunning,
    int? ProcessId,
    SessionCapabilities? Capabilities,
    IReadOnlyList<SessionLiveOption> LiveOptions);
