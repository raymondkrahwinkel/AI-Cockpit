namespace Cockpit.Plugins.Abstractions.Workspaces;

/// <summary>One open instance of a plugin's workspace type: the key it was opened with, and whether it is the preview tab or in its own window.</summary>
public sealed record OpenWorkspaceInstance(string InstanceKey, bool IsPreview, bool IsFloating);
