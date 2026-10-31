using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Swarm;

namespace Cockpit.App.ViewModels;

// AC-1929: a row merged after a split says so; the merge's divider shows per row which member wrote it, and carries
// the one Compare / Keep the newest question that merge asked.
public partial class TranscriptEntryViewModel
{
    public string? WrittenWhileApartOn { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MergeOriginLabel))]
    [NotifyPropertyChangedFor(nameof(MergeToggleLabel))]
    private bool _showsMergeOrigin;

    public string? MergeOriginLabel => WrittenWhileApartOn is not { } origin ? null
        : ShowsMergeOrigin ? $"written while apart · on {origin}" : "written while apart";

    public bool IsMergeDivider => IsDivider && Id.StartsWith(ConversationMerge.SeparatorIdPrefix, StringComparison.Ordinal);

    public string MergeToggleLabel => ShowsMergeOrigin ? "Hide what was merged" : "Show what was merged";

    public SwarmMergeQuestion? MergeQuestion =>
        IsMergeDivider && Session?.MergeQuestion is { } question && question.DividerId == Id ? question : null;

    internal void OnMergeQuestionChanged() => OnPropertyChanged(nameof(MergeQuestion));

    [RelayCommand]
    private void ToggleMergeOrigins()
    {
        var show = !ShowsMergeOrigin;
        ShowsMergeOrigin = show;
        if (Session is null)
        {
            return;
        }

        foreach (var row in Session.Transcript.Where(row => row.WrittenWhileApartOn is not null))
        {
            row.ShowsMergeOrigin = show;
        }
    }

    [RelayCommand]
    private void CompareMerge() => MergeQuestion?.Compare();

    [RelayCommand]
    private void KeepNewestMerge() => MergeQuestion?.KeepNewest();
}
