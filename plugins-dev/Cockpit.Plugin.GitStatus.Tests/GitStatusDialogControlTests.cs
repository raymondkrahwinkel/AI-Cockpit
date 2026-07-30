using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.GitStatus.Tests;

/// <summary>
/// The state column's cell (#1): the word and the icon/colour a row's state renders as. The code comment on
/// <c>GitStatusDialogControl._BuildStateCell</c> promises the icon/colour are read straight off the row rather than
/// re-derived from <see cref="GitRepoStatus.StateText"/> — a promise nothing held open until now, so the two could
/// drift (a new state added to one and not the other) without a test noticing.
/// </summary>
[Collection("avalonia")]
public class GitStatusDialogControlTests
{
    [Fact]
    public void AnErroredRepo_ShowsTheErrorWordWithTheErrorIcon() => _AssertCell(
        new GitRepoStatus("/repo", "repo", string.Empty, 0, 0, 0, false, "not a git repository"),
        "error", MaterialIconKind.AlertOutline, "CockpitStatusErrorBrush");

    [Fact]
    public void ACleanRepo_ShowsTheCleanWordWithTheDoneIcon() => _AssertCell(
        new GitRepoStatus("/repo", "repo", "main", 0, 0, 0, true, null),
        "clean", MaterialIconKind.Check, "CockpitStatusDoneBrush");

    [Fact]
    public void ARepoWithChanges_ShowsTheChangesWordWithTheWaitingIcon() => _AssertCell(
        new GitRepoStatus("/repo", "repo", "main", 3, 0, 0, true, null),
        "changes", MaterialIconKind.Circle, "CockpitStatusWaitingBrush");

    private static void _AssertCell(GitRepoStatus status, string expectedText, MaterialIconKind expectedIcon, string expectedBrushKey) =>
        HeadlessAvalonia.Run(() =>
        {
            var harness = DialogHarness.OpenWithRow(status);

            var icon = harness.StateCellIcon(expectedText);
            var expectedBrush = harness.Brush(expectedBrushKey);
            harness.Close();

            Assert.Equal(expectedIcon, icon.Kind);
            Assert.Same(expectedBrush, icon.Foreground);
        });

    /// <summary>One dialog under test, its single row planted directly (no real git behind it), in a window its real size.</summary>
    private sealed class DialogHarness
    {
        private DialogHarness(Window window) => _window = window;

        private readonly Window _window;

        public static DialogHarness OpenWithRow(GitRepoStatus status)
        {
            // Zero configured repos, deliberately: with none, the dialog's own load returns before it ever calls
            // out to git, and the row under test is the one planted below rather than whatever a real read of a
            // made-up path would produce.
            var settings = new GitStatusSettings(new InMemoryPluginStorage());
            var dialog = new GitStatusDialogControl(settings, new FakeCockpitActions());

            var window = new Window { Width = 900, Height = 400, Content = dialog };
            window.Show();
            window.UpdateLayout();

            var rows = typeof(GitStatusDialogControl).GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(dialog) as ObservableCollection<GitRepoStatus>
                ?? throw new InvalidOperationException("GitStatusDialogControl no longer keeps its grid rows in _rows.");
            rows.Add(status);
            window.UpdateLayout();

            return new DialogHarness(window);
        }

        /// <summary>
        /// The state cell's icon — the <c>StackPanel</c> the column's <c>CellTemplate</c> built, found by the word it
        /// carries rather than position, so this cannot accidentally pick up the toolbar's own icon+label buttons
        /// (Refresh, Copy), which are the same shape.
        /// </summary>
        public MaterialIcon StateCellIcon(string stateWord)
        {
            var textBlock = _window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Text == stateWord)
                ?? throw new InvalidOperationException($"No cell showing \"{stateWord}\" was rendered.");
            var panel = textBlock.GetVisualParent() as StackPanel
                ?? throw new InvalidOperationException($"The \"{stateWord}\" text is not inside the state cell's StackPanel.");
            return panel.GetVisualDescendants().OfType<MaterialIcon>().First();
        }

        public IBrush? Brush(string key) => Application.Current?.TryFindResource(key, out var value) == true ? value as IBrush : null;

        public void Close() => _window.Close();
    }
}
