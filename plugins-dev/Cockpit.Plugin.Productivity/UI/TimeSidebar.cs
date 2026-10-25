using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Cockpit.Plugin.Productivity.UI;

internal sealed class TimeSidebar : UserControl
{
    private readonly ProductivityController _controller;
    private readonly Action _openWorkspace;
    private readonly StackPanel _content = new() { Spacing = 8 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public TimeSidebar(ProductivityController controller, Action openWorkspace)
    {
        _controller = controller;
        _openWorkspace = openWorkspace;
        Content = new Border
        {
            Padding = new Thickness(12, 10),
            Child = _content,
        }.Themed(Border.BackgroundProperty, "CockpitPanelBgBrush");
        _controller.Changed += _Render;
        _timer.Tick += (_, _) => _Render();
        _timer.Start();
        DetachedFromVisualTree += (_, _) =>
        {
            _timer.Stop();
            _controller.Changed -= _Render;
        };
        _Render();
    }

    private void _Render()
    {
        _content.Children.Clear();
        _content.Children.Add(new TextBlock { Text = "Tijd", FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 14 });
        if (_controller.Stopwatch is not { } stopwatch)
        {
            _content.Children.Add(Ui.Hint("Er loopt geen stopwatch."));
            var start = new Button { Name = "SidebarStart", Content = "Start", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            start.Click += (_, _) => _openWorkspace();
            _content.Children.Add(start);
            return;
        }

        var elapsed = DateTimeOffset.Now - stopwatch.StartedAt;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        _content.Children.Add(new TextBlock { Text = stopwatch.Description, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        _content.Children.Add(new TextBlock
        {
            Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}",
            FontFamily = new Avalonia.Media.FontFamily("Cascadia Mono,Consolas,monospace"),
            FontSize = 20,
        });
        if (!string.IsNullOrWhiteSpace(stopwatch.ProjectId))
        {
            _content.Children.Add(Ui.Tag(stopwatch.ProjectId));
        }

        var stop = new Button { Name = "StopStopwatch", Content = "Stop", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        stop.Classes.Add("Accent");
        stop.Click += async (_, _) => await _controller.StopStopwatchAsync();
        _content.Children.Add(stop);
    }
}
