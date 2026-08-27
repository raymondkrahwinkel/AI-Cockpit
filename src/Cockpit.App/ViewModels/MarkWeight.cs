namespace Cockpit.App.ViewModels;

// Three steps rather than a number: an operator picking a line weight is choosing between "thinner than that" and
// "heavier than that", and a spinner would ask them a question in pixels that they would have to answer by trying it
// (AC-375).
public enum MarkWeight
{
    Thin,

    // What every mark was drawn at before there was a choice, so an operator who never touches this sees no change.
    Medium,

    Thick,
}
