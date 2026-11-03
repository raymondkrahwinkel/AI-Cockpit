using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The "Memory, instructions and reference" section in the real project editor (AC-485). Measured against the
/// actual markup rather than the view model alone, the same reason <see cref="ProjectDialogPluginFieldTests"/> and
/// <see cref="ProjectDialogMemorySourceTests"/> already are: a binding that silently does not wire up passes a
/// view-model-only test happily.
/// </summary>
[Collection("avalonia")]
public class ProjectDialogResourceRowTests
{
    // Distinguished by the static list every row's role picker shares — the one thing that cannot drift out of
    // sync with which row's markup it lives in, the same trick the Memory-source combo box tests already use.
    private static ComboBox RoleComboBox(Window window) =>
        window.GetVisualDescendants().OfType<ComboBox>()
            .First(box => ReferenceEquals(box.ItemsSource, ProjectResourceRowViewModel.RoleChoices));

    // Distinguished by its own Content text — every scene in this file has at most one resource row.
    private static CheckBox? VisibleSendAlongCheckBox(Window window) =>
        window.GetVisualDescendants().OfType<CheckBox>()
            .FirstOrDefault(box => box.IsEffectivelyVisible && Equals(box.Content, "Send along"));

    // Found via the row's own "Choose…" button rather than any property of the TextBox itself — Reference starts
    // out blank for every row, so nothing about the box's own state can tell one row's apart from another's, but
    // each row's "Choose…" button carries that row as its CommandParameter.
    private static TextBox ReferenceTextBox(ProjectDialogViewModel viewModel, Window window, ProjectResourceRowViewModel row)
    {
        var button = window.GetVisualDescendants().OfType<Button>()
            .First(b => ReferenceEquals(b.Command, viewModel.PickResourceCommand) && ReferenceEquals(b.CommandParameter, row));
        var grid = button.GetVisualParent<Grid>()!;
        return grid.GetVisualChildren().OfType<TextBox>().Single();
    }

    /// <summary>
    /// The other half of committing on focus loss, and the half that costs data if it is wrong: saving while the
    /// caret is still in the box. Every other text field in this app commits per keystroke, so this row is the only
    /// place where "what is on screen" and "what gets saved" can differ at all — and an operator who types a path
    /// and goes straight for the confirm button is the ordinary way to use this dialog, not an edge case.
    /// <para>
    /// The click is raised directly rather than through the pointer, so focus deliberately does <em>not</em> move.
    /// That is the pessimistic case and the one worth pinning: a real click happens to move focus first, which means
    /// this would keep working by luck rather than by design, and would break the day anything triggers the save
    /// without touching focus — a shortcut, an Enter binding, an automated flow.
    /// </para>
    /// </summary>
    [Fact]
    public void SavingStraightFromTheReferenceBox_KeepsWhatWasTyped() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel { Name = "Cockpit" };
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows.Single();
        row.Role = ProjectResourceRole.Reference;
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var textBox = ReferenceTextBox(viewModel, window, row);
        textBox.Focus();
        textBox.Text = "docs/handbook.md";
        window.UpdateLayout();

        var confirm = window.GetVisualDescendants().OfType<Button>()
            .First(button => ReferenceEquals(button.Command, viewModel.SaveCommand));
        confirm.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        var saved = viewModel.ToProject();
        window.Close();

        var resource = Assert.Single(saved.Resources);
        // A reference typed and saved without leaving the box must not be dropped.
        Assert.Equal("docs/handbook.md", resource.Reference);
    });

    // --- AC-486: "Send along" only appears, and only means anything, for an Instructions row ---------------------------

    [Fact]
    public void SwitchingRoleAwayFromInstructions_HidesTheSendAlongCheckboxAndTurnsItOff() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows.Single();
        row.Role = ProjectResourceRole.Instructions;
        row.SendsContent = true;
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        RoleComboBox(window).SelectedItem = ProjectResourceRole.Reference;
        window.UpdateLayout();

        var checkbox = VisibleSendAlongCheckBox(window);
        window.Close();

        // The row no longer means Instructions, so the checkbox must not linger visible.
        Assert.Null(checkbox);
        // Switching roles reset it, and that reset must actually reach the real markup, not only the view model.
        Assert.False(row.SendsContent);
    });

    // --- AC-612: a row pointing at a likely secrets location is reported, refuses content, and is disabled --------

    [Fact]
    public void ASecretPathRow_TickingSendAlongInTheViewModel_StaysFalseInTheRealMarkup() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows.Single();
        row.Role = ProjectResourceRole.Instructions;
        row.Reference = "~/.ssh/id_rsa";
        row.SendsContent = true; // an attempt to force it, e.g. from a hand-edited saved value re-applied at load
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var checkbox = VisibleSendAlongCheckBox(window);
        window.Close();

        Assert.NotNull(checkbox);
        Assert.NotEqual(true, checkbox!.IsChecked);
    });

    [Fact]
    public void TypingASecretPathLive_UnchecksAnAlreadyTickedSendAlongAtOnce() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows.Single();
        row.Role = ProjectResourceRole.Instructions;
        row.Reference = "docs/CONVENTIONS.md";
        row.SendsContent = true;
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();
        Assert.True(VisibleSendAlongCheckBox(window)!.IsChecked);

        var textBox = ReferenceTextBox(viewModel, window, row);
        textBox.Focus();
        textBox.Text = "~/.ssh/id_rsa";
        window.UpdateLayout();

        var checkbox = VisibleSendAlongCheckBox(window);
        window.Close();

        // No 400ms diagnostics window here — the tick comes off the instant the shape looks secret, live, not only on save.
        Assert.NotNull(checkbox);
        Assert.NotEqual(true, checkbox!.IsChecked);
        Assert.False(row.SendsContent);
    });
}
