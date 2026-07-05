namespace Cockpit.App.ViewModels;

/// <summary>A selectable Claude model: display label plus the CLI/SDK <c>--model</c> value.</summary>
public sealed record ModelOption(string Label, string Value);
