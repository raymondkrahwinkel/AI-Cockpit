using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The "Where it is tracked" section in the real project editor (AC-317). Measured against the actual markup rather
/// than the view model alone, because everything that can go wrong here goes wrong in the binding: a section that
/// does not appear, or a box whose text never reaches the property that gets saved. A view-model test passes happily
/// through both.
/// </summary>
[Collection("avalonia")]
public class ProjectDialogPluginFieldTests
{
    private static ProjectFieldRegistration Field(params ProjectFieldOption[] options) =>
        new("youtrack.project", "YouTrack project", _ => Task.FromResult<IReadOnlyList<ProjectFieldOption>>(options))
        {
            Hint = "Which project in YouTrack this one is tracked in.",
            Placeholder = "AC",
            AllowsMultiple = true,
        };

    private static ProjectFieldRegistration SingleValueField(params ProjectFieldOption[] options) =>
        new("github.repository", "GitHub repository", _ => Task.FromResult<IReadOnlyList<ProjectFieldOption>>(options))
        {
            Placeholder = "owner/repo",
        };

    /// <summary>
    /// A view model with its fields' options already in, built <em>before</em> the dispatcher is entered. The load
    /// hands its fetch to a worker and resumes on the caller's context, so waiting for it from inside
    /// <see cref="HeadlessAvalonia.Run"/> would block the very thread the continuation needs — the deadlock the
    /// dialog itself avoids by never waiting on the load at all.
    /// </summary>
    private static async Task<ProjectDialogViewModel> ViewModelWithAsync(params ProjectFieldRegistration[] fields)
    {
        var viewModel = new ProjectDialogViewModel();
        foreach (var field in fields)
        {
            viewModel.PluginFields.Add(new ProjectPluginFieldViewModel(field, value: null));
        }

        await viewModel.LoadPluginFieldOptionsAsync();
        return viewModel;
    }

    /// <summary>The heading if it is actually on screen. A hidden control is still in the visual tree, so finding one there says nothing.</summary>
    private static TextBlock? VisibleHeading(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Text == "Where it is tracked" && text.IsEffectivelyVisible);

    private static AutoCompleteBox FieldBox(Window window) =>
        window.GetVisualDescendants().OfType<AutoCompleteBox>().First();

    private static IReadOnlyList<AutoCompleteBox> FieldBoxes(Window window) =>
        [.. window.GetVisualDescendants().OfType<AutoCompleteBox>()];

    // Scoped by DataContext, not by content or tooltip text: the "Anything else worth keeping" section above has
    // its own "+ Add row"/"Remove this row" buttons, and matching on text alone catches both.
    private static Button AddRowButton(Window window) =>
        window.GetVisualDescendants().OfType<Button>().First(button => button.DataContext is ProjectPluginFieldViewModel);

    private static IReadOnlyList<Button> RemoveRowButtons(Window window) =>
        [.. window.GetVisualDescendants().OfType<Button>().Where(button => button.DataContext is ProjectPluginFieldRowViewModel)];

    [Fact]
    public void NoPluginContributesAField_TheSectionIsNotDrawn() => HeadlessAvalonia.Run(() =>
    {
        var window = new ProjectDialog { DataContext = new ProjectDialogViewModel() };
        window.Show();
        window.UpdateLayout();

        var heading = VisibleHeading(window);
        window.Close();

        Assert.Null(heading);
    });

    [Fact]
    public async Task AContributedField_IsDrawnUnderItsOwnTitle()
    {
        var viewModel = await ViewModelWithAsync(Field());

        HeadlessAvalonia.Run(() =>
        {
            var window = new ProjectDialog { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            var heading = VisibleHeading(window);
            var titles = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible)
                .Select(text => text.Text)
                .ToList();
            var box = window.GetVisualDescendants().OfType<AutoCompleteBox>().FirstOrDefault();
            window.Close();

            Assert.NotNull(heading);
            Assert.Contains("YouTrack project", titles);
            Assert.NotNull(box);
        });
    }

