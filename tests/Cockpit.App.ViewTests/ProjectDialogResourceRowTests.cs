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

    // Distinguished by its own Content text — every scene in this file has at most one resource row, the same
    // simplifying assumption VisibleTextContaining already relies on.
    private static CheckBox? VisibleSendAlongCheckBox(Window window) =>
        window.GetVisualDescendants().OfType<CheckBox>()
            .FirstOrDefault(box => box.IsEffectivelyVisible && Equals(box.Content, "Send along"));

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
        // A freshly added row defaults to Memory, the role the old standalone row always was.
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

        // A reference the probe could not resolve must be visible in the editor itself.
        Assert.NotNull(hint);
    });

    [Fact]
    public void AFreshlyAddedRow_ShowsNeitherOfTheHints() => HeadlessAvalonia.Run(() =>
    {
        // The negative half of both hints above, on one row and one layout pass: a row nobody has broken and
        // nobody has bound to this machine is the state every new row starts in, so a hint that leaks into it
        // would be the first thing an operator sees on an empty form.
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var notFound = VisibleTextContaining(window, "could not be found");
        var machineBound = VisibleTextContaining(window, "specific to this machine");
        window.Close();

        Assert.Null(notFound);
        Assert.Null(machineBound);
    });

    [Fact]
    public void AMachineBoundRow_ShowsTheMachineBoundHint() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        viewModel.ResourceRows.Single().Scope = ProjectResourceScope.Machine;
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var hint = VisibleTextContaining(window, "specific to this machine");
        window.Close();

        // An absolute, unshared path must be visible as such rather than only failing silently on another machine.
        Assert.NotNull(hint);
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

        // What is on screen is what the row holds, here as everywhere else in this app.
        Assert.Equal(typed, row.Reference);
        // A half-typed path is a path that does not exist — judging it while the operator is still writing it is
        // how the row turns red under their hands.
        Assert.False(row.IsBroken);
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
        // A reference typed and saved without leaving the box must not be dropped.
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
        // A divider must still separate this row from the next.
        Assert.Equal(1, dividers[0].BorderThickness.Bottom);
        Assert.Equal(1, dividers[1].BorderThickness.Bottom);
        // The last row must not draw a trailing hairline with nothing below it but the Add row button.
        Assert.Equal(0, dividers[2].BorderThickness.Bottom);
    });

    // --- AC-486: "Send along" only appears, and only means anything, for an Instructions row ---------------------------

    [Fact]
    public void ARoleOtherThanInstructions_NeverShowsTheSendAlongCheckbox() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var checkbox = VisibleSendAlongCheckBox(window);
        window.Close();

        // A freshly added row defaults to Memory, where "Send along" means nothing.
        Assert.Null(checkbox);
    });

    [Fact]
    public void AnInstructionsRow_ShowsTheSendAlongCheckboxAndItsHint() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        viewModel.ResourceRows.Single().Role = ProjectResourceRole.Instructions;
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var checkbox = VisibleSendAlongCheckBox(window);
        var hint = VisibleTextContaining(window, "leave it off for anything sensitive");
        window.Close();

        // Instructions is the one role this opt-in applies to.
        Assert.NotNull(checkbox);
        // The sensitivity and snapshot-timing consequences of ticking the box must be readable in the row itself,
        // not only on hover.
        Assert.NotNull(hint);
    });

    [Fact]
    public void CheckingSendAlong_ReachesTheRow() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows.Single();
        row.Role = ProjectResourceRole.Instructions;
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        VisibleSendAlongCheckBox(window)!.IsChecked = true;
        window.UpdateLayout();
        window.Close();

        Assert.True(row.SendsContent);
    });

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
    public void ASecretPathRow_ShowsTheWarningAndDisablesSendAlong() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows.Single();
        row.Role = ProjectResourceRole.Instructions;
        row.Reference = "~/.ssh/id_rsa";
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var hint = VisibleTextContaining(window, "looks like it holds credentials");
        var checkbox = VisibleSendAlongCheckBox(window);
        window.Close();

        // Melden (AC-612 effect 1): visible in the real markup, not only on the view model.
        Assert.NotNull(hint);
        // Inhoud (AC-612 effect 2): the checkbox is disabled, not merely unticked — nothing invites clicking it back on.
        Assert.NotNull(checkbox);
        Assert.False(checkbox!.IsEnabled);
    });

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

    [Fact]
    public void ASecretPathRow_HidesTheMachineBoundHintInFavourOfTheSecretWarning() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows.Single();
        row.Reference = "~/.ssh/id_rsa";
        row.Scope = ProjectResourceScope.Home;
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var scopeHint = VisibleTextContaining(window, "travels to everyone");
        var secretHint = VisibleTextContaining(window, "looks like it holds credentials");
        window.Close();

        // One row, one primary explanation (ShowsScopeLabel's own remarks) — the scope sentence steps aside.
        Assert.Null(scopeHint);
        Assert.NotNull(secretHint);
    });

    [Fact]
    public void ANonInstructionsSecretPathRow_ShowsTheSharingWarningWithoutMentioningContent() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows.Single();
        row.Role = ProjectResourceRole.Reference;
        row.Reference = "~/.aws/credentials";
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var hint = VisibleTextContaining(window, "looks like it holds credentials");
        window.Close();

        Assert.NotNull(hint);
        // Delen (AC-612 effect 3): a Reference/Memory row never offers "Send along" at all, so the sentence must not
        // claim content is withheld from a session — that sentence belongs to Instructions rows alone.
        Assert.DoesNotContain("its content will never be sent", hint!.Text);
    });

    [Fact]
    public void ANonSecretRow_NeverShowsTheSecretPathWarning() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        viewModel.ResourceRows.Single().Reference = "docs/CONVENTIONS.md";
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var hint = VisibleTextContaining(window, "looks like it holds credentials");
        window.Close();

        Assert.Null(hint);
    });

    /// <summary>
    /// AC-612 (Raymond, "no escape hatch"): a secret-shaped row inside the project folder must not offer "Make
    /// repo-relative" — clicking it would rewrite e.g. <c>SourceDirectory/.ssh/id_rsa</c> to <c>.ssh/id_rsa</c>, a
    /// shape the secret-path heuristic never evaluates (repo-relative is out of its scope), which would walk the row
    /// straight out of every check this ticket added through a button that already existed for an unrelated reason.
    /// </summary>
    [Fact]
    public void ASecretPathRowInsideTheProjectFolder_NeverOffersMakeRepoRelative() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows.Single();
        row.Reference = "~/.ssh/id_rsa";
        row.RepoRelativeFix = ".ssh/id_rsa"; // what ProjectDialogViewModel would compute were this row not secret
        var window = new ProjectDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var button = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => ReferenceEquals(b.Command, viewModel.ResourceRows.Single().ApplyRepoRelativeFixCommand) && b.IsEffectivelyVisible);
        window.Close();

        Assert.Null(button);
    });
}
