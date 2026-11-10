using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.UI.Files;
using Material.Icons;

namespace Cockpit.Plugin.RemoteFiles.UI;

// The host owns the browser, so this part only registers it and the settings view.
public sealed class RemoteFilesUi : ICockpitPluginUi
{
    public const string WorkspaceTypeId = "remote-files.markdown";

    // Read by Cockpit's `--screenshot --scene plugin:<name>` on a dev checkout only; not a plugin contract.
    public static IReadOnlyDictionary<string, Func<Control>> Scenes => RemoteFilesScenes.All;

    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddFileWorkspaceType(WorkspaceTypeId, "Markdown file");
        host.AddFileBrowser(new FileBrowserRegistration("remote-files.browser", "Remote files", MaterialIconKind.ServerNetwork, WorkspaceTypeId));
        host.AddSettings(() => new RemoteFilesSettingsControl(host.Channel,
            (title, content) => host.ShowDialogAsync(title, content, 520, 400)));
    }
}
