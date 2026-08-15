using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Cockpit.App.Controls;

namespace Cockpit.App.ViewTests;

// Cockpit serves no external UI-Automation tree below its windows (NoChildrenWindowPeer): it has its own in-app
// voice assistant, and exposing one lets an external UIA client realise a COM node per control that Avalonia never
// releases on detach (issue #8240), pinning every closed pane's transcript. This pins the fix: a window using the
// peer reports no automation children, so a client can never descend into — and thus never pin — the contents.
[Collection("avalonia")]
public sealed class WindowAutomationSuppressionTests
{
    private sealed class _SuppressedWindow : Window
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new NoChildrenWindowPeer(this);
    }

    [Fact]
    public async Task NoChildrenWindowPeer_HidesEveryDescendantFromAutomation()
    {
        await HeadlessAvalonia.RunAsync(() =>
        {
            var window = new _SuppressedWindow
            {
                Width = 400,
                Height = 300,
                Content = new StackPanel
                {
                    Children =
                    {
                        new Button { Content = "one" },
                        new TextBlock { Text = "two" },
                    },
                },
            };
            window.Show();
            window.UpdateLayout();

            // What an external UIA client's tree walk starts from — the window's root peer. It must expose no
            // children, so the walk realises no descendant peers (and creates no COM nodes to pin them).
            var rootPeer = ControlAutomationPeer.CreatePeerForElement(window);
            Assert.Empty(rootPeer.GetChildren());

            return Task.CompletedTask;
        });
    }
}
