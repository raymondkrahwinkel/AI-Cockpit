using Avalonia;
using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Sample;

/// <summary>
/// The plugin's left-menu section, built in code (no compiled XAML — the sturdiest option for a plugin).
/// It persists a snippet in the host's per-plugin storage and injects it into the active session (or the
/// clipboard when none is active).
/// </summary>
internal sealed class SamplePanelControl : UserControl
{
    public SamplePanelControl(ICockpitHost host)
    {
        var input = new TextBox
        {
            Text = host.Storage.Get<string>("snippet") ?? "Hello from my plugin!",
            AcceptsReturn = true,
            MinHeight = 60,
        };
        var status = new TextBlock { FontSize = 11 };

        var send = new Button { Content = "Send to session" };
        send.Click += async (_, _) =>
        {
            var text = input.Text ?? string.Empty;
            host.Storage.Set("snippet", text);
            if (host.Actions.HasActiveSession)
            {
                await host.Actions.InjectIntoActiveSessionAsync(text);
                status.Text = "Sent to the active session.";
            }
            else
            {
                await host.Actions.SetClipboardTextAsync(text);
                status.Text = "No active session — copied to the clipboard.";
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(4),
            Spacing = 6,
            Children = { input, send, status },
        };
    }
}
