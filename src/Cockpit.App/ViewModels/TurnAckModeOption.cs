using Cockpit.Core.Voice;

namespace Cockpit.App.ViewModels;

/// <summary>A selectable turn-start acknowledgement mode (AC-99): display label plus the <see cref="TurnAckMode"/> value.</summary>
public sealed record TurnAckModeOption(string Label, TurnAckMode Value);
