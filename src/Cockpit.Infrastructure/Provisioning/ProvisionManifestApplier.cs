using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Core.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Provisioning;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Infrastructure.Provisioning;

// AC-1506 (DB-2.5b): the one engine behind routes C2 and D. Phase 1 runs before plugins load and is all or nothing:
// resolve everything, install the plugins, then one write of cockpit.json. Phase 2 needs running plugins and comes after
// that write, so it is not atomic with it: the connections, the restore reference, and using the token up.
public static class ProvisionManifestApplier
{
    public const string Applied = "applied";

    public const string KeptLocal = "kept-local";

    public const string ReplacedManaged = "replaced-managed";

    public const string RestoredUnmanaged = "restored-unmanaged";

    public const string PluginFailed = "plugin-failed";

    public const string ConnectionSkipped = "connection-skipped";

    public const string SourceNote = "source-note";

    public const string SignInNeeded = "sign-in-needed";

    public const string TokenNeeded = "token-needed";

    // A section this version reads but does not apply yet, said rather than passed over.
    public const string NotApplied = "not-applied";

    // A Claude profile keeps its own provider on disk; every other provider is a plugin's.
    private const string ClaudeProviderId = "claude-provider";

    // AC-1316's opening stand, written as a default key like any other setting.
    private const string SimpleViewKey = "Layout.OpenInSimpleView";

    // AC-1511's one way to redeem an installation token: the connection's plugin's own channel action.
    private const string ExchangeAction = "connect-with-install-token";

    private const string ExchangeCredential = "exchange-provision-token";

    // The link configures and does not log in: the operator signs in through the plugin's own settings.
    private const string InteractiveCredential = "interactive";

    private static readonly JsonSerializerOptions ValueOptions = new() { Converters = { new JsonStringEnumConverter() } };

    private static readonly JsonSerializerOptions ChannelOptions = new(JsonSerializerDefaults.Web);

