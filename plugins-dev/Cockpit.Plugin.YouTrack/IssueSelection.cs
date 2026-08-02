namespace Cockpit.Plugin.YouTrack;

// Finds the issue that survives a grid reload by identity (AC-299 bug 2). `YouTrackDialogControl`
// rebuilds its grid's `ItemsSource` as a brand-new `ObservableCollection&lt;YouTrackIssue&gt;` from
// freshly fetched records on every reload — a Refresh, but also the reload a Start work or Set state kicks off
// right after moving the very issue that was selected. Even though `YouTrackIssue` is a
// value-equality record, swapping the collection instance drops the DataGrid's selection outright: it does not
// go looking through the new items for one that happens to compare equal. And even if it did, an issue whose
// State a Start/Set state call just changed would no longer be equal to the one that was selected a moment
// earlier. Matching on `YouTrackIssue.IdReadable` — the issue's identity, not its current field
// values — is what survives the very change that triggered the reload.
internal static class IssueSelection
{
    public static YouTrackIssue? Restore(IEnumerable<YouTrackIssue> issues, string? idReadable) =>
        string.IsNullOrEmpty(idReadable)
            ? null
            : issues.FirstOrDefault(issue => string.Equals(issue.IdReadable, idReadable, StringComparison.Ordinal));
}
