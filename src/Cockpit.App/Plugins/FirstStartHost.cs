using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.App.Views.Onboarding;
using Cockpit.Core.Configuration;
using Cockpit.Core.Provisioning;
using Cockpit.Infrastructure.Backup;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Provisioning;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.UI.Onboarding;

namespace Cockpit.App.Plugins;

// AC-1509: what a plugin's first-start route may ask of the host. A restore goes through the choice step's own restore
// view model, so the wizard sees it took over; a manifest goes through the provisioning applier's first phase, and the
// second phase runs with the plugins once the cockpit starts (App.InitializePlugins), as it does for route D.
internal sealed class FirstStartHost(string pluginId, string pluginName, IServiceProvider services) : IFirstStartHost
{
    public IPluginUiChannel Channel { get; } = new CockpitUiHost.UiChannel(services.GetRequiredService<PluginChannelHub>(), pluginId);

    public async Task<string> RestoreBackupAsync(string targetId, string backupId, CancellationToken cancellationToken = default)
    {
        if (services.GetServices<IFirstRunWizardStep>().OfType<StartChoiceStep>().FirstOrDefault()?.RestoreViewModel is not { } restore)
        {
            return "Restoring is not available in this build, so nothing was done.";
        }

        if (services.GetRequiredService<BackupTargets>().Find(targetId) is not { } target)
        {
            return "That place for backups is not available any more, so nothing was restored.";
        }

        var archive = Path.Combine(Path.GetTempPath(), $"cockpit-first-start-{Guid.NewGuid():n}.zip");
        try
        {
            await using (var source = await target.OpenReadAsync(backupId, cancellationToken))
            await using (var file = File.Create(archive))
            {
                await source.CopyToAsync(file, cancellationToken);
            }

            await restore.RestoreAsync(archive);
            return restore.Status;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"Nothing was restored: {exception.Message}";
        }
        finally
        {
            File.Delete(archive);
        }
    }

    public async Task<IReadOnlyList<string>> ApplyManifestAsync(
        string manifestJson,
        Func<IReadOnlyList<string>, Task<bool>> consent,
        CancellationToken cancellationToken = default)
    {
        var read = ProvisionManifestReader.Read(manifestJson);
        if (read.Manifest is not { } manifest)
        {
            return [read.Error ?? "The provisioning manifest was refused."];
        }

        // manifest-v1 §5.3: route C applies nothing before the operator saw where it comes from and what it installs.
        if (!await consent(WhatItSetsUp(manifest)))
        {
            return ["Nothing was set up."];
        }

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Cockpit.Provisioning");
        ProvisioningOutcome outcome;
        try
        {
            outcome = await ProvisionManifestApplier.ApplyAsync(
                new ProvisioningOutcome(manifest, read.Skipped, null) { Source = $"plugin {pluginName}" }, CockpitBuild.StateRoot, logger, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Setting up from the manifest failed");
            return [$"Nothing was set up: {exception.Message}"];
        }

        Program.Provisioning = outcome;

        if (outcome.Failure is { } failure)
        {
            return [$"Nothing was set up: {failure}"];
        }

        Dispatcher.UIThread.Post(() => services.GetServices<IFirstRunWizardStep>().OfType<StartChoiceStep>().FirstOrDefault()?.ViewModel.SettleSelected());

        return [Summary(manifest), .. outcome.Report.Select(line => $"{line.Path}: {line.Detail ?? line.Reason}")];
    }

    // The consent screen's lines (manifest-v1 §5.3): where it comes from, then what it sets up, with counts and names.
    // An issuer that carries only an id says "your organisation": an id tells the operator nothing.
    internal static IReadOnlyList<string> WhatItSetsUp(ProvisionManifest manifest)
    {
        var lines = new List<string>();
        if (manifest.Issuer is { } issuer)
        {
            var organisation = issuer.Organization?.Name;
            var profile = issuer.Profile?.Name;
            lines.Add(organisation is null && profile is null
                ? "From: your organisation and profile"
                : $"From: {string.Join(", ", new[] { organisation, profile, issuer.InstallationSet?.Name }.Where(name => name is not null))}");
        }

        var stores = (manifest.Stores?.Added ?? []).Concat(manifest.Stores?.Allowed ?? []).Select(store => store.Url).Distinct().ToList();
        if (stores.Count > 0)
        {
            lines.Add($"Plugin stores: {string.Join(", ", stores)}");
        }

        if (manifest.Plugins?.Required is { Count: > 0 } required)
        {
            lines.Add($"Plugins ({required.Count}): {string.Join(", ", required.Select(plugin => plugin.Id))}");
        }

        if (manifest.Profiles?.Items is { Count: > 0 } profiles)
        {
            lines.Add($"Profiles ({profiles.Count}): {string.Join(", ", profiles.Select(profile => profile.Label))}");
        }

        var projects = (manifest.Projects?.Own ?? []).Select(project => project.Name).Concat((manifest.Projects?.Shared ?? []).Select(project => project.Slug)).ToList();
        if (projects.Count > 0)
        {
            lines.Add($"Projects ({projects.Count}): {string.Join(", ", projects)}");
        }

        if (manifest.Settings is { Count: > 0 } settings)
        {
            lines.Add($"Settings ({settings.Count})");
        }

        if (manifest.Assistant?.OrgRules is { } rules)
        {
            lines.Add($"A rule set for your assistant: {rules.Title ?? rules.By}");
        }

        return lines.Count == 0 ? ["Settings only: no stores, plugins or rule set."] : lines;
    }

    // What an applied manifest left behind, in one sentence: the same counts the consent screen promised.
    internal static string Summary(ProvisionManifest manifest)
    {
        var parts = new List<string>();
        _Count(parts, manifest.Plugins?.Required?.Count ?? 0, "plugin");
        _Count(parts, manifest.Profiles?.Items?.Count ?? 0, "profile");
        _Count(parts, (manifest.Projects?.Own?.Count ?? 0) + (manifest.Projects?.Shared?.Count ?? 0), "project");
        _Count(parts, manifest.Settings?.Count ?? 0, "setting");

        return parts.Count == 0 ? "Set up: nothing to install, your settings are in place." : $"Set up: {string.Join(", ", parts)}.";
    }

    private static void _Count(List<string> parts, int count, string noun)
    {
        if (count > 0)
        {
            parts.Add($"{count} {noun}{(count == 1 ? string.Empty : "s")}");
        }
    }
}
