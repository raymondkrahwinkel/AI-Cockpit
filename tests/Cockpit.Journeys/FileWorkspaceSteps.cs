using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Files;
using Cockpit.App.ViewTests;

namespace Cockpit.Journeys;

internal static class FileWorkspaceSteps
{
    public static async Task CreateSaveAndFloatAsync(JourneyHost cockpit, Func<Task> open)
    {
        await open();
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var workspaces = cockpit.Cockpit.Workspaces;
            var entry = workspaces.Active ?? throw new InvalidOperationException("The file tab did not open.");
            var body = workspaces.ActivePluginBody as MarkdownFileWorkspace
                ?? throw new InvalidOperationException("The native file editor is missing.");
            await body.Ready;
            Assert.False(body.Editor.IsReadOnly);
            body.Editor.TextEditor.Document.Insert(body.Editor.Text.Length, "Written in Cockpit.");
            var document = body.Editor.TextEditor.Document;
            var undo = document.UndoStack;
            await workspaces.SelectWorkspaceCommand.ExecuteAsync(workspaces.Tabs.First(tab => tab.Id != entry.Id).Id);
            cockpit.Window.UpdateLayout();
            var tab = cockpit.Window.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Classes.Contains("workspaceTab") && border.DataContext is WorkspaceTabViewModel model && model.Id == entry.Id);
            var menu = tab.ContextMenu ?? throw new InvalidOperationException("The tab menu is missing.");
            menu.Open(tab);
            menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Pop out"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await HeadlessAvalonia.SettleAsync();
            Assert.True(workspaces.Settings.Workspaces.Single(workspace => workspace.Id == entry.Id).IsFloating);
            Dispatcher.UIThread.RunJobs();
            await workspaces.DockBackAsync(entry.Id);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(body, workspaces.ActivePluginBody);
            Assert.Same(document, body.Editor.TextEditor.Document);
            Assert.Same(undo, body.Editor.TextEditor.Document.UndoStack);
            await body.SaveAsync();
            Assert.Null(workspaces.Tabs.Single(tab => tab.Id == entry.Id).Marker);
            await open();
            Assert.Same(body, workspaces.ActivePluginBody);
            Assert.Equal("# Journey\n\nWritten in Cockpit.", body.Editor.Text);
            await workspaces.CloseWorkspaceCommand.ExecuteAsync(entry.Id);
        });
    }
}
