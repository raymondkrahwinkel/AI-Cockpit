namespace Cockpit.App.Views;

// AC-1265: how MarkdownView's Copy reaches the whole of a code block that was split across transcript rows,
// without the control having to know what a transcript row is.
internal interface ISpannedCodeSource
{
    /// <summary>
    /// The whole code of the block this row is a fragment of, or empty when the row is not part of a split
    /// block — which is what leaves Copy reading the block it sits under.
    /// </summary>
    string SpannedCodeText { get; }
}
