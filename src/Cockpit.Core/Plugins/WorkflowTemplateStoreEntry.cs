namespace Cockpit.Core.Plugins;

// One workflow template a store offers (#69). A template is a flow as text — the same text the editor exports one to
// — so unlike a plugin there is no assembly to load, no code to consent to, and no hash to pin against a running
// process. What it needs from a store is what a plugin needs: identity, a description, and a checksum so that what
// arrives is what was published.
//
// It is not code, but it is not inert either: a flow can carry a shell command, and installing one is agreeing to
// have those steps on your canvas. So the store shows what a template *does* before it is installed, and an
// installed flow is never armed — reading it before arming it is the operator's own check.
//
// `Id`: Stable identity ("raymond.ticket-to-agent"), so a template can be recognised across versions.
// `Name`: What the store and the template picker show.
// `Description`: One line: what the flow does.
// `Author`: Who published it.
// `Version`: The published version, so an update to a template is a thing that can be seen.
// `Path`: Where the flow's JSON sits, relative to the index.
// `Sha256`: The checksum of that JSON, when the store publishes one — what arrived is then verifiably what was published.
// `Category`: The heading the picker files it under; defaults to the store's own name.
// `Requires`: The plugins whose steps this flow uses ("youtrack"), so a template that cannot run here says so instead of opening as a canvas of steps the editor cannot resolve.
public sealed record WorkflowTemplateStoreEntry(
    string Id,
    string Name,
    string? Description,
    string? Author,
    string? Version,
    string Path,
    string? Sha256 = null,
    string? Category = null,
    IReadOnlyList<string>? Requires = null);
