namespace Cockpit.App.ViewModels;

// How heavily a mark's lines are drawn (AC-375). Three steps rather than a number: an operator picking a line
// weight is choosing between "thinner than that" and "heavier than that", and a spinner would ask them a question
// in pixels that they would have to answer by trying it.
// Only the lines. A note's letters keep their own size — a label is there to be read, and at "thin" that is not a
// stylistic choice but an unreadable one (Raymond, 2026-07-27).
public enum MarkWeight
{
    Thin,

    // What every mark was drawn at before there was a choice, so an operator who never touches this sees no change.
    Medium,

    Thick,
}
