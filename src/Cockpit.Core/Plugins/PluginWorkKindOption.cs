namespace Cockpit.Core.Plugins;

/// <summary>One work kind as the first-run wizard offers it (AC-511): the <see cref="Key"/> a store index matches
/// against in <see cref="PluginStoreEntry.WorkKind"/>, plus how it reads on screen.</summary>
/// <param name="Key">Matched against a store entry's work kind, case-insensitively.</param>
/// <param name="Label">The chooser's own wording.</param>
/// <param name="Description">One line under the label — what this kind of work involves, not which plugins it ticks.</param>
public sealed record PluginWorkKindOption(string Key, string Label, string Description);
