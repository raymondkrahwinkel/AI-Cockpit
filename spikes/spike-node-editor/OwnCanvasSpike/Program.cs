using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

namespace OwnCanvasSpike;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<SpikeApp>()
            .UsePlatformDetect()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
}

internal sealed class SpikeApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var canvas = new FlowCanvas();

            // The workflow from the braindump, as far as a canvas can show it: a trigger feeding two actions.
            var trigger = canvas.AddNode("Trigger: pr.opened", 60, 60, inputs: 0, outputs: 1);
            var notify = canvas.AddNode("Action: notify", 380, 40, inputs: 1, outputs: 1);
            var delegateTask = canvas.AddNode("Action: delegate", 380, 190, inputs: 1, outputs: 1);

            canvas.Connect(trigger.Pins.First(), notify.Pins.First());
            canvas.Connect(trigger.Pins.First(), delegateTask.Pins.First());

            var status = new TextBlock
            {
                Margin = new Thickness(10),
                FontSize = 12,
                Foreground = Brushes.Gray,
                Text = $"{canvas.Describe()} — drag a node by its header, drag a wire out of a pin, wheel to zoom, drag the background to pan.",
            };

            var root = new DockPanel();
            DockPanel.SetDock(status, Dock.Bottom);
            root.Children.Add(status);
            root.Children.Add(canvas);

            desktop.MainWindow = new Window
            {
                Title = "Own flow canvas spike (#69)",
                Width = 1000,
                Height = 700,
                Content = root,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
