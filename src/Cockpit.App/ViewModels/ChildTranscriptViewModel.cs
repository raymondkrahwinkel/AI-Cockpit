using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

public sealed partial class ChildTranscriptViewModel : ObservableObject, IDisposable
{
    private readonly BackgroundTask _reference;
    private readonly Func<BackgroundTask, string?, CancellationToken, Task<ChildTranscriptPage>> _read;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _ids = [];
    private string? _cursor;
    private string? _parentPromptId;
    private bool _hasFirstEntry;
    public ChildAgentViewModel Child { get; }
    public ChildTranscriptViewModel(ChildAgentViewModel child, Func<BackgroundTask, string?, CancellationToken, Task<ChildTranscriptPage>> read)
    {
        Child = child;
        _reference = child.Reference;
        _read = read;
        child.Owner.PropertyChanged += _OnOwnerChanged;
        child.PropertyChanged += _OnChildChanged;
        Rows.CollectionChanged += _OnRowsChanged;
    }
    public ObservableCollection<TranscriptEntryViewModel> Rows { get; } = [];
    [ObservableProperty] private string _parentPrompt = "unknown";
    public IEnumerable<TranscriptEntryViewModel> TranscriptRows => Rows.Where(row => row.Id != _parentPromptId);
    public string ReadingStatus => Child.Reference.State switch { "completed" => "Finished", "running" => "Running", "unknown" => "Unknown", _ => Child.StatusText };
    public string ReadingExplanation => $"You are reading a sub-agent. It has no pane, no process and no permission settings of its own — it runs inside {Child.Owner.Title}. To steer it, ask the parent.";
    public string AgentIdentity => Child.Reference.AgentId ?? Child.Reference.TaskId;
    public string SourceText => IsTerminalOwner ? "provider transcript" : "provider history";
    public string TerminalExplanation => IsTerminalOwner ? SteeringExplanation : "Chat session: no terminal to open.";

    private void _OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Reset)
        {
            _hasFirstEntry = false;
            _parentPromptId = null;
            ParentPrompt = "unknown";
        }
        if (!_hasFirstEntry && Rows.FirstOrDefault() is { } first)
        {
            _hasFirstEntry = true;
            if (first.Kind == TranscriptEntryKind.UserText)
            {
                _parentPromptId = first.Id;
                ParentPrompt = first.Text;
            }
        }
        OnPropertyChanged(nameof(TranscriptRows));
    }
    [ObservableProperty] private string _status = "Loading…";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool _isReadComplete;
    private bool CanLoadMore() => !IsReadComplete;

    private bool _IsCurrent => !_lifetime.IsCancellationRequested && ReferenceEquals(Child.Owner.ChildTranscript, this)
        && Child.Owner.IsCurrentChild(Child, _reference);
    public bool IsTerminalOwner => Child.Owner is TtyViewModel;
    public bool CanPrepareParentRequest => _IsCurrent && Child.Owner.CanPrepareChildRequest;
    public bool CanOpenTerminal => _IsCurrent && Child.Owner.CanOpenOriginalTerminal;
    public string SteeringExplanation => !_IsCurrent
        ? "This child selection has changed. Reopen it from its owner."
        : IsTerminalOwner
            ? CanOpenTerminal ? "This terminal has no Cockpit composer. Open the original terminal to use its own sub-agent controls. Nothing is typed automatically."
                : "The original terminal is unavailable because its owner has stopped or disconnected."
            : !CanPrepareParentRequest ? "The parent is unavailable or cannot accept requests right now."
                : Child.Owner.SessionStatus is Core.Abstractions.Sessions.SessionStatus.Busy or Core.Abstractions.Sessions.SessionStatus.WorkingBackground
                    ? "Prepare an editable parent draft. Use its normal Send action with the existing queue or mid-turn policy. Child delivery is not guaranteed."
                    : "Prepare an editable parent draft. Nothing is sent until you use the parent's normal Send action. Child delivery is not guaranteed.";
    [ObservableProperty] private string _steeringStatus = "";

    [RelayCommand(CanExecute = nameof(CanPrepareParentRequest))]
    private void PrepareParentRequest()
    {
        if (!CanPrepareParentRequest)
        {
            SteeringStatus = SteeringExplanation;
            return;
        }
        string name = JsonSerializer.Serialize(_reference.Description ?? Child.Title);
        string agent = JsonSerializer.Serialize(_reference.AgentId ?? _reference.TaskId);
        string run = JsonSerializer.Serialize(_reference.RunId ?? "unknown");
        var request = $"Request to parent about sub-agent {name}\nProvider agent/thread: {agent}\nRun: {run}\n\n"
            + "Please steer this exact child with the following request:\n[Write your request here.]\n\n"
            + "If this child or run is unavailable or cannot resume, explain that. Do not choose another child or start a replacement. Do not retry automatically after uncertain delivery.";
        if (!Child.Owner.PrepareChildRequest(Child, _reference, request))
        {
            SteeringStatus = "The parent or child changed. No draft was prepared.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenTerminal))]
    private void OpenOriginalTerminal()
    {
        if (!CanOpenTerminal || !Child.Owner.OpenOriginalTerminal(Child, _reference))
        {
            SteeringStatus = "The original terminal or child is no longer available.";
        }
    }

    private void _OnOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        OnPropertyChanged(nameof(ReadingExplanation));
        OnPropertyChanged(nameof(TerminalExplanation));
        OnPropertyChanged(nameof(CanPrepareParentRequest));
        OnPropertyChanged(nameof(CanOpenTerminal));
        OnPropertyChanged(nameof(SteeringExplanation));
        PrepareParentRequestCommand.NotifyCanExecuteChanged();
        OpenOriginalTerminalCommand.NotifyCanExecuteChanged();
    }

    private void _OnChildChanged(object? sender, PropertyChangedEventArgs args)
    {
        OnPropertyChanged(nameof(ReadingStatus));
        OnPropertyChanged(nameof(AgentIdentity));
    }

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private async Task LoadMoreAsync()
    {
        try
        {
            BackgroundTask reference = _reference;
            ChildTranscriptPage page = await _read(reference, _cursor, _lifetime.Token);
            if (_lifetime.IsCancellationRequested)
            {
                return;
            }
            if (page.Reset)
            {
                Rows.Clear();
                _ids.Clear();
            }
            foreach (var entry in page.Entries)
            {
                if (!_ids.Add(entry.Id))
                {
                    continue;
                }
                TranscriptEntryKind kind = entry.Kind switch
                {
                    "assistant" => TranscriptEntryKind.AssistantText,
                    "user" => TranscriptEntryKind.UserText,
                    "thinking" => TranscriptEntryKind.Thinking,
                    _ => TranscriptEntryKind.ToolResult,
                };
                Rows.Add(new TranscriptEntryViewModel(kind, entry.Text) { Id = entry.Id });
                if (Rows.Count > 256)
                {
                    _ids.Remove(Rows[0].Id);
                    Rows.RemoveAt(0);
                }
            }
            _cursor = page.Cursor;
            IsReadComplete = page.Cursor is null;
            Status = page.Status == "Read-only" ? "" : page.Status;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            Status = "Not available: could not read child history";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (LoadMoreCommand.IsRunning)
        {
            return;
        }
        _cursor = null;
        Rows.Clear();
        _ids.Clear();
        IsReadComplete = false;
        await LoadMoreAsync();
    }

    public void Dispose()
    {
        Child.Owner.PropertyChanged -= _OnOwnerChanged;
        Child.PropertyChanged -= _OnChildChanged;
        Rows.CollectionChanged -= _OnRowsChanged;
        _lifetime.Cancel();
    }
}
