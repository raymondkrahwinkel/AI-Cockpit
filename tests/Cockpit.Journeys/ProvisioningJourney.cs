using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App;
using Cockpit.App.Converters;
using Cockpit.App.ViewTests;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Provisioning;

namespace Cockpit.Journeys;

// AC-1506, the unattended start (route D): `--provision` to a manifest server on this machine, as Program.Main runs it
// before any plugin loads. The cockpit comes up set up, past the wizard, and the one thing left is the provider sign-in.
[Collection(JourneyCollection.Alone)]
public sealed class ProvisioningJourney
{
    private const string Token = "journey-one-time-token";

    [Fact]
    public async Task An_unattended_start_comes_up_set_up_without_the_wizard_and_leaves_only_the_provider_sign_in()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapGet("/manifest", () => Results.Text(Manifest, "application/vnd.cockpit.provision+json"));
        await server.StartAsync();
        var url = $"{server.Urls.Single()}/manifest";

        try
        {
            await using var cockpit = JourneyHost.Desktop(stateRoot =>
                Program.Provisioning = ProvisioningEntry.ProvisionAsync($"{url}?t={Token}", false, stateRoot, NullLogger.Instance).GetAwaiter().GetResult());
            Assert.Null(Program.Provisioning!.Failure);

            await cockpit.StartDesktopAsync();
            await HeadlessAvalonia.RunAsync(() => Until.CollectionHolds(cockpit.Cockpit.Toasts, () => cockpit.Cockpit.Toasts.Count > 0));

            var toast = Assert.Single(cockpit.Cockpit.Toasts);
            Assert.Equal("This cockpit was set up for you. Still to do: sign in to claude-provider on this machine.", toast.Message);
            Assert.Equal(FirstRunWizardVersion.Current, await cockpit.Services.GetRequiredService<IFirstRunWizardStateStore>().GetCompletedVersionAsync());
            Assert.Contains("Acme dev", (await cockpit.Services.GetRequiredService<ISessionProfileStore>().LoadAsync()).Select(profile => profile.Label));

            var file = JsonNode.Parse(await File.ReadAllTextAsync(CockpitConfigPath.For(cockpit.StateRoot)))!;
            Assert.Equal(("false", "15", "true"), (file["Notifications"]!["LocalEnabled"]!.ToString(), file["Terminal"]!["FontSize"]!.ToString(), file["SessionBehavior"]!["AutoCloseOnExit"]!.ToString()));
            Assert.Null(file["Layout"]);
            Assert.Contains(Program.Provisioning!.Report, line => line is { Path: "settings.layout.themeMode", Reason: "skipped-unknown" });

            // Used up only now that everything was applied, and kept as its hash alone.
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Token)));
            Assert.Contains(hash, await File.ReadAllTextAsync(Path.Combine(cockpit.StateRoot, "provision-tokens-used.txt")));
            Assert.All(Directory.EnumerateFiles(cockpit.StateRoot, "*.json", SearchOption.AllDirectories), path => Assert.DoesNotContain(Token, File.ReadAllText(path)));

            // AC-1507: the locked font size is read-only in Options, and a save through the view model leaves it as Acme set it.
            await HeadlessAvalonia.RunAsync(async () =>
            {
                await Until.Holds(cockpit.Cockpit, () => cockpit.Cockpit.ManagedSettings.ContainsKey("Terminal.FontSize"));
                Assert.Equal("Managed by Acme", ManagedSettingConverter.Label.Convert(cockpit.Cockpit.ManagedSettings, typeof(string), "Terminal.FontSize", CultureInfo.InvariantCulture));
                cockpit.Cockpit.TerminalFontSize = 20;
                await cockpit.Cockpit.SaveTerminalSettingsCommand.ExecuteAsync(null);
            });
            Assert.Equal("15", JsonNode.Parse(await File.ReadAllTextAsync(CockpitConfigPath.For(cockpit.StateRoot)))!["Terminal"]!["FontSize"]!.ToString());

            // Applied again on top of the running cockpit: set, not added, and nothing to say.
            var again = (await ProvisioningEntry.ProvisionAsync(url, true, cockpit.StateRoot, NullLogger.Instance))!;
            Assert.Null(again.Failure);
            Assert.DoesNotContain(again.Report, line => line.Reason is ProvisionManifestApplier.Applied or ProvisionManifestApplier.ReplacedManaged or ProvisionManifestApplier.KeptLocal);
            Assert.Single(await cockpit.Services.GetRequiredService<ISessionProfileStore>().LoadAsync(), profile => profile.Label == "Acme dev");
        }
        finally
        {
            Program.Provisioning = null;
        }
    }

    // The link configures Depot and does not log in: the toast leads to the plugin's own sign-in, and the link stays usable.
    [Fact]
    public async Task An_interactive_connection_leaves_the_link_usable_and_its_toast_offers_the_plugin_settings_to_sign_in()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapGet("/manifest", () => Results.Text(InteractiveManifest, "application/vnd.cockpit.provision+json"));
        await server.StartAsync();
        var url = $"{server.Urls.Single()}/manifest?t={Token}";

        try
        {
            await using var cockpit = JourneyHost.Desktop(stateRoot =>
                Program.Provisioning = ProvisioningEntry.ProvisionAsync(url, false, stateRoot, NullLogger.Instance).GetAwaiter().GetResult());
            await cockpit.StartDesktopAsync();
            await HeadlessAvalonia.RunAsync(() => Until.CollectionHolds(cockpit.Cockpit.Toasts, () => cockpit.Cockpit.Toasts.Count > 0));

            var toast = Assert.Single(cockpit.Cockpit.Toasts);
            Assert.Equal("This cockpit was set up for you. Still to do: add the connection to https://depot.acme.nl in the Depot settings and sign in there.", toast.Message);
            Assert.True(toast.HasAction);
            Assert.Equal("Open Depot settings", toast.ActionLabel);
            Assert.False(File.Exists(Path.Combine(cockpit.StateRoot, "provision-tokens-used.txt")));
            Assert.Null((await ProvisioningEntry.ProvisionAsync(url, true, cockpit.StateRoot, NullLogger.Instance))!.Failure);
        }
        finally
        {
            Program.Provisioning = null;
        }
    }

    private const string InteractiveManifest = """
        {
          "format": "cockpit.provision", "formatVersion": 1,
          "issuer": { "kind": "static" },
          "connections": [ { "id": "acme-depot", "plugin": "depot", "required": true,
                             "config": { "url": "https://depot.acme.nl", "credential": "interactive" } } ]
        }
        """;

    private const string Manifest = """
        {
          "format": "cockpit.provision", "formatVersion": 1,
          "issuer": { "kind": "static", "organization": { "id": "acme", "name": "Acme" } },
          "plugins": { "required": [ { "id": "clock" }, { "id": "git-status" } ] },
          "settings": {
            "notifications.localEnabled":      { "value": false,   "mode": "default", "by": "org" },
            "terminal.fontSize":               { "value": 15,      "mode": "locked",  "by": "org" },
            "sessionBehavior.autoCloseOnExit": { "value": true,    "mode": "default", "by": "org" },
            "layout.themeMode":                { "value": "Light", "mode": "default", "by": "org" }
          },
          "profiles": { "items": [
            { "id": "acme-dev", "label": "Acme dev", "by": "org", "provider": { "id": "claude-provider", "model": "claude-sonnet-5-5" } } ] },
          "firstRun": { "workKind": "ontwikkelen" }
        }
        """;
}
