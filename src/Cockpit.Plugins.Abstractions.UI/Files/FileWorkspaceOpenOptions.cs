using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.Plugins.Abstractions.UI.Files;

public sealed record FileWorkspaceOpenOptions(FileRef File, string InstanceKey, string Title,
    string Location = "", bool Edit = false, bool CreateNew = false, string InitialText = "", bool Preview = false);
