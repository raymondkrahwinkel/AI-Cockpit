using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zyra.Voice.Core.Abstractions;
using Zyra.Voice.Core.Abstractions.Claude;
using Zyra.Voice.Core.Abstractions.Profiles;
using Zyra.Voice.Core.Claude;
using Zyra.Voice.Core.Profiles;

namespace Zyra.Voice.App.ViewModels;

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

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _status = "Not started.";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>True while <see cref="ProfileChoices"/> should be shown for the user to pick from.</summary>
    [ObservableProperty]
    private bool _isChoosingProfile;

    [ObservableProperty]
    private ClaudeProfile? _selectedProfile;

    /// <summary>Label of the profile the running session was started under, once known.</summary>
    [ObservableProperty]
    private string? _activeProfileLabel;

    // Parameterless constructor kept for the Avalonia previewer design-time context.
    public ClaudeSessionViewModel()
    {
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
            await _session.StartAsync(profile, _lifetimeCancellation.Token);
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

        try
        {
            await _session.SendUserMessageAsync(text);
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Send failed: {ex.Message}"));
            IsBusy = false;
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

    private void Apply(ClaudeSessionEvent evt)
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
                _currentThinkingEntry = null;
                if (_currentAssistantEntry is null)
                {
                    _currentAssistantEntry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, string.Empty);
                    Transcript.Add(_currentAssistantEntry);
                }

                _currentAssistantEntry.AppendText(delta.Text);
                break;

            case AssistantTextCompleted completed:
                _currentThinkingEntry = null;
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
                Transcript.Add(new TranscriptEntryViewModel(
                    TranscriptEntryKind.ToolUse,
                    $"Tool: {toolUse.ToolName}({toolUse.InputJson})")
                {
                    ToolUseId = toolUse.ToolUseId,
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

                break;

            case Question question:
                Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Question, question.Text));
                break;

            case TurnCompleted turn:
                Transcript.Add(new TranscriptEntryViewModel(
                    TranscriptEntryKind.TurnCompleted,
                    turn.IsError ? $"Turn failed ({turn.Subtype})" : $"Turn completed ({turn.Subtype})"));
                _currentAssistantEntry = null;
                IsBusy = false;
                break;

            case SessionError error:
                Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, error.Message));
                IsBusy = false;
                break;

            // SessionStatusChanged/RateLimitInfo/UnknownEvent are out of scope for the transcript
            // view (per-session status overview and agent-tree rendering are later increments);
            // ConsumeEventsAsync already delivers them to any future subscriber.
            case SessionStatusChanged:
            case RateLimitInfo:
            case UnknownEvent:
                break;
        }
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
