using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.App.ViewModels;

public sealed partial class ChildTranscriptViewModel(ChildAgentViewModel child, ISessionControl control) : ObservableObject, IDisposable
{
    private readonly Cockpit.Core.Sessions.BackgroundTask _reference = child.Reference;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _ids = [];
    private string? _cursor;
    public ChildAgentViewModel Child { get; } = child;
    public ObservableCollection<TranscriptEntryViewModel> Rows { get; } = [];
    [ObservableProperty] private string _status = "Loading…";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool _isReadComplete;
    private bool CanLoadMore() => !IsReadComplete;

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private async Task LoadMoreAsync()
    {
        try
        {
            var reference = _reference;
            var page = await control.ReadChildTranscriptAsync(reference.AgentId ?? reference.TaskId, reference.RunId, _cursor, _lifetime.Token);
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
                var kind = entry.Kind switch
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
            Status = page.Status;
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
        _lifetime.Cancel();
    }
}
