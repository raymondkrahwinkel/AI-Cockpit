using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Claude;
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
public partial class ClaudeSessionViewModel : ViewModelBase, ITransientService, IAsyncDisposable
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

    /// <summary>Populated only while <see cref="ProfileSelectionKind.RequiresChoice"/> is pending the user's pick.</summary>
    public ObservableCollection<ClaudeProfile> ProfileChoices { get; } = [];

    private static readonly PermissionModeOption[] _permissionModes =
    [
        new("Ask permissions", "default"),
        new("Accept edits", "acceptEdits"),
        new("Plan mode", "plan"),
        new("Auto mode", "auto"),
        new("Bypass permissions", "bypassPermissions"),
    ];

    /// <summary>Permission modes offered per session; the selected one becomes <c>--permission-mode</c> at launch.</summary>
    public IReadOnlyList<PermissionModeOption> PermissionModes => _permissionModes;

    [ObservableProperty]
    private PermissionModeOption _selectedPermissionMode = _permissionModes[3]; // Auto mode by default, matching the desktop app.

    private static readonly ModelOption[] _models =
    [
        new("Opus 4.8", "opus"),
        new("Sonnet", "sonnet"),
        new("Haiku", "haiku"),
    ];

    /// <summary>Models offered per session; the selected one becomes <c>--model</c> at launch and can be switched live.</summary>
    public IReadOnlyList<ModelOption> Models => _models;

    [ObservableProperty]
    private ModelOption _selectedModel = _models[1]; // Sonnet by default.

    // UNVERIFIED mapping — see EffortOption remarks: exact token budgets per level are a
    // best guess, not confirmed against the SDK/CLI.
    private static readonly EffortOption[] _efforts =
    [
        new("Low", "low", 4_000),
        new("Medium", "medium", 16_000),
        new("High", "high", 32_000),
    ];

    /// <summary>Thinking-effort levels offered per session; drives the thinking-budget control.</summary>
    public IReadOnlyList<EffortOption> Efforts => _efforts;

    [ObservableProperty]
    private EffortOption _selectedEffort = _efforts[1]; // Medium by default.

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _status = "Not started.";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Display title for this session's sidebar/grid panel, e.g. "Claude 1". Set by <see cref="CockpitViewModel"/>.</summary>
    [ObservableProperty]
    private string _title = "Claude";

    /// <summary>True while this is <see cref="CockpitViewModel.SelectedSession"/> — drives the sidebar's active-item highlight. Set by <see cref="CockpitViewModel"/>.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Coarse status for the sidebar/grid overview — see <see cref="ViewModels.SessionStatus"/>.</summary>
    [ObservableProperty]
    private SessionStatus _sessionStatus = SessionStatus.Idle;

    /// <summary>Short human-readable label for <see cref="SessionStatus"/>, for the sidebar status row.</summary>
    public string SessionStatusLabel => SessionStatus switch
    {
        SessionStatus.Busy => "Busy",
        SessionStatus.WaitingForInput => "Waiting for input",
        SessionStatus.NeedsAttention => "Needs attention",
        SessionStatus.Done => "Done",
        _ => "Idle",
    };

    /// <summary>Theme brush resource key for the status dot — resolved in the view via a converter.</summary>
    public string SessionStatusBrushKey => SessionStatus switch
    {
        SessionStatus.Busy => "CockpitStatusBusyBrush",
        SessionStatus.WaitingForInput or SessionStatus.NeedsAttention => "CockpitStatusWaitingBrush",
        SessionStatus.Done => "CockpitStatusDoneBrush",
        _ => "CockpitTextFaintBrush",
    };

    /// <summary>Keeps the derived status label/brush in sync whenever <see cref="SessionStatus"/> changes.</summary>
    partial void OnSessionStatusChanged(SessionStatus value)
    {
        OnPropertyChanged(nameof(SessionStatusLabel));
        OnPropertyChanged(nameof(SessionStatusBrushKey));
    }

    /// <summary>True while a pending permission decision or CLI <c>needs_action</c> signal is outstanding, driving <see cref="SessionStatus.NeedsAttention"/>.</summary>
    private bool _needsAttention;

    /// <summary>True while <see cref="ProfileChoices"/> should be shown for the user to pick from.</summary>
    [ObservableProperty]
    private bool _isChoosingProfile;

    [ObservableProperty]
    private ClaudeProfile? _selectedProfile;

    /// <summary>Label of the profile the running session was started under, once known.</summary>
    [ObservableProperty]
    private string? _activeProfileLabel;

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
            IsPendingPermission = true,
        });
    }

    public ClaudeSessionViewModel(IClaudeSession session, IClaudeProfileStore profileStore, IClaudeProfileLoginChecker loginChecker)
    {
        _session = session;
        _profileStore = profileStore;
        _loginChecker = loginChecker;
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

    /// <summary>
    /// Live-switches the running session's thinking-effort level. UNVERIFIED: routed through
    /// <see cref="IClaudeSession.SetModelAsync"/>'s sibling control-channel plumbing is not
    /// available for thinking tokens on <see cref="IClaudeSession"/> yet (no
    /// <c>SetMaxThinkingTokensAsync</c> member exists) — flagged as a gap for the parent to
    /// confirm against the SDK's <c>setMaxThinkingTokens</c> before wiring further.
    /// </summary>
    partial void OnSelectedEffortChanged(EffortOption value)
    {
        // See remarks: no session-level control method exists yet to carry this live. The
        // dropdown selection is tracked so a future increment can wire it once
        // IClaudeSession grows a SetMaxThinkingTokensAsync (or equivalent) method.
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

    [RelayCommand]
    private async Task SendAsync()
    {
        if (_session is null || string.IsNullOrWhiteSpace(InputText))
        {
            return;
        }

        var text = InputText;
        InputText = string.Empty;
        Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"> {text}"));
        _currentAssistantEntry = null;
        IsBusy = true;
        _needsAttention = false;
        _RecomputeStatus();

        try
        {
            await _session.SendUserMessageAsync(text);
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

    public async ValueTask DisposeAsync()
    {
        _lifetimeCancellation?.Cancel();

        if (_eventLoopTask is not null)
        {
            try
            {
                await _eventLoopTask;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        if (_session is not null)
        {
            await _session.DisposeAsync();
        }

        _lifetimeCancellation?.Dispose();
    }
}
