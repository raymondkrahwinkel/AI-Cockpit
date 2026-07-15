using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Sample;

/// <summary>
/// A starter Cockpit plugin. It contributes a left-menu button that opens a dialog; add a settings view
/// with <c>host.AddSettings(...)</c> (opened from the manager's gear), register your own services in
/// <see cref="ConfigureServices"/>, and persist settings via <c>host.Storage</c>. See docs/plugins/PLUGIN-SDK.md.
/// </summary>
public sealed class SamplePlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "sample",
        DisplayName: "Sample",
        Version: "1.0.0",
        Author: "You",
        Description: "A starter Cockpit plugin — replace this with your own.");

    public void ConfigureServices(IServiceCollection services)
    {
        // Register your own services here (runs before the host container is built).
    }

    public void Initialize(ICockpitHost host)
    {
        // A left-menu button that opens the plugin's content in a dialog. For plugin settings, also call
        // host.AddSettings(() => new YourSettingsControl(...)) — it appears behind the gear in the manager.
        host.AddSideMenuButton("Sample", () => _ = host.ShowDialogAsync("Sample", () => new SamplePanelControl(host)));

        // The other contribution points, in case one of them fits better than a button — see
        // docs/plugins/PLUGIN-SDK.md for each, and plugins-dev/ for a worked example of every one:
        //
        //   host.AddSessionHeaderItem(session => new YourIndicator(host, session));
        //       A small control in EVERY session's header, handed that session's own context (its working
        //       directory and its output stream). For status belonging to one session — not to the cockpit.
        //
        //   host.AddConversationPicker(new ConversationPickerRegistration("Search history", PickAsync));
        //       Lends your history-browsing to the New-session dialog's "resume by session id" field.
        //
        //   host.AddShortcut(new PluginShortcut("sample.open", "Sample", "Shift+S", OpenSample));
        //       A gesture plus a command-palette entry, listed alongside the app's own shortcuts.
        //
        //   host.AddWidget(new WidgetRegistration("sample.widget", "Sample", context => new YourWidget(context))
        //   {
        //       Icon = "🧩", Description = "What it shows.", DefaultColumnSpan = 6, DefaultRowSpan = 4,
        //       CreateConfigView = context => new YourWidgetSettings(context),   // omit it and the pane has no ⚙
        //   });
        //       A pane on a Dashboard workspace. Each placed instance gets its own IWidgetContext — storage
        //       scoped to that instance, so two of your widgets on one dashboard keep separate config. Publish
        //       with "category": "Widgets" to land in the store's Widgets section.
        //
        //   host.AddSideMenuSection("Sample", () => new YourSectionControl());   // inline, always visible
        //   host.AddSessionProvider(registration);   // your own model/CLI as a selectable provider
        //   _ = host.AddMcpServer(contribution);     // put an MCP server in the shared registry
        //   var profiles = await host.GetProfilesAsync();   // where each provider keeps its state on disk
        //   host.Sessions                            // read surface: the selected session, and every session's output
    }

    public void Dispose()
    {
    }
}
