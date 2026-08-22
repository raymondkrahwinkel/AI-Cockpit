namespace Cockpit.Core.Help;

// One addressable section of an article: the run of text under a heading that carries an explicit `{#id}`.
// The id is written by hand and never derived from the heading text, because a deep link and a search hit
// both aim at it — deriving it would move every link the moment someone rewrote the heading, and it would
// move again in each translation, so a link would work in exactly one language (AC-1033).
public sealed record HelpSection(string Id, string Title, string Text);
