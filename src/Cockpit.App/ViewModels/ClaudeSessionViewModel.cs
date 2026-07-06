using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Claude;
using Cockpit.Core.Claude.Permissions;
using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

/// <summary>
/// F-C1 cockpit: a single Claude Code session rendered as a streaming transcript with a
/// chat-style input box and read-only-so-far allow/deny affordances for tool use.
/// </summary>
/// <remarks>
/// Visual layout has not been verified against a running Avalonia window in this sandbox
/// (no display available here) — treat the XAML as unverified until Raymond runs it.
/// </remarks>
public partial class ClaudeSessionViewModel : SessionPanelViewModel, ITransientService
{
    private readonly IClaudeSession? _session;
    private readonly IClaudeProfileStore? _profileStore;
    private readonly IClaudeProfileLoginChecker? _loginChecker;
    private CancellationTokenSource? _lifetimeCancellation;
    private Task? _eventLoopTask;
    private TranscriptEntryViewModel? _currentAssistantEntry;
    private TranscriptEntryViewModel? _currentThinkingEntry;
    private string? _lastUsedProfileLabel;

    public ObservableCollection<TranscriptEntryViewModel> Transcript { get; } = [];

    /// <summary>Images pasted into the input, sent with the next message and cleared afterwards.</summary>
    public ObservableCollection<ImageAttachmentViewModel> PendingAttachments { get; } = [];

    /// <summary>True while at least one image is queued, so the chip strip can hide when empty.</summary>
    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    /// <summary>Populated only while <see cref="ProfileSelectionKind.RequiresChoice"/> is pending the user's pick.</summary>
    public ObservableCollection<ClaudeProfile> ProfileChoices { get; } = [];

    /// <summary>
    /// Permission modes offered in the running panel: the three live-switchable modes
    /// (<see cref="SessionOptionCatalog.LivePermissionModes"/>), or — once a session was launched in
    /// bypass — a single locked "Bypass permissions" entry, since the CLI cannot switch a running
    /// session into or out of bypass. The dialog offers the full four via the catalog.
    /// </summary>
    public IReadOnlyList<PermissionModeOption> PermissionModes =>
        IsPermissionModeLocked ? [SelectedPermissionMode] : SessionOptionCatalog.LivePermissionModes;

    [ObservableProperty]
    private PermissionModeOption _selectedPermissionMode = SessionOptionCatalog.DefaultPermissionMode;

    /// <summary>
    /// True once the session was launched in bypass: bypass is terminal (launch-only), so the panel
    /// dropdown is disabled rather than offering a switch the CLI would reject — no dead control (#15).
    /// </summary>
    [ObservableProperty]
    private bool _isPermissionModeLocked;

    partial void OnIsPermissionModeLockedChanged(bool value) => OnPropertyChanged(nameof(PermissionModes));

    /// <summary>Models offered per session; the selected one becomes <c>--model</c> at launch and can be switched live.</summary>
    public IReadOnlyList<ModelOption> Models => SessionOptionCatalog.Models;

    [ObservableProperty]
    private ModelOption _selectedModel = SessionOptionCatalog.DefaultModel;

    /// <summary>Thinking-effort levels offered per session; drives the thinking-budget control.</summary>
    public IReadOnlyList<EffortOption> Efforts => SessionOptionCatalog.Efforts;

    [ObservableProperty]
    private EffortOption _selectedEffort = SessionOptionCatalog.DefaultEffort;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _status = "Not started.";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>True while a pending permission decision or CLI <c>needs_action</c> signal is outstanding, driving <see cref="SessionStatus.NeedsAttention"/>.</summary>
    private bool _needsAttention;

    /// <summary>True while <see cref="ProfileChoices"/> should be shown for the user to pick from.</summary>
    [ObservableProperty]
    private bool _isChoosingProfile;

    [ObservableProperty]
    private ClaudeProfile? _selectedProfile;

