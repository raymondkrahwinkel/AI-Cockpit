namespace Cockpit.Core.Help;

// One addressable section of an article: the run of text under a heading that carries an explicit `{#id}`.
// The id is written by hand and never derived from the heading text, because a deep link and a search hit
// both aim at it — deriving it would move every link the moment someone rewrote the heading, and it would
// move again in each translation, so a link would work in exactly one language (AC-1033).
// `Markdown` is the section's own source, heading included, so the window can render one control per section
// and scroll to the right one — a deep link that lands at the top of a long page has answered the wrong
// question. `Text` is the same thing as plain words, for the search to match and to quote a snippet from.
public sealed record HelpSection(string Id, string Title, string Text, string Markdown);
