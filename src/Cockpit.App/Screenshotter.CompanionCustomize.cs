using System.Text.Json;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Input;
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
    // The customize pop-out under the strip's options, as the mockup's "manage" scene, after the model's own checks: a
    // new plugin tool waits under Available, a withdrawn one keeps its place, the order survives a restart, and the
    // assistant stays first. "reordered" moves Status up with the arrow key first.
    private static Window _CompanionCustomizeScene(int width, int height, bool reordered)
    {
        List<CompanionStripItem> items =
        [
            .. _CompanionSceneItems().Take(2),
            new("cockpit.notifications", "Notifications", "Waiting for you", "", MaterialIconKind.BellOutline, null, () => _SceneBody(1), false),
            new("cockpit.status", "Status", "Cockpit status", "", MaterialIconKind.Pulse, null, () => _SceneBody(1), false),
        ];
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
        registry.Unregister("checks.run");
        model.SetEnabled(CompanionCustomizationModel.AssistantId, false);
        model.Move(CompanionCustomizationModel.AssistantId, 1);
        if (model.OnStrip.First() != CompanionCustomizationModel.AssistantId)
        {
            throw new InvalidOperationException("The assistant must stay first and enabled.");
        }

        // What the mockup shows: its seven items on the strip, three plugin items waiting under Available.
        var shown = _CompanionSceneItems();
        string?[] owners = [null, null, null, null, "depot", "local-ci", "github-pull-requests"];
        var display = new CompanionCustomizationModel();
        var extra = new (string Id, string Title, MaterialIconKind Icon, string? Owner)[]
        {
            ("cockpit.open", "Open Cockpit", MaterialIconKind.OpenInNew, null),
            ("usage-trend.meter", "Usage", MaterialIconKind.Speedometer, "usage-trend"),
            ("example-companion-tool.hello", "Example", MaterialIconKind.HandWave, "example"),
        };
        var registrations = shown.Select(item => new CompanionToolRegistration(item.Id, item.Title, _ => item.CreateView()) { IconKind = item.IconKind })
            .Concat(extra.Select(tool => new CompanionToolRegistration(tool.Id, tool.Title, _ => new TextBlock()) { IconKind = tool.Icon })).ToList();
        var ownerOf = shown.Select((item, index) => (item.Id, owners[index])).Concat(extra.Select(tool => (tool.Id, tool.Owner)))
            .ToDictionary(pair => pair.Item1, pair => pair.Item2);
        display.Merge(registrations, id => ownerOf.GetValueOrDefault(id));
        foreach (var id in owners.Select((owner, index) => owner is null ? null : shown[index].Id).OfType<string>())
        {
            display.SetEnabled(id, true);
        }

        var sources = new Dictionary<string, string>
        {
            ["depot"] = "Depot", ["local-ci"] = "Local CI", ["github-pull-requests"] = "GitHub pull requests",
            ["usage-trend"] = "Usage trend", ["example"] = "Example companion tool",
        };
        var panel = new CompanionCustomizePanel(display, _ => Task.CompletedTask, () => { }, () => { },
            id => display.PluginId(id) is { } owner ? sources[owner] : "Cockpit");
        if (reordered)
        {
            var row = panel.GetLogicalDescendants().OfType<Control>().Single(control => control.Tag as string == "cockpit.status");
            row.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up });
            if (display.OnStrip.ElementAt(1) != "cockpit.status")
            {
                throw new InvalidOperationException("The panel's arrow key did not reorder the strip.");
            }
        }

        var strip = new CompanionWindow(null);
        strip.SetTools([.. display.OnStrip.Select(id => shown.Single(item => item.Id == id))]);

        return _CompanionDeskScene(width, height, new CompanionDesk(strip, card => _StripItem(card, "more"), panel, 360));
    }
}
