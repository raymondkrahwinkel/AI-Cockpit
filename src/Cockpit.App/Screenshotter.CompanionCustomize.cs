using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Layout;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Material.Icons;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    private static Window _CompanionCustomizeScene(bool reordered)
    {
        var items = _CompanionSceneItems();
        items.RemoveAt(2);
        items.Add(new("cockpit.notifications", "Notifications", "Waiting for you", "", MaterialIconKind.BellOutline, null, () => _SceneBody(1), false));
        items.Add(new("cockpit.status", "Status", "Cockpit status", "", MaterialIconKind.CircleOutline, null, () => _SceneBody(1), false));
        items.Add(new("checks.run", "Local checks", "Run local checks", "", MaterialIconKind.CheckCircleOutline, null, () => _SceneBody(1), false));
        items.Add(new("timer.start", "Task timer", "Time a task", "", MaterialIconKind.TimerOutline, null, () => _SceneBody(1), false));
        var tools = items.Select(item => new CompanionToolRegistration(item.Id, item.Title, _ => item.CreateView()) { IconKind = item.IconKind }).ToList();
        var registry = new CompanionToolRegistry();
        var storage = new PluginScenes.SceneStorage();
        foreach (var tool in tools)
        {
            registry.Register(tool, storage, NullCockpitSessionObserver.Instance, tool.Id == "checks.run" ? "local-checks" : null);
        }
        var model = new CompanionCustomizationModel();
        model.Merge(registry.Tools, registry.GetPluginId);
        var notices = 0;
        registry.Changed += (_, _) => notices += model.Merge(registry.Tools, registry.GetPluginId).Count;
        if (!model.Available.Contains("timer.start") || model.OnStrip.Contains("timer.start"))
        {
            throw new InvalidOperationException("A new plugin tool did not wait under Available.");
        }
        model.SetEnabled("checks.run", true);
        model.Move("checks.run", -1);
        var expected = model.OnStrip.ToArray();
        registry.Unregister("checks.run");
        registry.Unregister("checks.run");
        if (notices != 1)
        {
            throw new InvalidOperationException("Withdrawing a plugin must produce exactly one notice.");
        }
        registry.Register(tools.Single(tool => tool.Id == "checks.run"), storage, NullCockpitSessionObserver.Instance, "local-checks");
        if (!model.OnStrip.SequenceEqual(expected))
        {
            throw new InvalidOperationException("The returning plugin lost its place on the strip.");
        }
        var json = JsonSerializer.Serialize(model.Layout);
        var restored = new CompanionCustomizationModel(JsonSerializer.Deserialize<CompanionStripLayout>(json));
        restored.Merge(tools, _ => null);
        if (!restored.OnStrip.SequenceEqual(expected) || restored.PluginId("checks.run") != "local-checks")
        {
            throw new InvalidOperationException("The strip's order or owning plugin was lost across restart.");
        }
        model = restored;
        registry.Unregister("checks.run");
        var panel = new CompanionCustomizePanel(model, _ => Task.CompletedTask, () => { });
        if (reordered)
        {
            var up = panel.GetLogicalDescendants().OfType<Button>().Single(button => button.Tag as string == "cockpit.status:Move up");
            up.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (model.OnStrip.ElementAt(1) != "cockpit.status")
            {
                throw new InvalidOperationException("The panel's arrow did not reorder the strip.");
            }
        }
        model.SetEnabled(CompanionCustomizationModel.AssistantId, false);
        model.Move(CompanionCustomizationModel.AssistantId, 1);
        if (model.OnStrip.First() != CompanionCustomizationModel.AssistantId)
        {
            throw new InvalidOperationException("The assistant must stay first and enabled.");
        }
        var strip = new CompanionWindow(null);
        strip.SetTools([.. model.OnStrip.Select(id => items.Single(item => item.Id == id))]);
        var column = new StackPanel { Spacing = 12, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Left };
        column.Children.Add(strip.TakeCard());
        column.Children.Add(new CompanionPopOutCard
        {
            Width = 432,
            Body = panel,
        });
        var window = new Window { Width = 520, Height = 740, Content = column };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("CockpitWindowBgBrush"));
        return window;
    }
}
