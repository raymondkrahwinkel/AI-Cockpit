using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.UI.Files;
using Material.Icons;

namespace Cockpit.Plugin.LocalFiles.UI;

// The host owns the browser, so this part only registers it and the settings view.
public sealed class LocalFilesUi : ICockpitPluginUi
{
    public const string WorkspaceTypeId = "local-files.markdown";

    // Read by Cockpit's `--screenshot --scene plugin:<name>` on a dev checkout only; not a plugin contract.
    public static IReadOnlyDictionary<string, Func<Control>> Scenes => LocalFilesScenes.All;

    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddFileWorkspaceType(WorkspaceTypeId, "Markdown file");
        host.AddFileBrowser(new FileBrowserRegistration("local-files.browser", "Local files", MaterialIconKind.FolderOutline, WorkspaceTypeId));
        host.AddSettings(() => new LocalFilesSettingsControl(host.Channel, host.GetProjectsAsync,
            (title, content) => host.ShowDialogAsync(title, content, 560, 480)));
    }
}
