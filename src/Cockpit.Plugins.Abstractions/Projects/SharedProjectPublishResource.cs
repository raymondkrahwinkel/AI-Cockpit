namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// One resource row offered for publishing (AC-620) — the write-side mirror of <see cref="SharedProjectBindingResource"/>.
/// Unlike <see cref="SharedProjectDefinitionEdit"/>'s claimed-field-only scope, a first publish has no existing
/// remote definition to carry rows through from, so every row the local project has is offered here; the source's
/// own portability/secrecy rules (AC-244/AC-612) decide, at write time, which of them actually cross.
/// </summary>
/// <param name="Role">What a session does with this row — same plain-string idiom as <see cref="SharedProjectBindingResource.Role"/>.</param>
/// <param name="Reference">Where this resource is, or what names it — exactly as the local project stores it, unfiltered.</param>
/// <param name="Label">What the operator called this row. Null when they never named it.</param>
public sealed record SharedProjectPublishResource(string Role, string Reference, string? Label);
