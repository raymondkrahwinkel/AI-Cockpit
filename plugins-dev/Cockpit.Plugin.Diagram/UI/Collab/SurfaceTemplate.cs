namespace Cockpit.Plugin.Diagram.Collab;

// AC-911: a template is nothing heavier than source text — Mermaid for a diagram, wireframe source for a
// wireframe — the same shape DiagramDocument.Sample already was. Adding one is a new record in a list, not a
// file, a manifest entry or a reload path.
internal sealed record SurfaceTemplate(string Name, string Source);
