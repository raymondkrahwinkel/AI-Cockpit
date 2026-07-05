namespace Cockpit.App.ViewModels;

/// <summary>A selectable Claude permission mode: display label plus the CLI <c>--permission-mode</c> value.</summary>
public sealed record PermissionModeOption(string Label, string Value);
