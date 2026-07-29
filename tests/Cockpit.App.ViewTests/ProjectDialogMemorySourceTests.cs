using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// A Memory row's picker in the real project editor (AC-165/166). Measured against the actual markup rather than
/// the view model alone, the same reason <c>ProjectDialogPluginFieldTests</c> is: a binding that silently does not
/// wire up passes a view-model-only test happily, and everything here lives in the binding.
/// <para>
/// AC-485 moved the picker from one field the dialog always showed onto a <see cref="ProjectResourceRowViewModel"/>
/// row instead — every test here adds exactly one row (<c>AddResourceRowCommand</c> defaults its role to Memory)
/// and finds that row's own controls, rather than a control fixed once in the window.
/// </para>
/// </summary>
[Collection("avalonia")]
public class ProjectDialogMemorySourceTests
{
    private static ProjectDialogViewModel ViewModelWith(params MemorySourceChoice[] sources)
    {
        var viewModel = new ProjectDialogViewModel();
        foreach (var source in sources)
        {
            viewModel.MemorySourceChoices.Add(source);
        }

        viewModel.AddResourceRowCommand.Execute(null);
        return viewModel;
    }

    // Distinguished from the Profile row's own ComboBox (ObservableCollection<string>) by the runtime type of what
    // it is actually bound to, rather than by position — the one thing that cannot drift out of sync with the markup.
    private static ComboBox? MemoryComboBox(Window window) =>
        window.GetVisualDescendants().OfType<ComboBox>()
            .FirstOrDefault(box => box.ItemsSource is ObservableCollection<MemorySourceChoice>);

    private static Button ChooseButton(ProjectDialogViewModel viewModel, Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .First(button => ReferenceEquals(button.Command, viewModel.PickResourceCommand));

    [Fact]
    public void NoMemorySourcesRegistered_ThePickerStaysHiddenAndChooseStaysUsable() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = ViewModelWith();
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var comboBox = MemoryComboBox(window);
        var chooseButton = ChooseButton(viewModel, window);
        window.Close();

        Assert.NotNull(comboBox);
        Assert.False(comboBox!.IsEffectivelyVisible, "a cockpit with no memory-source plugin must not hold an empty picker open");
        Assert.True(chooseButton.IsEnabled, "with no source registered, Folder is the only mode there is");
    });

    [Fact]
    public void SourcesRegistered_ThePickerOffersFolderThenEachSource() => HeadlessAvalonia.Run(() =>
    {
        var folder = new MemorySourceChoice("Folder", null);
        var depot = new MemorySourceChoice("Depot project", "depot");
        var viewModel = ViewModelWith(folder, depot);
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var comboBox = MemoryComboBox(window);
        window.Close();

        Assert.NotNull(comboBox);
        Assert.True(comboBox!.IsEffectivelyVisible);
        Assert.Equal(new object[] { folder, depot }, comboBox.ItemsSource!.Cast<object>());
    });

    [Fact]
    public void PickingASourceInTheComboBox_ReachesTheViewModelAndDisablesChoose() => HeadlessAvalonia.Run(() =>
    {
        var depot = new MemorySourceChoice("Depot project", "depot");
        var viewModel = ViewModelWith(new MemorySourceChoice("Folder", null), depot);
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var comboBox = MemoryComboBox(window)!;
        comboBox.SelectedItem = depot;
        window.UpdateLayout();
        var chooseButton = ChooseButton(viewModel, window);
        window.Close();

        Assert.Equal(depot, viewModel.ResourceRows.Single().SelectedMemorySourceChoice);
        Assert.False(chooseButton.IsEnabled, "a source's identifier is typed, not browsed for on disk");
    });

    [Fact]
    public void TypingTheIdentifierWithASourceSelected_ReachesWhatGetsSaved() => HeadlessAvalonia.Run(() =>
    {
        var depot = new MemorySourceChoice("Depot project", "depot");
        var viewModel = ViewModelWith(new MemorySourceChoice("Folder", null), depot);
        viewModel.ResourceRows.Single().SelectedMemorySourceChoice = depot;
        viewModel.Name = "Cockpit";
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var chooseButton = ChooseButton(viewModel, window);
        // The box beside "Choose…" in the same row — the one control that row actually has to type into.
        var textBox = ((Grid)chooseButton.Parent!).Children.OfType<TextBox>().First();
        textBox.Focus();
        textBox.Text = "cockpit";
        window.UpdateLayout();
        // AC-485 review (MUST-FIX 2): the box now commits on losing focus, not per keystroke — any other focusable
        // control stands in for "the operator moved on".
        window.GetVisualDescendants().OfType<TextBox>().First(other => !ReferenceEquals(other, textBox)).Focus();
        window.UpdateLayout();
        window.Close();

        Assert.Equal("cockpit", viewModel.ResourceRows.Single().Reference);
        Assert.Equal("depot:cockpit", viewModel.ToProject().MemoryRef);
    });

    // AC-502: a source that CAN enumerate its own locations flips this from disabled back to enabled — the
    // opposite of PickingASourceInTheComboBox_ReachesTheViewModelAndDisablesChoose above, which pins the case a
    // source cannot list at all.
    [Fact]
    public void PickingASourceThatCanListItsOwnLocations_KeepsChooseEnabled() => HeadlessAvalonia.Run(() =>
    {
        var depot = new MemorySourceChoice("Depot project", "depot", ListLocationsAsync: _ => Task.FromResult(
            Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocationsResult.Success([])));
        var viewModel = ViewModelWith(new MemorySourceChoice("Folder", null), depot);
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var comboBox = MemoryComboBox(window)!;
        comboBox.SelectedItem = depot;
        window.UpdateLayout();
        var chooseButton = ChooseButton(viewModel, window);
        window.Close();

        Assert.True(chooseButton.IsEnabled, "this source can list its own locations, so Choose… opens a picker of names");
    });
}
