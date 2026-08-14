using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-772: the Manage-projects window now hosts the same <c>ProjectRowView</c> the Projects page's List layout does,
/// so its rows carry buttons where they used to carry nothing. That collides with the window's own row handlers —
/// a <c>Button</c> handles Click but not DoubleTapped, so a double-click on Start would run the button's action twice
/// and then open the editor over it. Measured against the real markup, since the collision only exists in the tree.
/// </summary>
[Collection("avalonia")]
public class ProjectRowActionsTests
{
    [Fact]
    public async Task DoubleClickingARowsOwnButton_DoesNotAlsoOpenTheEditor() =>
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var store = Substitute.For<IProjectStore>();
            store.LoadAsync(Arg.Any<CancellationToken>())
                .Returns(ProjectSettings.Empty.WithProject(Project.Create("Cockpit") with { DefaultProfileLabel = "personal" }));
            var dialogs = Substitute.For<ISessionDialogService>();
            var projects = new ProjectsViewModel(store, dialogs);
            await projects.LoadAsync();

            var dialog = new ProjectsDialog { DataContext = projects };
            dialog.Show();
            dialog.UpdateLayout();

            var start = dialog.GetVisualDescendants().OfType<Button>()
                .First(button => button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Start"));

            start.RaiseEvent(new TappedEventArgs(Control.DoubleTappedEvent, new PointerEventArgs(
                Control.DoubleTappedEvent, start, null!, start, default, 0, default, default)));

            // The editor is the one thing a double-click on the row itself does; from a button it must do nothing.
            await dialogs.DidNotReceive().ShowProjectDialogAsync(Arg.Any<Project?>(), Arg.Any<ISharedProjectSource?>());

            dialog.Close();
        });
}
