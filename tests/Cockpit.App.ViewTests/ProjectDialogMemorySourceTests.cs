using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using FluentAssertions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The "Memory" row's picker in the real project editor (AC-165/166). Measured against the actual markup rather
/// than the view model alone, the same reason <c>ProjectDialogPluginFieldTests</c> is: a binding that silently does
/// not wire up passes a view-model-only test happily, and everything here lives in the binding.
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

        return viewModel;
    }

    // Distinguished from the Profile row's own ComboBox (ObservableCollection<string>) by the runtime type of what
    // it is actually bound to, rather than by position — the one thing that cannot drift out of sync with the markup.
    private static ComboBox? MemoryComboBox(Window window) =>
        window.GetVisualDescendants().OfType<ComboBox>()
            .FirstOrDefault(box => box.ItemsSource is ObservableCollection<MemorySourceChoice>);

    private static Button ChooseButton(ProjectDialogViewModel viewModel, Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .First(button => ReferenceEquals(button.Command, viewModel.PickMemoryCommand));

    [Fact]
    public void NoMemorySourcesRegistered_ThePickerStaysHiddenAndChooseStaysUsable() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var comboBox = MemoryComboBox(window);
        var chooseButton = ChooseButton(viewModel, window);
        window.Close();

        comboBox.Should().NotBeNull("the row still holds the combo box in the tree — Avalonia's IsVisible=\"False\" collapses it out of layout instead of merely painting it invisible, which is what leaves no gap behind it, not the control's absence");
        comboBox!.IsEffectivelyVisible.Should().BeFalse("a cockpit with no memory-source plugin must not hold an empty picker open");
        chooseButton.IsEnabled.Should().BeTrue("with no source registered, Folder is the only mode there is");
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

        comboBox.Should().NotBeNull();
        comboBox!.IsEffectivelyVisible.Should().BeTrue();
        comboBox.ItemsSource!.Cast<object>().Should().Equal(folder, depot);
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

        viewModel.SelectedMemorySourceChoice.Should().Be(depot, "the combo box's own selection must reach the view model, not merely display it");
        chooseButton.IsEnabled.Should().BeFalse("a source's identifier is typed, not browsed for on disk");
    });

    [Fact]
    public void TypingTheIdentifierWithASourceSelected_ReachesWhatGetsSaved() => HeadlessAvalonia.Run(() =>
    {
        var depot = new MemorySourceChoice("Depot project", "depot");
        var viewModel = ViewModelWith(new MemorySourceChoice("Folder", null), depot);
        viewModel.SelectedMemorySourceChoice = depot;
        viewModel.Name = "Cockpit";
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var chooseButton = ChooseButton(viewModel, window);
        // The box beside "Choose…" in the same row — the one control that row actually has to type into.
        var textBox = ((Grid)chooseButton.Parent!).Children.OfType<TextBox>().First();
        textBox.Text = "cockpit";
        window.UpdateLayout();
        window.Close();

        viewModel.MemoryRef.Should().Be("cockpit");
        viewModel.ToProject().MemoryRef.Should().Be("depot:cockpit", "the plugin's scheme is prepended on save, not typed by the operator");
    });
}
