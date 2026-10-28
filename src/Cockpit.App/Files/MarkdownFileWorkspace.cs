using Avalonia.Controls;
using Cockpit.App.Views.Editor;
using Cockpit.Plugins.Abstractions.UI.Files;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Files;

internal sealed class MarkdownFileWorkspace : UserControl
{
    internal MarkdownFileWorkspace(IWorkspaceContext context, FileWorkspaceOpenOptions options,
        FileSourceChannelClient client, Func<Task<bool>> confirmDiscard)
    {
        _ = (context, options, client, confirmDiscard);
        Content = Editor;
    }
    internal MarkdownEditor Editor { get; } = new();
    internal Task Ready { get; } = Task.FromException(new NotSupportedException("The file workspace is not implemented yet."));
    internal Task SaveAsync() => throw new NotSupportedException("Saving is not implemented yet.");
}
