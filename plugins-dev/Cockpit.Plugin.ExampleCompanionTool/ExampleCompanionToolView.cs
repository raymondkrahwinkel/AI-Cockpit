using Avalonia.Controls;
using Avalonia.Layout;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.Plugins.Abstractions.CompanionTools;

namespace Cockpit.Plugin.ExampleCompanionTool;

// The whole of what this example has to prove (AC-240): an icon a plugin drew, reacting to a click a plugin
// handled. A live status would need a data source and is already shown by the assistant tool (AC-238) — adding
// one here would just be a second thing to maintain for no new proof.
internal sealed class ExampleCompanionToolView : UserControl
{
    private const string ClickCountKey = "clickCount";

    private readonly TextBlock _clickCount;

    public ExampleCompanionToolView(ICompanionToolContext context)
    {
        var count = context.Storage.Get<int>(ClickCountKey);
        _clickCount = new TextBlock { Text = _Label(count), FontSize = 12, Opacity = 0.7 };

        var button = new Button
        {
            Content = new MaterialIcon { Kind = MaterialIconKind.HandWave, Width = 20, Height = 20 },
        };
        button.Click += (_, _) =>
        {
            count++;
            context.Storage.Set(ClickCountKey, count);
            _clickCount.Text = _Label(count);
        };

        Content = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { button, _clickCount },
        };
    }

    private static string _Label(int count) => count == 0 ? "Click the icon" : $"Clicked {count}x";
}
