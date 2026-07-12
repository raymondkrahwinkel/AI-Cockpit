using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Transcripts;
using Cockpit.Core.Transcripts;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Backs the transcript-search dialog (#9): a query box over the on-disk <c>claude</c> transcripts, showing
/// matching user/assistant lines with a snippet, which session they came from and when. Search is explicit
/// (button or Enter) rather than search-as-you-type, so a broad history isn't re-scanned on every keystroke.
/// </summary>
public partial class TranscriptSearchDialogViewModel : ViewModelBase
{
    private const int MinQueryLength = 2;

    private readonly ITranscriptSearchService? _searchService;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _statusMessage = "Search your past sessions by any text you or the agent wrote.";

    public ObservableCollection<TranscriptSearchHit> Results { get; } = [];

    public bool HasResults => Results.Count > 0;

    public event Action? CloseRequested;

    // Design-time constructor for the previewer.
    public TranscriptSearchDialogViewModel()
    {
    }

    public TranscriptSearchDialogViewModel(ITranscriptSearchService searchService)
    {
        _searchService = searchService;
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var query = Query.Trim();
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));

        if (query.Length < MinQueryLength)
        {
            StatusMessage = $"Type at least {MinQueryLength} characters to search.";
            return;
        }

        if (_searchService is null)
        {
            return;
        }

        IsSearching = true;
        StatusMessage = "Searching…";
        try
        {
            var hits = await _searchService.SearchAsync(query);
            foreach (var hit in hits)
            {
                Results.Add(hit);
            }

            StatusMessage = hits.Count switch
            {
                0 => "No matches.",
                1 => "1 match.",
                _ => $"{hits.Count} matches (most recent sessions first).",
            };
        }
        catch (Exception exception)
        {
            StatusMessage = $"Search failed: {exception.Message}";
        }
        finally
        {
            IsSearching = false;
            OnPropertyChanged(nameof(HasResults));
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
