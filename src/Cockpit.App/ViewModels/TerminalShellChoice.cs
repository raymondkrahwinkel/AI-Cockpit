namespace Cockpit.App.ViewModels;

/// <summary>
/// One entry in the Options "default shell" picker (#AC-25): a human label ("PowerShell (pwsh)", "OS default
/// (bash)") and the value persisted to <see cref="Cockpit.Core.Terminal.TerminalSettings.Shell"/> — a shell id, or
/// empty for "OS default". A record so equality is by value, which lets the ComboBox reselect the saved choice after
/// a reload by matching on <see cref="Value"/>.
/// </summary>
public sealed record TerminalShellChoice(string Label, string Value);
