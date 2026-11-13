using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Infrastructure.Vault;
using Cockpit.Plugins.Abstractions.Vault;

namespace Cockpit.App;

// AC-2347: the vault's screens with made-up facts: Options › Vault, its closed state, the status-bar panel and the consent card.
internal static partial class Screenshotter
{
    private static OptionsDialog _VaultOptions(VaultAvailability availability)
    {
        var cockpit = new CockpitViewModel();
        _VaultSample(cockpit.Vault, availability);
        var dialog = new OptionsDialog { DataContext = cockpit };
        var nav = dialog.FindControl<ListBox>("CategoryNav")
            ?? throw new InvalidOperationException("The Options dialog has no 'CategoryNav' sidebar to select on.");
        nav.SelectedItem = nav.Items.OfType<ListBoxItem>().First(item => item.Tag as string == "vault");
        return dialog;
    }

    private static MainWindow _VaultStatusBarScene()
    {
        var cockpit = new CockpitViewModel();
        cockpit.DelegatedTasks.Tasks.Clear();
        cockpit.DelegatedTasks.Groups.Clear();
        cockpit.TestRuns.Apply([]);
        cockpit.ResourceCpu = "CPU 21%  ·  RAM ";
        cockpit.ResourceMemory = "1.840 MB";
        cockpit.ResourceSessions = "3 sessions";
        cockpit.Worktrees.Count = 0;
        _VaultSample(cockpit.Vault, VaultAvailability.Available);
        cockpit.Vault.OpenRequests = 1;
        return new MainWindow { DataContext = cockpit };
    }

    private static void _OpenVaultPanel(Window window)
    {
        window.GetVisualDescendants().OfType<VaultStatusMeter>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static Window _VaultConsent(int width, int height) => new()
    {
        Width = width,
        Height = height,
        Content = _WindowConsentChoices(VaultMcpTools.Card(
            "Session 3", "pane-3", @"D:\Projects\Webshop", ["npm", "run", "deploy:staging"],
            [("DEPLOY_TOKEN", "op://Agents/deploy-bot/token")],
            "I need the deploy token to push the staging build after the tests pass.", "Webshop")),
    };

    private static Task _Done() => Task.CompletedTask;

    private static void _VaultSample(VaultOverviewViewModel vault, VaultAvailability availability)
    {
        if (availability != VaultAvailability.Available)
        {
            vault.Apply(availability, [], [], [], []);
            return;
        }

        var items = new VaultItemRow[]
        {
            new("staging-db / password", "op://Agents/staging-db/password", "2 · in use", true),
            new("deploy-bot / token", "op://Agents/deploy-bot/token", "none", false),
            new("staging-api / client-secret", "op://Agents/staging-api/client-secret", "none", false),
        };
        var providers = new VaultProviderRow[]
        {
            new("1Password", "Service account cockpit-agents · reads one vault: Agents", "Connected", VaultProviderState.Connected, items),
            new("Bitwarden", "Machine account cockpit-agents · Secrets Manager project Agents", "Connected", VaultProviderState.Connected,
                [new VaultItemRow("smtp-relay / password", "bws://Agents/smtp-relay-password", "1 · in use", true)]),
        };
        var permissions = new VaultPermissionRow[]
        {
            new("staging-db / password", "1Password", "Session 1 · Invoices", null, "This session", false, "14:02", "14:02", _Done),
            new("staging-db / password", "1Password", "Project Invoices", "every session", "Always", true, "10 Nov", "12 Nov", _Done),
            new("smtp-relay / password", "Bitwarden", "Session 2 · Webshop", null, "This session", false, "11:15", "11:15", _Done),
        };
        var audit = new VaultAuditRow[]
        {
            new("14:06", "Requested", "deploy-bot / token", "Session 3", string.Empty, "1Password"),
            new("14:04", "Masked", "staging-db / password", "Session 1", "1 occurrence in the output", "1Password"),
            new("14:02", "Injected", "staging-db / password", "Session 1", string.Empty, "1Password"),
            new("14:02", "Allowed", "staging-db / password", "Session 1", string.Empty, "1Password"),
        };
        var groups = new VaultSessionGroup[]
        {
            new("pane-1", "Session 1", "Invoices",
            [
                new("staging-db / password", "1Password", "1Password · since 14:02 · allowed for this session", true, _Done, _Done),
            ]),
            new("pane-2", "Session 2", "Webshop",
            [
                new("smtp-relay / password", "Bitwarden", "Bitwarden · since 11:15 · allowed for this session", true, _Done, _Done),
                new("deploy-bot / token", "1Password", "1Password · since 14:07 · always allowed for Webshop", false, null, _Done),
            ]),
        };
        vault.Apply(availability, providers, permissions, audit, groups);
    }
}