    [Fact]
    public async Task TypingInTheBox_ReachesTheValueThatGetsSaved()
    {
        var viewModel = await ViewModelWithAsync(Field());

        HeadlessAvalonia.Run(() =>
        {
            // The binding mode is the whole point: one-way and the operator's typing never leaves the control, so the
            // project saves as unlinked while the box on screen says otherwise.
            var window = new ProjectDialog { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            FieldBox(window).Text = "PRIVATE";
            window.UpdateLayout();
            window.Close();

            Assert.Equal("PRIVATE", viewModel.PluginFields.Single().Value);
            Assert.Equal("PRIVATE", viewModel.ToProject().LinkedAs("youtrack.project"));
        });
    }

    [Fact]
    public async Task PickingAnOptionsDisplayName_SavesItsIdentifier()
    {
        var viewModel = await ViewModelWithAsync(Field(new ProjectFieldOption("AC", "AI-Cockpit — AC")));

        HeadlessAvalonia.Run(() =>
        {
            var window = new ProjectDialog { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            // What the control does when the operator picks from the list: it puts that option's display text in the box.
            FieldBox(window).Text = "AI-Cockpit — AC";
            window.UpdateLayout();
            window.Close();

            Assert.Equal("AC", viewModel.ToProject().LinkedAs("youtrack.project"));
        });
    }

    [Fact]
    public async Task ClickingAddRow_DrawsASecondBoxToPickInto()
    {
        // AC-884 (rebuilt after a bug report on the live app, 2026-08-18): a second identifier used to share the
        // first box's text via commas, and typing past a picked display name corrupted it — an AutoCompleteBox
        // already owns its own text on a pick. Each identifier now gets its own row instead.
        var viewModel = await ViewModelWithAsync(Field(
            new ProjectFieldOption("EWB", "EVE Workbench — EWB"),
            new ProjectFieldOption("AT", "Auth Tooling — AT")));

        HeadlessAvalonia.Run(() =>
        {
            var window = new ProjectDialog { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            FieldBoxes(window).Single().Text = "EVE Workbench — EWB";
            window.UpdateLayout();
            AddRowButton(window).Command!.Execute(null);
            window.UpdateLayout();
            FieldBoxes(window)[1].Text = "Auth Tooling — AT";
            window.UpdateLayout();
            window.Close();

            Assert.Equal(["EWB", "AT"], viewModel.ToProject().LinkedAsAll("youtrack.project"));
        });
    }

    [Fact]
    public async Task RemovingARow_DropsItsIdentifierAndItsBox()
    {
        var viewModel = await ViewModelWithAsync(Field(new ProjectFieldOption("EWB", "EVE Workbench — EWB")));
        viewModel.PluginFields.Single().Rows.Single().Text = "EWB";
        viewModel.PluginFields.Single().AddRowCommand.Execute(null);
        viewModel.PluginFields.Single().Rows[1].Text = "AT";

        HeadlessAvalonia.Run(() =>
        {
            var window = new ProjectDialog { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();
            Assert.Equal(2, FieldBoxes(window).Count);

            // The first row's own button is disabled rather than absent (Raymond, AC-884 review) — its column
            // stays reserved so the two rows' boxes line up, but nothing removes the row a single-value field
            // relies on.
            var removeButtons = RemoveRowButtons(window);
            Assert.False(removeButtons[0].IsEnabled);
            Assert.True(removeButtons[1].IsEnabled);
            removeButtons[1].Command!.Execute(removeButtons[1].CommandParameter);
            window.UpdateLayout();
            var remainingBoxes = FieldBoxes(window);
            window.Close();

            Assert.Single(remainingBoxes);
            Assert.Equal(["EWB"], viewModel.ToProject().LinkedAsAll("youtrack.project"));
        });
    }

    [Fact]
    public async Task AFieldThatDoesNotAllowMultiple_HasNoAddRowButton()
    {
        var viewModel = new ProjectDialogViewModel();
        viewModel.PluginFields.Add(new ProjectPluginFieldViewModel(SingleValueField(), value: null));
        await viewModel.LoadPluginFieldOptionsAsync();

        HeadlessAvalonia.Run(() =>
        {
            var window = new ProjectDialog { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();

            var addButtonVisible = window.GetVisualDescendants().OfType<Button>()
                .Where(button => button.DataContext is ProjectPluginFieldViewModel)
                .Any(button => button.IsEffectivelyVisible);
            var removeButtonVisible = RemoveRowButtons(window).Any(button => button.IsEffectivelyVisible);
            window.Close();

            Assert.False(addButtonVisible);
            Assert.False(removeButtonVisible);
        });
    }
}
