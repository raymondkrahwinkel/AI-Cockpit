using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Sample.UI;

// The plugin's dialog, built in code (no compiled XAML — the sturdiest option for a plugin). It keeps a snippet in
// the plugin's storage, asks the backend part for a default over the channel, and sends the snippet to the
// selected session, or to the clipboard when none is selected.
internal sealed class SamplePanelControl : UserControl
{
    public SamplePanelControl(ICockpitUiHost host)
    {
        var input = new TextBox
        {
            Text = host.Storage.Get<string>("snippet") ?? string.Empty,
            AcceptsReturn = true,
            MinHeight = 60,
        };
        var status = new TextBlock { FontSize = 11 };

        var greet = new Button { Content = "Ask the backend part" };
        greet.Click += async (_, _) =>
        {
            var answer = await host.Channel.InvokeAsync("greeting", JsonSerializer.SerializeToElement(new { }));
            input.Text = answer.GetProperty("text").GetString();
        };

        var send = new Button { Content = "Send to session" };
        send.Click += async (_, _) =>
        {
            var text = input.Text ?? string.Empty;
            host.Storage.Set("snippet", text);
            if (host.ActivePaneId is { } paneId)
            {
                await host.SendToSessionAsync(paneId, text);
                status.Text = "Sent to the selected session.";
            }
            else
            {
                await host.SetClipboardTextAsync(text);
                status.Text = "No session selected — copied to the clipboard.";
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(4),
            Spacing = 6,
            Children = { input, greet, send, status },
        };
    }
}
