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
    private static Button AddRowButton(ProjectDialogViewModel viewModel, Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .First(button => ReferenceEquals(button.Command, viewModel.AddResourceRowCommand));

    private static Button RemoveRowButton(ProjectDialogViewModel viewModel, Window window, ProjectResourceRowViewModel row) =>
        window.GetVisualDescendants().OfType<Button>()
            .First(button => ReferenceEquals(button.Command, viewModel.RemoveResourceRowCommand) && ReferenceEquals(button.CommandParameter, row));

    // Distinguished by the static list every row's role picker shares — the one thing that cannot drift out of
    // sync with which row's markup it lives in, the same trick the Memory-source combo box tests already use.
    private static ComboBox RoleComboBox(Window window) =>
        window.GetVisualDescendants().OfType<ComboBox>()
            .First(box => ReferenceEquals(box.ItemsSource, ProjectResourceRowViewModel.RoleChoices));

    private static TextBlock? VisibleTextContaining(Window window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.IsEffectivelyVisible && block.Text is { } value && value.Contains(text, StringComparison.Ordinal));

    // Found via the row's own "Choose…" button rather than any property of the TextBox itself — Reference starts
    // out blank for every row, so nothing about the box's own state can tell one row's apart from another's, but
    // each row's "Choose…" button carries that row as its CommandParameter (the same trick AddRowButton/RemoveRowButton
    // already use).
    private static TextBox ReferenceTextBox(ProjectDialogViewModel viewModel, Window window, ProjectResourceRowViewModel row)
    {
        var button = window.GetVisualDescendants().OfType<Button>()
            .First(b => ReferenceEquals(b.Command, viewModel.PickResourceCommand) && ReferenceEquals(b.CommandParameter, row));
        var grid = button.GetVisualParent<Grid>()!;
        return grid.GetVisualChildren().OfType<TextBox>().Single();
    }

    [Fact]
    public void AddRow_ShowsARoleComboBoxAndAnEmptyReferenceBox() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        AddRowButton(viewModel, window).Command!.Execute(null);
        window.UpdateLayout();

        var roleBox = RoleComboBox(window);
        window.Close();

        Assert.Single(viewModel.ResourceRows);
        Assert.Equal(ProjectResourceRole.Memory, roleBox.SelectedItem);
    });

    [Fact]
    public void RemoveRow_TakesItBackOutOfTheList() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows.Single();
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        RemoveRowButton(viewModel, window, row).Command!.Execute(row);
        window.UpdateLayout();
        window.Close();

        Assert.Empty(viewModel.ResourceRows);
    });

    [Fact]
    public void PickingARoleInTheComboBox_ReachesTheRow() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        RoleComboBox(window).SelectedItem = ProjectResourceRole.Instructions;
        window.UpdateLayout();
        window.Close();

        Assert.Equal(ProjectResourceRole.Instructions, viewModel.ResourceRows.Single().Role);
    });

    [Fact]
    public void ABrokenRow_ShowsTheNotFoundHint() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        viewModel.ResourceRows.Single().IsBroken = true;
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var hint = VisibleTextContaining(window, "could not be found");
        window.Close();

        Assert.NotNull(hint);
    });

    [Fact]
    public void AnUnbrokenRow_NeverShowsTheNotFoundHint() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var hint = VisibleTextContaining(window, "could not be found");
        window.Close();

        Assert.Null(hint);
    });

    [Fact]
    public void AMachineBoundRow_ShowsTheMachineBoundHint() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        viewModel.ResourceRows.Single().IsMachineBound = true;
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var hint = VisibleTextContaining(window, "specific to this machine");
        window.Close();

        Assert.NotNull(hint);
    });

    [Fact]
    public void APortableRow_NeverShowsTheMachineBoundHint() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var hint = VisibleTextContaining(window, "specific to this machine");
        window.Close();

        Assert.Null(hint);
    });

    /// <summary>
    /// AC-485 review (MUST-FIX 2, FIX 4): what the operator types reaches the row at once — the same per-keystroke
    /// binding every other field in this app uses — while the <em>judgement</em> about it waits for the typing to
    /// stop. Those are two different things, and an earlier attempt conflated them: binding on focus loss did stop
    /// the red "could not be found" flashing mid-edit, but it also meant a path typed and saved without leaving the
    /// box never reached the row at all (see <see cref="SavingStraightFromTheReferenceBox_KeepsWhatWasTyped"/>).
    /// Debouncing the check rather than delaying the value gets the quiet edit without the lost one.
    /// </summary>
    [Fact]
    public void TypingInTheReferenceBox_ReachesTheRowAtOnceButIsNotJudgedYet() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows.Single();
        row.Role = ProjectResourceRole.Reference;
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        // Rooted per platform: CI runs Linux, development runs Windows, and "is this absolute" is the one question
        // this row's diagnostics actually ask.
        var typed = Path.Combine(
            OperatingSystem.IsWindows() ? @"C:\Users\raymond" : "/home/raymond",
            "no-such-folder",
            "handbook.md");

        var textBox = ReferenceTextBox(viewModel, window, row);
        textBox.Focus();
        textBox.Text = typed;
        window.UpdateLayout();
        window.Close();

        Assert.Equal(typed, row.Reference);
        Assert.False(row.IsBroken,
            "a half-typed path is a path that does not exist — judging it while the operator is still writing it is how the row turns red under their hands");
    });

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
        Assert.Equal("docs/handbook.md", resource.Reference);
    });

    /// <summary>
    /// AC-485 review (FIX 8): a hairline under every row read fine between two rows, but under the very last one it
    /// hung there with nothing below it but the "+ Add row" button — a divider for a row that is not there.
    /// </summary>
    [Fact]
    public void MultipleRows_OnlyTheLastRowsDividerIsSuppressed() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        viewModel.AddResourceRowCommand.Execute(null);
        viewModel.AddResourceRowCommand.Execute(null);
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var dividers = window.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("resourceRowDivider"))
            .ToList();
        window.Close();

        Assert.Equal(3, dividers.Count);
        Assert.Equal(1, dividers[0].BorderThickness.Bottom);
        Assert.Equal(1, dividers[1].BorderThickness.Bottom);
        Assert.Equal(0, dividers[2].BorderThickness.Bottom);
    });
}