    // Parameterless constructor kept for the Avalonia previewer design-time context. Seeds a
    // few sample transcript rows so the previewer/Screenshotter render the styled components
    // (thinking, tool-use, collapsed tool-result, pending permission) — does not touch the real
    // DI-backed session.
    public ClaudeSessionViewModel()
    {
        Status = "Connected (12 tools, cwd=D:/Projects/dotnet/Cockpit).";
        ActiveProfileLabel = "raymond@work";

        Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "> los de bug op in ClaudeSessionView"));

        var thinking = new TranscriptEntryViewModel(TranscriptEntryKind.Thinking,
            "De gebruiker vraagt om de layout-bug te fixen. Ik bekijk eerst de XAML-structuur...")
        {
            IsExpanded = false,
        };
        Transcript.Add(thinking);

        Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText,
            "Ik heb de oorzaak gevonden: de DockPanel-volgorde zorgde ervoor dat de ScrollViewer werd platgedrukt. Ik verplaats de top- en bottom-docks vóór de laatste child."));

        Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse,
            "Tool: Edit({\"file_path\":\"ClaudeSessionView.axaml\",\"old_string\":\"...\"})")
        {
            ToolUseId = "sample-tool-1",
            ToolName = "Edit",
            InputJson = "{\"file_path\":\"ClaudeSessionView.axaml\",\"old_string\":\"...\"}",
        });

        Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.ToolResult,
            "Tool result: The file D:\\Projects\\dotnet\\Cockpit\\src\\Cockpit.App\\Views\\ClaudeSessionView.axaml has been updated successfully.")
        {
            IsExpanded = false,
        });

        Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse,
            "Tool: Bash({\"command\":\"dotnet build\"})")
        {
            ToolUseId = "sample-tool-2",
            ToolName = "Bash",
            InputJson = "{\"command\":\"dotnet build\"}",
            IsPendingPermission = true,
        });

        _TrackPendingAttachments();

        // A sample pasted-image chip so the previewer/Screenshotter render the attachment strip.
        // Decoding the Bitmap needs Avalonia's imaging platform: the previewer and the headless
        // Screenshotter both initialize an Application, the unit-test host does not — so guard on
        // that rather than decode (and crash) when no platform is present.
        if (Application.Current is not null)
        {
            AddPastedImage(Convert.FromBase64String(_SampleChipPng));
        }
    }

    // 64x64 solid-blue PNG, design-time only: seeds one attachment chip for the Avalonia previewer.
    private const string _SampleChipPng =
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJ" +
        "cEhZcwAADsMAAA7DAcdvqGQAAACJSURBVHhe5cgxAQAADMOg+je9CYgEDh623dkSmoQmoUloEpqEJqFJaBKahCah" +
        "SWgSmoQmoUloEpqEJqFJaBKahCahSWgSmoQmoUloEpqEJqFJaBKahCahSWgSmoQmoUloEpqEJqFJaBKahCahSWgS" +
        "moQmoUloEpqEJqFJaBKahCahSWgSmoQmYXlhqOHSNEsP9wAAAABJRU5ErkJggg==";

    public ClaudeSessionViewModel(IClaudeSession session, IClaudeProfileStore profileStore, IClaudeProfileLoginChecker loginChecker)
    {
        _session = session;
        _profileStore = profileStore;
        _loginChecker = loginChecker;
        _TrackPendingAttachments();
    }

    private void _TrackPendingAttachments()
    {
        PendingAttachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPendingAttachments));
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (_session is null || _profileStore is null || _loginChecker is null || _eventLoopTask is not null)
        {
            return;
        }

        Status = "Checking profiles...";

        var profiles = await _profileStore.LoadAsync();
        var statuses = profiles.Select(p => new ClaudeProfileStatus(p, _loginChecker.IsLoggedIn(p))).ToList();
        var outcome = ProfileSelector.Select(statuses, _lastUsedProfileLabel);

        switch (outcome.Kind)
        {
            case ProfileSelectionKind.LoginRequired:
                Status = "No logged-in Claude profile found. Run 'claude /login' in a terminal, then try again.";
                return;

            case ProfileSelectionKind.RequiresChoice:
                ProfileChoices.Clear();
                foreach (var candidate in outcome.Candidates)
                {
                    ProfileChoices.Add(candidate);
                }

                SelectedProfile = outcome.Candidates[0];
                IsChoosingProfile = true;
                Status = "Choose a profile to start the session.";
                return;

            case ProfileSelectionKind.UseSilently:
                await StartWithProfileAsync(outcome.SingleProfile);
                return;
        }
    }

    [RelayCommand]
    private async Task ConfirmProfileChoiceAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        IsChoosingProfile = false;
        await StartWithProfileAsync(SelectedProfile);
    }

    private async Task StartWithProfileAsync(ClaudeProfile? profile)
    {
        if (_session is null)
        {
            return;
        }

        Status = "Starting...";
        _lifetimeCancellation = new CancellationTokenSource();

        try
        {
            await _session.StartAsync(profile, SelectedPermissionMode.Value, SelectedModel.Value, _lifetimeCancellation.Token);
            _eventLoopTask = ConsumeEventsAsync(_lifetimeCancellation.Token);
            _lastUsedProfileLabel = profile?.Label;
            ActiveProfileLabel = profile?.Label;
            Status = profile is null ? "Session started." : $"Session started ({profile.Label}).";

            // Thinking budget has no launch flag — the control request is the only path — so apply
            // the selected effort once the session is live, otherwise it runs at the CLI default
            // until the operator first touches the dropdown.
            await _SetMaxThinkingTokensSafeAsync(SelectedEffort.MaxThinkingTokens);
        }
        catch (Exception ex)
        {
            Status = $"Failed to start: {ex.Message}";
        }
    }

    /// <summary>Live-switches the running session's permission mode. No-op before the session has started.</summary>
    partial void OnSelectedPermissionModeChanged(PermissionModeOption value)
    {
        if (_session is null || _eventLoopTask is null)
        {
            return;
        }

        _ = _SetPermissionModeSafeAsync(value.Value);
    }

    /// <summary>Live-switches the running session's model. No-op before the session has started.</summary>
    partial void OnSelectedModelChanged(ModelOption value)
    {
        if (_session is null || _eventLoopTask is null)
        {
            return;
        }

        _ = _SetModelSafeAsync(value.Value);
    }

    /// <summary>Live-switches the running session's thinking budget. No-op before the session has started.</summary>
    partial void OnSelectedEffortChanged(EffortOption value)
    {
        if (_session is null || _eventLoopTask is null)
        {
            return;
        }

        _ = _SetMaxThinkingTokensSafeAsync(value.MaxThinkingTokens);
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (_session is null || _eventLoopTask is null)
        {
            return;
        }

        try
        {
            await _session.InterruptAsync();
            Status = "Interrupted.";
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Interrupt failed: {ex.Message}"));
        }
    }

    private async Task _SetPermissionModeSafeAsync(string mode)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            await _session.SetPermissionModeAsync(mode);
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Permission-mode switch failed: {ex.Message}"));
        }
    }

    private async Task _SetModelSafeAsync(string model)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            await _session.SetModelAsync(model);
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Model switch failed: {ex.Message}"));
        }
    }

    private async Task _SetMaxThinkingTokensSafeAsync(int maxThinkingTokens)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            await _session.SetMaxThinkingTokensAsync(maxThinkingTokens);
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Effort switch failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Queues a pasted image as a pending attachment for the next message. Called from the view's
    /// CTRL+V handler, which owns the Avalonia clipboard read; the view model only sees PNG bytes so
    /// it stays free of UI-toolkit types and unit-testable.
    /// </summary>
    public void AddPastedImage(byte[] pngBytes)
    {
        PendingAttachments.Add(new ImageAttachmentViewModel(pngBytes, a => PendingAttachments.Remove(a)));
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (_session is null || (string.IsNullOrWhiteSpace(InputText) && PendingAttachments.Count == 0))
        {
            return;
        }

        var text = InputText;
        var images = PendingAttachments
            .Select(a => Core.Claude.ImageAttachment.FromBytes(a.PngBytes, a.MediaType))
            .ToList();

        InputText = string.Empty;
        PendingAttachments.Clear();

        var echo = images.Count == 0
            ? $"> {text}"
            : $"> {text} [+{images.Count} image{(images.Count == 1 ? "" : "s")}]";
        Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, echo));
        _currentAssistantEntry = null;
        IsBusy = true;
        _needsAttention = false;
        _RecomputeStatus();

        try
        {
            await _session.SendUserMessageAsync(text, images);
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Send failed: {ex.Message}"));
            IsBusy = false;
            _RecomputeStatus();
        }
    }

    [RelayCommand]
    private async Task AllowToolAsync(TranscriptEntryViewModel entry)
    {
        await RespondToPermissionAsync(entry, allow: true);
    }

    [RelayCommand]
    private async Task DenyToolAsync(TranscriptEntryViewModel entry)
    {
        await RespondToPermissionAsync(entry, allow: false);
    }

    /// <summary>Allows the call and persists a rule matching only this exact tool + input for the session's profile.</summary>
    [RelayCommand]
    private async Task AllowAlwaysExactToolAsync(TranscriptEntryViewModel entry)
    {
        await AllowAlwaysAsync(entry, PermissionRuleScope.Exact);
    }

    /// <summary>Allows the call and persists a rule matching every future call to this tool for the session's profile.</summary>
    [RelayCommand]
    private async Task AllowAlwaysWildcardToolAsync(TranscriptEntryViewModel entry)
    {
        await AllowAlwaysAsync(entry, PermissionRuleScope.Wildcard);
    }

    private async Task RespondToPermissionAsync(TranscriptEntryViewModel entry, bool allow)
    {
        if (_session is null || entry.ToolUseId is null)
        {
            return;
        }

        entry.PermissionDecision = allow ? "Allowed" : "Denied";
        entry.IsPendingPermission = false;
        await _session.RespondToPermissionAsync(entry.ToolUseId, allow);
    }

    private async Task AllowAlwaysAsync(TranscriptEntryViewModel entry, PermissionRuleScope scope)
    {
        if (_session is null || entry.ToolUseId is null || entry.ToolName is null)
        {
            return;
        }

        entry.PermissionDecision = scope == PermissionRuleScope.Wildcard
            ? $"Always allowed ({entry.ToolName}:*)"
            : $"Always allowed (exact: {entry.ToolName})";
        entry.IsPendingPermission = false;

        await _session.AllowPermissionAlwaysAsync(entry.ToolUseId, entry.ToolName, entry.InputJson ?? "{}", scope);
    }

    private async Task ConsumeEventsAsync(CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            await foreach (var evt in _session.Events.WithCancellation(cancellationToken))
            {
                await Dispatcher.UIThread.InvokeAsync(() => Apply(evt));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    /// <summary>internal (rather than private) so <c>Cockpit.Core.Tests</c> can drive it directly, bypassing <c>Dispatcher.UIThread</c> — see <see cref="ConsumeEventsAsync"/>.</summary>
    internal void Apply(ClaudeSessionEvent evt)
    {
        switch (evt)
        {
            case SessionInitialized init:
                Status = $"Connected ({init.Tools.Count} tools, cwd={init.Cwd}).";
                break;

            case AssistantThinkingDelta thinking:
                if (_currentThinkingEntry is null)
                {
                    _currentThinkingEntry = new TranscriptEntryViewModel(TranscriptEntryKind.Thinking, string.Empty);
                    Transcript.Add(_currentThinkingEntry);
                }

                _currentThinkingEntry.AppendText(thinking.Thinking);
                break;

            case AssistantTextDelta delta:
                // A text delta means the thinking block (if any) for this turn is done.
                _RemoveCurrentThinkingEntry();
                if (_currentAssistantEntry is null)
                {
                    _currentAssistantEntry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, string.Empty);
                    Transcript.Add(_currentAssistantEntry);
                }

                _currentAssistantEntry.AppendText(delta.Text);
                break;

            case AssistantTextCompleted completed:
                _RemoveCurrentThinkingEntry();
                if (_currentAssistantEntry is not null)
                {
                    // Streaming deltas already built the text; nothing further to append.
                    _currentAssistantEntry = null;
                }
                else
                {
                    Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, completed.Text));
                }

                break;

            case ToolUseRequested toolUse:
                _RemoveCurrentThinkingEntry();
                Transcript.Add(new TranscriptEntryViewModel(
                    TranscriptEntryKind.ToolUse,
                    $"Tool: {toolUse.ToolName}({toolUse.InputJson})")
                {
                    ToolUseId = toolUse.ToolUseId,
                    ToolName = toolUse.ToolName,
                    InputJson = toolUse.InputJson,
                });
                break;

            case ToolResult toolResult:
                Transcript.Add(new TranscriptEntryViewModel(
                    TranscriptEntryKind.ToolResult,
                    toolResult.IsError ? $"Tool error: {toolResult.Content}" : $"Tool result: {toolResult.Content}"));
                break;

            case PermissionRequested permission:
                var entry = Transcript.LastOrDefault(t => t.ToolUseId == permission.ToolUseId);
                if (entry is not null)
                {
                    entry.IsPendingPermission = true;
                }

                _needsAttention = true;
                _RecomputeStatus();
                break;

            case Question question:
                Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Question, question.Text));
                break;

            case TurnCompleted turn:
                _RemoveCurrentThinkingEntry();
                Transcript.Add(new TranscriptEntryViewModel(
                    TranscriptEntryKind.TurnCompleted,
                    turn.IsError ? $"Turn failed ({turn.Subtype})" : $"Turn completed ({turn.Subtype})"));
                _currentAssistantEntry = null;
                IsBusy = false;
                _RecomputeStatus();
                break;

            case SessionError error:
                _RemoveCurrentThinkingEntry();
                Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, error.Message));
                IsBusy = false;
                _RecomputeStatus();
                break;

            case SessionStatusChanged statusChanged:
                // needs_action non-empty is the CLI telling the host the session wants attention
                // (e.g. a pending question) — same "jump out in the sidebar" signal as a pending
                // tool permission. RateLimitInfo/UnknownEvent stay out of scope for status (per-session
                // status overview and agent-tree rendering are later increments); ConsumeEventsAsync
                // already delivers them to any future subscriber.
                if (!string.IsNullOrEmpty(statusChanged.NeedsAction))
                {
                    _needsAttention = true;
                }

                _RecomputeStatus();
                break;

            case RateLimitInfo:
            case UnknownEvent:
                break;
        }
    }

    /// <summary>
    /// Derives <see cref="SessionStatus"/> from the flags this view model already tracks:
    /// busy while a turn is in flight, needs-attention while a permission/needs_action signal is
    /// outstanding (takes priority over busy so it still surfaces if a new send arrives before the
    /// user reacts), done once a turn completed and nothing is pending, idle otherwise.
    /// </summary>
    private void _RecomputeStatus()
    {
        SessionStatus = (_needsAttention, IsBusy) switch
        {
            (true, _) => SessionStatus.NeedsAttention,
            (false, true) => SessionStatus.Busy,
            (false, false) => Transcript.Any(t => t.Kind == TranscriptEntryKind.TurnCompleted) ? SessionStatus.Done : SessionStatus.Idle,
        };
    }

    /// <summary>
    /// Removes the in-progress "Thinking..." transcript row, if any, once real text/tool-use
    /// output or a turn boundary makes it stale. No-op when there is no current thinking entry.
    /// </summary>
    private void _RemoveCurrentThinkingEntry()
    {
        if (_currentThinkingEntry is null)
        {
            return;
        }

        Transcript.Remove(_currentThinkingEntry);
        _currentThinkingEntry = null;
    }

    public override async ValueTask DisposeAsync()
    {
        _lifetimeCancellation?.Cancel();

        // Dispose the session (kill the CLI process) first: that closes its stdout so the event loop
        // unwinds on its own, and — critically on app shutdown — it kills the child claude even if the
        // UI dispatcher is already gone and the loop's final dispatch can't complete, which is what
        // kept the child (and its MCP permission-server connection) alive and hung the process (#32).
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }

        if (_eventLoopTask is not null)
        {
            try
            {
                // The child is already dead here; a bounded wait keeps a stuck final UI dispatch from
                // blocking shutdown forever.
                await _eventLoopTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException)
            {
                // Expected: cancelling the lifetime token ends the event loop.
            }
            catch (TimeoutException)
            {
                // The dispatcher is gone and the loop can't finish; the child is already killed, so
                // dropping the wait is safe.
            }
        }

        _lifetimeCancellation?.Dispose();
    }
}
