namespace Cockpit.App.ViewModels;

// A selectable Claude model: display label plus the CLI/SDK `--model` value.
public sealed record ModelOption(string Label, string Value);
