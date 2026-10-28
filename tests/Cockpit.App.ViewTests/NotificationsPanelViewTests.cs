using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Toasts;

namespace Cockpit.App.ViewTests;

[Collection("avalonia")]
public sealed class NotificationsPanelViewTests
{
    // Layer 2, view (AC-1853, mockup screens 04 and 09): a title over its message, one line without, the restart's own
    // group above Today, and Clear read takes only what was read.
    [Fact]
    public void ThePanel_ShowsTitleAndMessage_GroupsByLastSessionAndToday_AndClearsOnlyRead() => HeadlessAvalonia.Run(() =>
    {
        var host = new ToastHostViewModel((_, _) => { });
        host.Add("Its OAuth token expired.", ToastSeverity.Warning, [new ToastAction("Sign in", static () => { })], sticky: true,
            key: "mcp.youtrack", sourceId: "youtrack", title: "YouTrack MCP needs you to sign in again");
        host.Notifications.Items[0].KeptFrom(DateTimeOffset.Now.AddDays(-2));
        host.Add("Saved the layout.", ToastSeverity.Success, [], sticky: false);
        host.Add("You're on 0.131.2.", ToastSeverity.Information, [], sticky: false, title: "Cockpit 0.132.0 is available");
        host.Notifications.Items[0].MarkReadCommand.Execute(null);
        host.Notifications.Filter = NotificationFilter.All;

        var window = new Window { Content = new NotificationsPanelView { DataContext = host.Notifications }, Width = 420, Height = 760 };
        window.Show();
        window.UpdateLayout();
        var shown = window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text).ToList();

        Assert.Equal(
            ["Still open from your last session", "YouTrack MCP needs you to sign in again", "Its OAuth token expired.",
                "Clear read", "Today", "Cockpit 0.132.0 is available", "You're on 0.131.2.", "Saved the layout."],
            shown.Where(text => text is "Still open from your last session" or "Today" or "Clear read" || host.Notifications.Items.Any(item => item.Message == text || item.Title == text)));

        host.Notifications.VisibleGroups[1].ClearReadCommand.Execute(null);

        Assert.Equal(["Saved the layout.", "Its OAuth token expired."], host.Notifications.Items.Select(item => item.Message));
    });
}
