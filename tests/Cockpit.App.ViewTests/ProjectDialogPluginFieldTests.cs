using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Plugins.Abstractions.Projects;
using FluentAssertions;

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

    [Fact]
    public void NoPluginContributesAField_TheSectionIsNotDrawn() => HeadlessAvalonia.Run(() =>
    {
        var window = new ProjectDialog { DataContext = new ProjectDialogViewModel() };
        window.Show();
        window.UpdateLayout();

        var heading = VisibleHeading(window);
        window.Close();

        heading.Should().BeNull("a cockpit with no tracker plugin must not hold an empty section open");
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

            heading.Should().NotBeNull();
            titles.Should().Contain("YouTrack project");
            box.Should().NotBeNull("the field is a box the operator can filter and type in");
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

            viewModel.PluginFields.Single().Value.Should().Be("PRIVATE");
            viewModel.ToProject().LinkedAs("youtrack.project").Should().Be("PRIVATE");
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

            viewModel.ToProject().LinkedAs("youtrack.project").Should().Be("AC", "the plugin queries with the tag, not the label");
        });
    }
}