    // Manifest names that are not their cockpit.json path in camelCase (manifest-v1 §3.3).
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal) { ["projects.categoryOrder"] = "CategoryOrder" };

    public static Task<ProvisioningOutcome> ApplyAsync(ProvisioningOutcome outcome, string stateRoot, ILogger logger, CancellationToken cancellationToken = default)
    {
        var pluginsRoot = Path.Combine(stateRoot, "plugins");
        var storeClient = new PluginStoreClient();
        var provisioning = new PluginProvisioningService(storeClient, new PluginInstaller(pluginsRoot));
        return ApplyAsync(outcome, stateRoot, pluginsRoot, storeClient, provisioning, logger, cancellationToken);
    }

    internal static async Task<ProvisioningOutcome> ApplyAsync(
        ProvisioningOutcome outcome,
        string stateRoot,
        string pluginsRoot,
        IPluginStoreClient storeClient,
        IPluginProvisioningService provisioning,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var manifest = outcome.Manifest ?? throw new ArgumentException("There is no manifest to apply.", nameof(outcome));
        var report = manifest.Notes.Select(note => note with { Reason = SourceNote, Detail = note.Detail ?? note.Reason }).ToList();
        var access = new CockpitConfigFileAccess(CockpitConfigPath.For(stateRoot));
        var current = await access.ReadAsync(cancellationToken).ConfigureAwait(false) ?? new CockpitConfigFile();

        var tokenNeeded = _TokenNeeded(manifest, current);
        report.AddRange(tokenNeeded);
        var settings = _Settings(manifest, report);
        _ReportNotApplied(manifest, report);
        var firstRun = _FirstRun(manifest, report);
        var plugins = await _ResolvePluginsAsync(manifest, current, pluginsRoot, storeClient, firstRun.WorkKind, report, cancellationToken).ConfigureAwait(false);

        // Nothing is installed while anything is unresolved, and nothing is written while anything failed: the write is last.
        var pinned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failed = plugins.Failed;
        foreach (var request in failed.Count == 0 ? plugins.Requests : Enumerable.Empty<PluginProvisionRequest>())
        {
            var result = (await provisioning.InstallManyAsync([request], AbstractionsContract.Version, cancellationToken: cancellationToken).ConfigureAwait(false)).Results[0];
            if (result is { IsSuccess: true, FolderId: { } folderId, Sha256: { } sha256 })
            {
                pinned[folderId] = sha256;
                report.Add(new($"plugins.{request.Id}", Applied, Detail: $"installed {request.Version.Version}"));
            }
            else
            {
                failed.Add(new($"plugins.{request.Id}", PluginFailed, Detail: result.Error ?? "the store could not hand it over"));
            }
        }

        if (failed.Count > 0)
        {
            report.AddRange(failed);
            var failure = $"Provisioning wrote nothing to the settings: {string.Join("; ", failed.Concat(tokenNeeded).Select(line => $"{line.Path} ({line.Detail})"))}.";
            logger.LogError("{Failure}", failure);
            return outcome with { Failure = failure, Report = report };
        }

        var organisation = manifest.Issuer?.Organization is { } org ? org.Name ?? org.Id : null;
        await access.UpdateAsync(file =>
        {
            var state = file.Provisioning ??= new ProvisioningStateEntry();
            var wanted = settings.Select(setting => new Wanted(setting.Key, setting.Locked, organisation ?? setting.By, ManagedConfigKeys.Json(setting.Value),
                    target => ManagedConfigKeys.Set(target, setting.Chain, setting.Value)))
                .Concat(_PluginKeys(manifest, plugins.Enable.Concat(pinned.Keys), file, state, organisation ?? "your organisation"))
                .Concat((manifest.Profiles?.Items ?? []).Select(profile => _ProfileKey(file, profile, organisation ?? profile.By, firstRun.ReadingLevel)))
                .Concat(_FirstRunKeys(firstRun, organisation ?? "your organisation"))
                .ToList();
            _Merge(file, state, wanted, report);

            foreach (var (id, sha256) in pinned)
            {
                if (file.Plugins.TryGetValue(id, out var entry))
                {
                    entry.PinnedSha256 = sha256;
                }
            }

            foreach (var store in _Stores(manifest, current).Where(store => !file.PluginStores.Any(store.SameStoreAs)))
            {
                file.PluginStores.Add(store);
            }

            foreach (var project in manifest.Projects?.Own ?? [])
            {
                var key = $"Projects.{project.Id}";
                if (file.Projects.All(candidate => candidate.Id != project.Id))
                {
                    file.Projects.Add(_Project(project));
                    report.Add(new($"projects.own.{project.Id}", Applied, Detail: project.Name));
                }
                else if (!state.Applied.ContainsKey(key))
                {
                    report.Add(new($"projects.own.{project.Id}", KeptLocal, Detail: "a project with this id is already here; the local one stays"));
                }

                // A project is added once and never overwritten; this only marks that the local one was already named.
                state.Applied[key] = ManagedConfigKeys.Hash(project.Id);
            }

            // The wizard's questions are answered by the manifest; the provider sign-in stays for the report (§3.10).
            if (manifest.FirstRun is not null)
            {
                file.FirstRunWizardVersion = FirstRunWizardVersion.Current;
            }

            state.Policy = manifest.Policy is { } policy ? policy with { By = organisation ?? policy.By } : null;
            state.Source = outcome.Source ?? state.Source;
            state.ManifestId = manifest.ManifestId;
            state.Revision = manifest.Revision;
        }, cancellationToken).ConfigureAwait(false);

        await _WriteOrgRulesAsync(manifest.Assistant?.OrgRules, organisation, stateRoot, report, cancellationToken).ConfigureAwait(false);
        return outcome with { Report = report };
    }

    // §3.6: its own file, never the personal ones. A manifest without a rule set takes the file away; both say so.
    private static async Task _WriteOrgRulesAsync(ProvisionOrgRules? rules, string? organisation, string stateRoot, List<ProvisionNote> report, CancellationToken cancellationToken)
    {
        var path = CockpitConfigPath.AssistantOrgRulesFor(stateRoot);
        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null;
        if (rules is null)
        {
            if (existing is not null)
            {
                File.Delete(path);
                report.Add(new("assistant.orgRules", Applied, Detail: "the organisation's rule set was removed"));
            }

            return;
        }

        var by = organisation ?? rules.By;
        var title = string.IsNullOrWhiteSpace(rules.Title) ? string.Empty : $"## {rules.Title.Trim()}\n\n";
        var text = $"# Rule set of {by}\n\nSet by {by}. Where it conflicts with your own instructions or memory, this rule set wins.\n\n{title}{rules.Content.Trim()}\n";
        if (existing == text)
        {
            return;
        }

        var staging = path + ".tmp";
        await File.WriteAllTextAsync(staging, text, cancellationToken).ConfigureAwait(false);
        File.Move(staging, path, overwrite: true);
        report.Add(new("assistant.orgRules", Applied, Detail: existing is null ? $"rule set of {by} written" : $"rule set of {by} updated"));
    }

    // One key the manifest has a value for: its JSON as the manifest wants it, and how to write that in.
    private sealed record Wanted(string Key, bool Locked, string By, string Value, Action<CockpitConfigFile> Write);

    // manifest-v1 §4.2, per key: a default gives way to a local choice, a lock pushes it aside and keeps it, a release puts it back.
    private static void _Merge(CockpitConfigFile file, ProvisioningStateEntry state, List<Wanted> wanted, List<ProvisionNote> report)
    {
        foreach (var (key, managed) in state.Managed.Where(managed => !wanted.Any(want => want.Locked && want.Key == managed.Key)).ToList())
        {
            state.Managed.Remove(key);
            if (state.Displaced.Remove(key, out var displaced))
            {
                ManagedConfigKeys.Write(file, key, displaced);
            }

            report.Add(new(key, RestoredUnmanaged, managed.By, $"no longer managed by {managed.By}"));
        }

        foreach (var want in wanted)
        {
            var local = ManagedConfigKeys.Read(file, want.Key);
            // Back at the built-in value after an apply is a choice too; only a key never applied can be unset.
            var unset = local is null || (!state.Applied.ContainsKey(want.Key) && ManagedConfigKeys.IsUnset(want.Key, local));
            var hash = ManagedConfigKeys.Hash(want.Value);
            if (want.Locked)
            {
                if (local != want.Value)
                {
                    // A lock moving to a new value replaces the previous lock's value, not the operator's.
                    if (!unset && local is not null && local != state.Managed.GetValueOrDefault(want.Key)?.Value)
                    {
                        state.Displaced.TryAdd(want.Key, local);
                        report.Add(new(want.Key, ReplacedManaged, want.By, $"replaced by {want.By}'s management; your own value is kept"));
                    }

                    want.Write(file);
                }

                state.Managed[want.Key] = new ManagedKeyEntry { By = want.By, Value = ManagedConfigKeys.Read(file, want.Key) ?? want.Value };
                state.Applied[want.Key] = state.Offered[want.Key] = hash;
            }
            // A local value already equal to the default is adopted as applied, not reported as a choice that stays.
            else if (unset || local == want.Value || local is not null && state.Applied.GetValueOrDefault(want.Key) == ManagedConfigKeys.Hash(local))
            {
                if (local != want.Value)
                {
                    want.Write(file);
                    if (!unset)
                    {
                        report.Add(new(want.Key, Applied, want.By, "the default was updated"));
                    }
                }

                state.Applied[want.Key] = state.Offered[want.Key] = ManagedConfigKeys.Hash(ManagedConfigKeys.Read(file, want.Key) ?? want.Value);
            }
            else if (state.Offered.GetValueOrDefault(want.Key) != hash)
            {
                state.Offered[want.Key] = hash;
                report.Add(new(want.Key, KeptLocal, want.By, want.Value.StartsWith('{') ? "your choice stays; the default changed" : $"your choice stays; the default is now {want.Value}"));
            }
        }
    }

    private sealed record FirstRunChoices(PluginWorkKindOption? WorkKind, string? ReadingLevel, bool? SimpleView);

    // §3.10: what the wizard would have asked. Each answer that cannot be applied is said, never passed over.
    private static FirstRunChoices _FirstRun(ProvisionManifest manifest, List<ProvisionNote> report)
    {
        var asked = manifest.FirstRun;
        var kind = PluginWorkKinds.All.FirstOrDefault(option => string.Equals(option.Key, asked?.WorkKind, StringComparison.OrdinalIgnoreCase));
        if (asked?.WorkKind is { } name && kind is null)
        {
            report.Add(new("firstRun.workKind", NotApplied, Detail: $"no work kind is called \"{name}\"; they are {string.Join(", ", PluginWorkKinds.All.Select(option => option.Key))}"));
        }

        string? level = null;
        if (asked?.ReadingLevel is { } text)
        {
            if (Enum.TryParse<ReadingLevel>(text, ignoreCase: true, out var parsed) && Enum.GetName(parsed) is { } named)
            {
                level = named;
            }
            else
            {
                report.Add(new("firstRun.readingLevel", NotApplied, Detail: $"\"{text}\" is not a reading level"));
            }
        }

        // A reading level is a profile's default; without a profile from the manifest there is none to put it on.
        if (level is not null && manifest.Profiles?.Items is not { Count: > 0 })
        {
            report.Add(new("firstRun.readingLevel", NotApplied, Detail: "it is the default of the profiles the manifest brings, and it brings none"));
            level = null;
        }

        return new(kind, level, asked?.SimpleView);
    }

    private static IEnumerable<Wanted> _FirstRunKeys(FirstRunChoices choices, string by)
    {
        if (choices.SimpleView is { } simple)
        {
            var json = ManagedConfigKeys.Json(simple);
            yield return new Wanted(SimpleViewKey, false, by, json, target => ManagedConfigKeys.Write(target, SimpleViewKey, json));
        }
    }

    // Required plugins are locked on, forbidden ones locked off where they are registered (§3.1), selected ones a default.
    private static IEnumerable<Wanted> _PluginKeys(
        ProvisionManifest manifest, IEnumerable<string> enable, CockpitConfigFile file, ProvisioningStateEntry state, string by)
    {
        var required = (manifest.Plugins?.Required ?? []).Select(plugin => plugin.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var on = enable.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => new Wanted($"Plugins.{id}.Enabled", required.Contains(id), by, "true", target => ManagedConfigKeys.Write(target, $"Plugins.{id}.Enabled", "true")));
        var off = (manifest.Plugins?.Forbidden ?? [])
            .Where(id => file.Plugins.ContainsKey(id) || state.Managed.ContainsKey($"Plugins.{id}.Enabled"))
            .Select(id => new Wanted($"Plugins.{id}.Enabled", true, by, "false", target => ManagedConfigKeys.Write(target, $"Plugins.{id}.Enabled", "false")));
        return on.Concat(off);
    }

    // A profile's value is its carried fields after the manifest's are set on it, so what it keeps of this machine counts too.
    private static Wanted _ProfileKey(CockpitConfigFile file, ProvisionProfile profile, string by, string? readingLevel)
    {
        var key = $"Profiles.{profile.Label}";
        var probe = new CockpitConfigFile();
        var local = ManagedConfigKeys.Read(file, key);
        if (local is not null)
        {
            ManagedConfigKeys.Write(probe, key, local);
        }

        return new Wanted(key, profile.Mode == ProvisionManifestReader.LockedMode, by, _Profiled(probe, profile, readingLevel), target => _Profiled(target, profile, readingLevel));
    }

    private static string _Profiled(CockpitConfigFile file, ProvisionProfile profile, string? readingLevel)
    {
        var entry = file.Profiles.FirstOrDefault(candidate => candidate.Label == profile.Label);
        if (entry is null)
        {
            entry = new SessionProfileEntry();
            file.Profiles.Add(entry);
        }

        _SetProfile(entry, profile, readingLevel);
        return ManagedConfigKeys.Carried(entry);
    }

    // Phase 2, once plugins run. The token is used up only when nothing it was meant for failed; otherwise the link stays usable.
    public static async Task<ProvisioningOutcome> FinishAsync(
        ProvisioningOutcome outcome, PluginChannelHub channels, string stateRoot, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (outcome is not { Manifest: { } manifest, Failure: null })
        {
            return outcome;
        }

        var report = outcome.Report.ToList();
        var complete = true;
        foreach (var connection in manifest.Connections ?? [])
        {
            var path = $"connections.{connection.Id}";
            var config = connection.Config is { ValueKind: JsonValueKind.Object } block ? block : default;
            var credential = _String(config, "credential");
            if (credential == InteractiveCredential)
            {
                complete = false;
                report.Add(new(path, SignInNeeded, Detail: $"add the connection to {_String(config, "url") ?? connection.Id} in the settings of plugin {connection.Plugin} and sign in there"));
                continue;
            }

            if (credential != ExchangeCredential)
            {
                complete = false;
                report.Add(new(path, ConnectionSkipped, Detail: $"handing a connection to plugin {connection.Plugin} is not built yet"));
                continue;
            }

            if (outcome.Token is not { } token)
            {
                complete = false;
                report.Add(new(path, ConnectionSkipped, Detail: "the link carried no installation token to sign in with"));
                continue;
            }

            var (signedIn, detail) = await _ExchangeAsync(channels, connection, config, token, cancellationToken).ConfigureAwait(false);
            complete &= signedIn;
            report.Add(new(path, signedIn ? Applied : ConnectionSkipped, Detail: detail));
        }

        if (manifest.Backup?.RestoreFrom is { } restoreFrom)
        {
            report.Add(new("backup.restoreFrom", NotApplied, Detail: $"restoring \"{restoreFrom}\" needs a backup target from a plugin, and none offers one yet"));
        }

        report.AddRange((manifest.Profiles?.Items ?? []).Select(profile => profile.Provider?.Id).OfType<string>().Distinct()
            .Select(provider => new ProvisionNote($"providers.{provider}", SignInNeeded, Detail: $"sign in to {provider} on this machine")));

        if (outcome.Token is { } used)
        {
            if (complete)
            {
                ProvisioningEntry.UseUpToken(stateRoot, used);
            }
            else
            {
                report.Add(new("link", NotApplied, Detail: $"the link was not used up; start with it again and {ProvisioningEntry.ReapplyArgument}"));
            }
        }

        foreach (var line in report)
        {
            logger.LogInformation("Provisioning {Path}: {Outcome} {Detail}", line.Path, line.Reason, line.Detail);
        }

        return outcome with { Report = report };
    }

    // The plugin gets the block's config as it came, plus the connection's id and a name to file it under.
    private static async Task<(bool SignedIn, string Detail)> _ExchangeAsync(
        PluginChannelHub channels, ProvisionConnection connection, JsonElement config, ProvisionToken token, CancellationToken cancellationToken)
    {
        var block = JsonNode.Parse(config.GetRawText())!.AsObject();
        block["id"] = connection.Id;
        block["name"] ??= connection.Id;
        var payload = JsonSerializer.SerializeToElement(new JsonObject { ["connection"] = block, ["token"] = token.Value });
        try
        {
            var answer = await channels.InvokeAsync(connection.Plugin, ExchangeAction, payload, cancellationToken).ConfigureAwait(false);
            var reply = answer.Deserialize<ExchangeAnswer>(ChannelOptions);
            return (reply?.Success == true, (reply?.Message ?? "no answer").Replace(token.Value, "[redacted]", StringComparison.Ordinal));
        }
        catch (PluginChannelUnknownActionException)
        {
            return (false, $"plugin {connection.Plugin} has no way to redeem an installation token on this cockpit; the connection is not signed in");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (false, exception.Message.Replace(token.Value, "[redacted]", StringComparison.Ordinal));
        }
    }

    private sealed record ExchangeAnswer(bool Success, string? Message);

    private static string? _String(JsonElement config, string name) =>
        config.ValueKind == JsonValueKind.Object && config.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record SettingValue(string Key, PropertyInfo[] Chain, object? Value, bool Locked, string By);

    // Each key maps to one cockpit.json leaf on the carrier white list (AC-540); any other key is ignored and reported.
    private static List<SettingValue> _Settings(ProvisionManifest manifest, List<ProvisionNote> report)
    {
        var settings = new List<SettingValue>();
        foreach (var (key, setting) in manifest.Settings ?? [])
        {
            var path = Aliases.GetValueOrDefault(key) ?? string.Join('.', key.Split('.').Select(name => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..]));
            var chain = SettingsCarrierWhitelist.Carried.Contains(path) ? ManagedConfigKeys.Chain(path) : null;
            if (chain is null)
            {
                report.Add(new($"settings.{key}", ProvisionManifestReader.SkippedUnknown, setting.By, "not on the settings white list, so it was not written"));
                continue;
            }

            try
            {
                settings.Add(new(path, chain, setting.Value.Deserialize(chain[^1].PropertyType, ValueOptions), setting.Mode == ProvisionManifestReader.LockedMode, setting.By));
            }
            catch (JsonException)
            {
                report.Add(new($"settings.{key}", ProvisionManifestReader.SkippedUnknown, setting.By, $"not a value {path} takes"));
            }
        }

        return settings;
    }

    private static void _ReportNotApplied(ProvisionManifest manifest, List<ProvisionNote> report)
    {
        void Say(bool present, string path, string why)
        {
            if (present)
            {
                report.Add(new(path, NotApplied, Detail: why));
            }
        }

        Say(manifest.Profiles?.AssistantProfile is not null, "profiles.assistantProfile", "the assistant's profile is chosen on this machine for now");
        Say(manifest.Profiles?.DefaultProfile is not null, "profiles.defaultProfile", "the default profile is chosen on this machine for now");
        Say(manifest.Projects?.Shared is { Count: > 0 }, "projects.shared", "shared projects arrive with their connection's plugin");
        Say(manifest.Backup is { Target: not null } or { Schedule: not null }, "backup", "the backup target and schedule are set on this machine for now");
        foreach (var project in manifest.Projects?.Own ?? [])
        {
            Say(project is { MemoryRef: not null } or { Resources: not null } or { PluginFields: not null } or { AdditionalInfo: not null } or { Jobs: not null },
                $"projects.own.{project.Id}", "its memory, resources, plugin fields, info and jobs are not applied yet");
        }
    }

    private sealed record ResolvedPlugins(List<PluginProvisionRequest> Requests, List<string> Enable, List<ProvisionNote> Failed);

    // required ∪ (selected ∩ allowed) − forbidden (§3.1). A registered version here, or one in this build's bundle, that
    // is new enough is not fetched; one that ships with the build and is not installed yet is left to the bundle's seeding.
    private static async Task<ResolvedPlugins> _ResolvePluginsAsync(
        ProvisionManifest manifest,
        CockpitConfigFile current,
        string pluginsRoot,
        IPluginStoreClient storeClient,
        PluginWorkKindOption? workKind,
        List<ProvisionNote> report,
        CancellationToken cancellationToken)
    {
        var resolved = new ResolvedPlugins([], [], []);
        var forbidden = manifest.Plugins?.Forbidden ?? [];
        var allowed = manifest.Plugins?.Allowed is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().Select(id => id.GetString()).ToList() : null;
        var wanted = (manifest.Plugins?.Required ?? []).Select(plugin => (plugin.Id, plugin.MinVersion, plugin.Pin, plugin.Store))
            .Concat((manifest.Plugins?.Selected ?? []).Where(plugin => allowed is null || allowed.Contains(plugin.Id)).Select(plugin => (plugin.Id, plugin.MinVersion, Pin: (string?)null, Store: (string?)null)))
            .Where(plugin => !forbidden.Contains(plugin.Id))
            .ToList();

        var indexes = new Dictionary<PluginStoreConfig, PluginStoreFetchResult>();

        async Task<PluginStoreFetchResult> FetchAsync(PluginStoreConfig store)
        {
            if (!indexes.TryGetValue(store, out var fetched))
            {
                fetched = indexes[store] = await storeClient.FetchIndexAsync(store, cancellationToken).ConfigureAwait(false);
            }

            return fetched;
        }

        // AC-511's path: what the stores tag for this work kind comes as selected plugins, so allowed and forbidden still rule.
        if (workKind is not null)
        {
            var tagged = new List<(string Id, string? MinVersion, string? Pin, string? Store)>();
            // A private store without a token here is not asked: _TokenNeeded already says so.
            var readable = (manifest.Stores?.Allowed ?? []).Concat(manifest.Stores?.Added ?? [])
                .Where(store => store.Auth != "token-required" || !string.IsNullOrWhiteSpace(_LocalToken(store, current)))
                .Select(store => PluginStoreConfig.Remote(store.Url, _LocalToken(store, current)));
            foreach (var store in readable.Concat(current.PluginStores).DistinctBy(candidate => candidate.Location))
            {
                var index = (await FetchAsync(store).ConfigureAwait(false)).Index;
                tagged.AddRange((index?.Plugins ?? [])
                    .Where(entry => entry.Audience?.Contains(workKind.Key, StringComparer.OrdinalIgnoreCase) == true
                        && entry.Category != PluginStoreEntry.ProviderCategory
                        && !forbidden.Contains(entry.Id)
                        && (allowed is null || allowed.Contains(entry.Id))
                        && entry.Versions.Any(offered => PluginCompatibility.IsCompatible(offered, AbstractionsContract.Version, HostVersionInfo.Current)))
                    .Select(entry => (entry.Id, (string?)null, (string?)null, (string?)null)));
            }

            if (tagged.Count == 0)
            {
                report.Add(new("firstRun.workKind", NotApplied, Detail: $"no store offers a plugin for {workKind.Label} that runs here"));
            }

            wanted.AddRange(tagged);
        }

        wanted = [.. wanted.DistinctBy(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase)];
        foreach (var (id, minVersion, pin, store) in wanted)
        {
            var installed = _Version(File.Exists(Path.Combine(pluginsRoot, id, "plugin.json")) ? File.ReadAllText(Path.Combine(pluginsRoot, id, "plugin.json")) : null);
            var bundled = _Version(BundledPluginResources.TryRead($"{id}/plugin.json") ?? _BundledFile(id));
            // A folder without a registration is one an earlier, failed apply left: fetched again, so it gets its pin.
            if (installed is not null && current.Plugins.ContainsKey(id) && _Satisfies(installed, minVersion, pin))
            {
                resolved.Enable.Add(id);
                continue;
            }

            if (installed is null && bundled is not null && _Satisfies(bundled, minVersion, pin))
            {
                report.Add(new($"plugins.{id}", Applied, Detail: $"{bundled} comes with this build"));
                continue;
            }

            var stores = _Stores(manifest, current, store).Concat(store is null ? current.PluginStores : []).DistinctBy(candidate => candidate.Location).ToList();
            PluginProvisionRequest? request = null;
            foreach (var candidate in stores)
            {
                var fetched = await FetchAsync(candidate).ConfigureAwait(false);
                var entry = fetched.Index?.Plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, id, StringComparison.OrdinalIgnoreCase));
                var version = entry?.Versions
                    .Where(offered => _Satisfies(offered.Version, minVersion, pin) && PluginCompatibility.IsCompatible(offered, AbstractionsContract.Version, HostVersionInfo.Current))
                    .Aggregate((PluginStoreVersion?)null, (best, offered) => best is null || PluginVersion.IsNewer(offered.Version, best.Version) ? offered : best);
                if (version is not null)
                {
                    request = new PluginProvisionRequest(id, entry!.Name, candidate, version);
                    break;
                }
            }

            if (request is null)
            {
                var unread = string.Join(", ", stores.Where(candidate => indexes[candidate].IsSuccess is false).Select(candidate => candidate.Location));
                resolved.Failed.Add(new($"plugins.{id}", PluginFailed, Detail: $"no store offers a version {pin ?? (minVersion is null ? "" : $"from {minVersion} ")}that runs here"
                    + (unread.Length == 0 ? "" : $"; could not read {unread}")));
                continue;
            }

            resolved.Requests.Add(request);
        }

        return resolved;
    }

    private static bool _Satisfies(string version, string? minVersion, string? pin) =>
        pin is not null ? string.Equals(version, pin, StringComparison.OrdinalIgnoreCase) : minVersion is null || !PluginVersion.IsNewer(minVersion, version);

    private static string? _Version(string? pluginJson)
    {
        try
        {
            return pluginJson is null ? null : JsonNode.Parse(pluginJson)?["version"]?.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? _BundledFile(string id)
    {
        var path = Path.Combine(AppContext.BaseDirectory, BundledPluginInstaller.BundledFolderName, id, "plugin.json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    // The manifest's stores, each with the token this machine holds for it: a token never travels in a manifest (§3.2).
    private static IEnumerable<PluginStoreConfig> _Stores(ProvisionManifest manifest, CockpitConfigFile current, string? id = null) =>
        (manifest.Stores?.Allowed ?? []).Concat(manifest.Stores?.Added ?? [])
            .Where(store => id is null || store.Id == id)
            .Select(store => PluginStoreConfig.Remote(store.Url, _LocalToken(store, current)));

    private static string? _LocalToken(ProvisionStore store, CockpitConfigFile current) =>
        current.PluginStores.FirstOrDefault(local => local is not null && local.SameStoreAs(PluginStoreConfig.Remote(store.Url)))?.Token;

    private static List<ProvisionNote> _TokenNeeded(ProvisionManifest manifest, CockpitConfigFile current) =>
        [.. (manifest.Stores?.Allowed ?? []).Concat(manifest.Stores?.Added ?? [])
            .Where(store => store.Auth == "token-required" && string.IsNullOrWhiteSpace(_LocalToken(store, current)))
            .DistinctBy(store => store.Id)
            .Select(store => new ProvisionNote($"stores.{store.Id}", TokenNeeded, Detail: $"enter the token for store {store.Id} ({store.Url}) under Manage stores"))];

    // Sets what travels and keeps what belongs to this machine: config directories, paths, keys and secret values.
    private static void _SetProfile(SessionProfileEntry entry, ProvisionProfile profile, string? readingLevel)
    {
        entry.Label = profile.Label;
        entry.Purpose = profile.Purpose;
        entry.SystemPrompt = profile.SystemPrompt;
        entry.DefaultKind = profile.DefaultKind;
        entry.MemoryCapMegabytes = profile.MemoryCapMegabytes;
        entry.EnabledMcpServers = profile.EnabledMcpServers;
        entry.Assistant = profile.Assistant is { } assistant ? (assistant.ValueKind == JsonValueKind.String ? assistant.GetString() : assistant.GetRawText()) : null;

        if (profile.Provider is { } provider)
        {
            var config = entry.Provider ?? new ProviderConfigEntry();
            config.Provider = provider.Id == ClaudeProviderId ? SessionProvider.ClaudeCli : SessionProvider.Plugin;
            config.PluginProviderId = provider.Id == ClaudeProviderId ? null : provider.Id;
            config.Model = provider.Model ?? config.Model;
            entry.Provider = config;
        }

        if (profile.Defaults is { } defaults)
        {
            var config = entry.Defaults ?? new ProfileDefaultsEntry();
            config.PermissionMode = defaults.PermissionMode ?? config.PermissionMode;
            config.Model = defaults.Model ?? config.Model;
            config.Effort = defaults.Effort ?? config.Effort;
            config.AutoApproveTools = defaults.AutoApproveTools ?? config.AutoApproveTools;
            config.OptionDefaults = defaults.OptionDefaults ?? config.OptionDefaults;
            config.DefaultReadingLevel = defaults.ReadingLevel ?? config.DefaultReadingLevel;
            entry.Defaults = config;
        }

        // The first-run level is the default of a profile that names none of its own.
        if (readingLevel is not null && profile.Defaults?.ReadingLevel is null)
        {
            var config = entry.Defaults ?? new ProfileDefaultsEntry();
            config.DefaultReadingLevel = readingLevel;
            entry.Defaults = config;
        }

        if (profile.Delegation is { } delegation)
        {
            var config = entry.Delegation ?? new DelegationPolicyEntry();
            config.AllowedAsTarget = delegation.AllowedAsTarget ?? config.AllowedAsTarget;
            config.MaxConcurrent = delegation.MaxConcurrent ?? config.MaxConcurrent;
            config.PermissionCeiling = delegation.PermissionCeiling ?? config.PermissionCeiling;
            config.MayDelegateFurther = delegation.MayDelegateFurther ?? config.MayDelegateFurther;
            config.TimeoutMinutes = delegation.TimeoutMinutes ?? config.TimeoutMinutes;
            config.AllowedTools = delegation.AllowedTools ?? config.AllowedTools;
            entry.Delegation = config;
        }

        if (profile.Environment is { } environment)
        {
            var local = entry.EnvironmentVariables ?? [];
            entry.EnvironmentVariables = [.. environment.Select(variable => new ProfileEnvironmentVariableEntry
            {
                Key = variable.Name,
                Value = variable.Secret == true ? null : variable.Value,
                SecretValue = variable.Secret == true ? local.FirstOrDefault(kept => kept.Key == variable.Name)?.SecretValue : null,
            })];
        }
    }

    private static ProjectEntry _Project(ProvisionOwnProject project) => new()
    {
        Id = project.Id,
        Name = project.Name,
        Description = project.Description,
        Category = project.Category,
        GitUrl = project.GitUrl,
        DefaultProfileLabel = project.DefaultProfileLabel,
        BehaviorPrompt = project.BehaviorPrompt,
        Assistant = project.Assistant is { } assistant ? (assistant.ValueKind == JsonValueKind.String ? assistant.GetString() : assistant.GetRawText()) : null,
        IsolateInWorktreeByDefault = project.IsolateInWorktreeByDefault ?? false,
    };
}
