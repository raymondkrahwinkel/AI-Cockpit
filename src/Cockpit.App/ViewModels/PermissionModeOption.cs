namespace Cockpit.App.ViewModels;

// A selectable Claude permission mode: display label plus the CLI `--permission-mode` value.
public sealed record PermissionModeOption(string Label, string Value);
