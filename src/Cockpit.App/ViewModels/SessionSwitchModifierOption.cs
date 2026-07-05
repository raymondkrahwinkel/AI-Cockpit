using Cockpit.Core.SessionSwitching;

namespace Cockpit.App.ViewModels;

/// <summary>A selectable session-switch modifier: display label plus the <see cref="SessionSwitchModifier"/> value.</summary>
public sealed record SessionSwitchModifierOption(string Label, SessionSwitchModifier Value);
