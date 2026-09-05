using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-892: the row's name/badge/cloud-icon line moved from a horizontal <c>StackPanel</c> to a
/// <c>Grid ColumnDefinitions="*,Auto"</c> — the same shape <c>ProjectCardView</c> already used for this content —
/// so the name trims in its own column instead of pushing the share badge out however wide it wants to be. A
/// StackPanel does not clip, so this would fail silently: nothing throws, the badge just drifts past the row.
/// </summary>
[Collection("avalonia")]
public class ProjectRowLayoutTests
{
    private const double RowWidth = 320;

    [Fact]
    public void ALongProjectName_DoesNotPushTheShareBadgePastTheRow() => HeadlessAvalonia.Run(() =>
    {
        var project = new Project("p1", "A project name long enough to fill the whole row and then quite a bit more");
        var card = new ProjectCardViewModel(project, originBadge: "◆ Depot", hasRemoteChanges: true);

        var window = new Window
        {
            Width = RowWidth,
            SizeToContent = SizeToContent.Height,
            Content = new ProjectRowView { DataContext = card },
        };

        window.Show();
        try
        {
            window.UpdateLayout();

            var badge = window.GetVisualDescendants().OfType<Border>()
                .First(border => border.Classes.Contains("sharedProjectBadge"));
            var topLeft = badge.TranslatePoint(default, window) ?? default;
            var right = topLeft.X + badge.Bounds.Width;

            Assert.True(right <= window.Bounds.Width + 1,
                $"badge right edge at {right:0.#} exceeds row width {window.Bounds.Width:0.#}");
        }
        finally
        {
            window.Close();
        }
    });
}
