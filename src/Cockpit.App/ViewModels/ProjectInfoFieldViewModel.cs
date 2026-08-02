using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

// One editable row of a project's extra information (AC-295): a label the operator chose and the value under it.
// Neither is validated — both are the operator's own words, and a row that is only a pasted link, with no label
// yet, is a perfectly good row. An untouched one is dropped on save rather than held against it.
public partial class ProjectInfoFieldViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private string _value;

    // Whether a session started on this project is told this row (AC-314). Off unless the operator ticks it.
    [ObservableProperty]
    private bool _isSharedWithSessions;

    // Whether the value is a credential (AC-318): stored encrypted, masked everywhere it is shown, and never told to a session.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShareWithSessions))]
    private bool _isSecret;

    // Whether the sharing tick is worth offering. A secret never reaches a session, so the choice is not the
    // operator's to make on that row — the box goes insensitive rather than sitting there ticked and ignored.
    public bool CanShareWithSessions => !IsSecret;

    // Marking a row secret unticks the sharing it can no longer have, rather than leaving the flag true underneath a
    // box gone grey. The domain gate (ReachesSessions) already keeps it out of a prompt either way, so this is about
    // the editor telling the truth about its own state — and about what the operator sees when they untick Secret again.
    partial void OnIsSecretChanged(bool value)
    {
        if (value)
        {
            IsSharedWithSessions = false;
        }
    }

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
