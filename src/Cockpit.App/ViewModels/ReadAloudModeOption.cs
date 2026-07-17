using Cockpit.Core.Voice;

namespace Cockpit.App.ViewModels;

/// <summary>A selectable read-aloud rendering mode (#35): display label plus the <see cref="ReadAloudMode"/> value.</summary>
public sealed record ReadAloudModeOption(string Label, ReadAloudMode Value);
