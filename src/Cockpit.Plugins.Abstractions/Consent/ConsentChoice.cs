namespace Cockpit.Plugins.Abstractions.Consent;

/// <summary>
/// One question on a consent card that the operator answers by picking (AC-1714): "with which session", "what the
/// agent may do". Set on <see cref="ConsentRequest.Choices"/>; the picked values come back in
/// <see cref="ConsentDecision.Chosen"/> under <see cref="Key"/>.
/// </summary>
/// <param name="Key">
/// The key the picked values come back under. Unique within one request.
/// </param>
/// <param name="Label">
/// The question as the card shows it — "With", "What the agent may do".
/// </param>
/// <param name="Options">
/// What can be picked. An approval always carries at least one enabled option per choice, so an empty list makes the request unapprovable.
/// </param>
/// <param name="AllowMultiple">
/// False: exactly one option, shown as a list to pick from. True: one or more, shown as checkboxes.
/// </param>
public sealed record ConsentChoice(string Key, string Label, IReadOnlyList<ConsentOption> Options, bool AllowMultiple = false);

/// <summary>
/// One pickable answer to a <see cref="ConsentChoice"/>.
/// </summary>
/// <param name="Value">
/// What comes back in <see cref="ConsentDecision.Chosen"/> when this option is picked.
/// </param>
/// <param name="Label">
/// The option as the card shows it.
/// </param>
/// <param name="Detail">
/// An optional second line under the label, saying what picking it allows.
/// </param>
/// <param name="Selected">
/// Picked when the card opens. The operator still has to approve; nothing is granted by a default alone.
/// </param>
/// <param name="Enabled">
/// False shows the option but refuses it: an approval that names a disabled option is a denial.
/// </param>
public sealed record ConsentOption(string Value, string Label, string? Detail = null, bool Selected = false, bool Enabled = true);
