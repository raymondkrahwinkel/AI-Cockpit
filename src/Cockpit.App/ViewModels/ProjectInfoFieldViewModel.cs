using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One editable row of a project's extra information (AC-295): a label the operator chose and the value under it.
/// Neither is validated — both are the operator's own words, and a row that is only a pasted link, with no label
/// yet, is a perfectly good row. An untouched one is dropped on save rather than held against it.
/// </summary>
public partial class ProjectInfoFieldViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private string _value;

    /// <summary>Whether a session started on this project is told this row (AC-314). Off unless the operator ticks it.</summary>
    [ObservableProperty]
    private bool _isSharedWithSessions;

    /// <summary>Whether the value is a credential (AC-318): stored encrypted, masked everywhere it is shown, and never told to a session.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShareWithSessions))]
    private bool _isSecret;

    /// <summary>
    /// Whether the sharing tick is worth offering. A secret never reaches a session, so the choice is not the
    /// operator's to make on that row — the box goes insensitive rather than sitting there ticked and ignored.
    /// </summary>
    public bool CanShareWithSessions => !IsSecret;

    public ProjectInfoFieldViewModel(
        string label = "",
        string value = "",
        bool isSharedWithSessions = false,
        bool isSecret = false)
    {
        _label = label;
        _value = value;
        _isSharedWithSessions = isSharedWithSessions;
        _isSecret = isSecret;
    }

    public ProjectInfoField ToDomain() =>
        new(Label, Value) { IsSharedWithSessions = IsSharedWithSessions, IsSecret = IsSecret };
}
