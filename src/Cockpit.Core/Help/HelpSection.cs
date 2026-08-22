namespace Cockpit.Core.Help;

// AC-1033: the run of text under a heading carrying an explicit `{#id}`, which a deep link and a search hit
// both aim at — written by hand, so rewording a heading cannot move it. `Markdown` is the section's own
// source, for rendering and scrolling to one section; `Text` is the same as plain words, for matching.
public sealed record HelpSection(string Id, string Title, string Text, string Markdown);
