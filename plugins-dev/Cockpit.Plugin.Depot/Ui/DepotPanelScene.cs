using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Depot.UI;

// AC-2010: the whole Depot panel (header, tabs, Files, Notes, Inbox) on fixture data, for `--scene plugin:<name>`.
// The host is a proxy that answers every call with a default; only the channel carries fixture data.
internal sealed class DepotPanelScene : DispatchProxy
{
    private static readonly DepotBrowserProject _Project = new("eve-workbench/eve-workbench", "EVE Workbench", "Editor");
    private static readonly DateTimeOffset _Modified = new(2026, 10, 24, 16, 12, 0, TimeSpan.Zero);
    private IPluginUiChannel? _channel;

    public static Control Build(int tab)
    {
        var host = Create<ICockpitUiHost, DepotPanelScene>();
        if ((object)host is not DepotPanelScene proxy)
        {
            throw new InvalidOperationException("The scene host proxy was not created.");
        }

        var channel = new SceneChannel();
        proxy._channel = channel;
        var panel = new DepotBrowserPanel(host, [new("work", "Work", "https://depot.krahwinkel-it.nl")]);
        panel.GetLogicalDescendants().OfType<TabControl>().First().SelectedIndex = tab;
        return panel;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var type = targetMethod?.ReturnType;
        if (type is null || type == typeof(void))
        {
            return null;
        }

        if (type == typeof(IPluginUiChannel))
        {
            return _channel;
        }

        if (type == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var result = type.GetGenericArguments()[0];
            var value = result.IsGenericType && result.GetGenericArguments().Length == 1
                ? Array.CreateInstance(result.GetGenericArguments()[0], 0)
                : result.IsValueType ? Activator.CreateInstance(result) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))?.MakeGenericMethod(result).Invoke(null, [value]);
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private sealed class SceneChannel : IPluginUiChannel
    {
        public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement<object?>(_Answer(action), DepotChannel.Json));

        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => new Nothing();

        private static object? _Answer(string action) => action switch
        {
            DepotBrowserChannel.Projects => DepotBrowserAnswer<IReadOnlyList<DepotBrowserProject>>.Success([_Project]),
            DepotBrowserChannel.Tree => DepotBrowserAnswer<DepotBrowserTree>.Success(new(
            [
                new("Ideas/depot-sync.md", DepotBrowserKind.Document, 2048, _Modified, "text/markdown"),
                new("Ideas/voice-notes.md", DepotBrowserKind.Document, 912, _Modified.AddDays(-1), "text/markdown"),
                new("Mockups/AC-1749/v003.html", DepotBrowserKind.Artifact, 74845, _Modified.AddDays(2), "text/html"),
                new("Notes.md", DepotBrowserKind.Document, 68, _Modified, "text/markdown"),
            ], true)),
            DepotNotesChannel.Live => new DepotNotesPage("work", DepotInboxState.Ok,
            [
                new("eve-workbench/eve-workbench", "eve-workbench/eve-workbench", "Meetings/Weekly sync.md", "Weekly sync", "https://depot.krahwinkel-it.nl/n/7Kq3xP", true,
                    [new("Sanne"), new("Joris")]),
            ]),
            DepotInboxChannel.List => new DepotInboxPage(new("work", DepotInboxState.Ok, 1, new Dictionary<string, int>()),
            [
                new("m1", "FileShared", false, true, new DateTimeOffset(2026, 10, 28, 14, 2, 0, TimeSpan.Zero), null, "Joris shared a file", "https://depot.krahwinkel-it.nl/mailbox?id=m1"),
            ], new DateTimeOffset(2026, 10, 28, 14, 5, 0, TimeSpan.Zero), "raymond"),
            _ => null,
        };

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
