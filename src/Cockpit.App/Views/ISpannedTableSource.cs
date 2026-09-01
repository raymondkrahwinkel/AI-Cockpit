namespace Cockpit.App.Views;

// AC-1272: how MarkdownView shares one table's column widths across the transcript rows it was split
// across (route A2), without the control having to know what a transcript row is.
internal interface ISpannedTableSource
{
    /// <summary>
    /// The whole markdown of the table this row is a fragment of, or empty when the row is not part of a
    /// split table — which is what leaves an ordinary, unsplit table's columns auto-sized as before.
    /// </summary>
    string SpannedTableText { get; }
}
