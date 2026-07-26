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

    public ProjectInfoFieldViewModel(string label = "", string value = "")
    {
        _label = label;
        _value = value;
    }

    public ProjectInfoField ToDomain() => new(Label, Value);
}
