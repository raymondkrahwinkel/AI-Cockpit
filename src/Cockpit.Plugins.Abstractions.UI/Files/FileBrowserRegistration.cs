using Material.Icons;

namespace Cockpit.Plugins.Abstractions.UI.Files;

/// <summary>
/// A host-built file browser in the dock rail over this plugin's file source. <paramref name="Id"/> is the dock panel's
/// stable id; <paramref name="WorkspaceTypeId"/> is the type registered with AddFileWorkspaceType, which opens the files.
/// </summary>
public sealed record FileBrowserRegistration(string Id, string Title, MaterialIconKind IconKind, string WorkspaceTypeId);
