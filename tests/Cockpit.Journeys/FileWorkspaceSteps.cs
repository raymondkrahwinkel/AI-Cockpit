using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Files;
using Cockpit.App.Views;
using Cockpit.Core.Workspaces;
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

    // A single click is a preview the next preview replaces; a double-click keeps it, and the next preview opens beside it.
    public static async Task PreviewReplacedUntilKeptAsync(JourneyHost cockpit, Func<string, bool, Task> open)
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var workspaces = cockpit.Cockpit.Workspaces;
            int FileTabs() => workspaces.Settings.Workspaces.Count(workspace => workspace.InstanceKey is not null);
            Task Close(string title) => workspaces.CloseWorkspaceCommand.ExecuteAsync(
                workspaces.Settings.Workspaces.Single(workspace => workspace.Name == title).Id);
            await open("notes/a.md", true);
            await open("notes/b.md", true);
            Assert.Equal(1, FileTabs());
            Assert.Equal("b.md", workspaces.Active?.Name);
            cockpit.Window.UpdateLayout();
            var tab = cockpit.Window.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Classes.Contains("workspaceTab") && border.DataContext is WorkspaceTabViewModel { IsPreview: true });
            var menu = tab.ContextMenu ?? throw new InvalidOperationException("The tab menu is missing.");
            menu.Open(tab);
            var copy = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Copy path"));
            copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await HeadlessAvalonia.SettleAsync();
            var clipboard = cockpit.Window.Clipboard ?? throw new InvalidOperationException("The clipboard is missing.");
            Assert.Equal("Depot / Journey / acme/cockpit / notes/b.md", await clipboard.TryGetTextAsync());
            menu.Close();
            await open("notes/b.md", false);
            await open("notes/c.md", true);
            Assert.Equal(2, FileTabs());
            await Close("b.md");
            await Close("c.md");
        });
    }

    // Someone else writes between opening and saving: nothing is written, Merge keeps both, the next save lands.
    public static async Task ChangedBehindYourBackThenMergedAsync(JourneyHost cockpit, Func<Task> open, Action changeFromOutside)
    {
        await open();
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var workspaces = cockpit.Cockpit.Workspaces;
            var entry = workspaces.Active ?? throw new InvalidOperationException("The file tab did not open.");
            var body = workspaces.ActivePluginBody as MarkdownFileWorkspace
                ?? throw new InvalidOperationException("The native file editor is missing.");
            await body.Ready;
            body.BeginEdit();
            body.Editor.TextEditor.Document.Insert(body.Editor.Text.Length - 1, ", edited");
            // AC-1972: Close others from Projects asks once for the set, then the file asks itself; Cancel keeps it.
            var asked = new List<string>();
            using (Window.WindowOpenedEvent.AddClassHandler<ConfirmationDialog>((dialog, _) =>
            {
                asked.Add((dialog.DataContext as ConfirmationDialogViewModel)?.Title ?? "");
                var answer = asked.Count == 1;
                Dispatcher.UIThread.Post(() => dialog.Close(answer));
            }))
            {
                var projects = workspaces.Settings.Workspaces.Single(workspace => workspace.Type == WorkspaceType.Projects).Id;
                await cockpit.Cockpit.CloseWorkspacesWithConfirmationAsync(BulkClose.Others, projects).WaitAsync(Until.Ceiling);
            }
            Assert.Equal(2, asked.Count);
            Assert.Matches(@"^Close [0-9]+ workspaces?\?$", asked[0]);
            Assert.Equal("Discard unsaved changes?", asked[1]);
            Assert.Contains(workspaces.Settings.Workspaces, workspace => workspace.Id == entry.Id);
            changeFromOutside();
            await body.SaveAsync();
            Assert.True(body.HasConflict);
            body.MergeSourceChanges();
            Assert.False(body.HasConflict);
            await body.SaveAsync();
            Assert.Null(workspaces.Tabs.Single(tab => tab.Id == entry.Id).Marker);
            await workspaces.CloseWorkspaceCommand.ExecuteAsync(entry.Id);
        });
    }
}
