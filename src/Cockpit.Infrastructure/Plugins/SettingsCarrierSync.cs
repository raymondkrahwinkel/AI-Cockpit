using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Plugins.Abstractions.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Plugins;

// One setting both machines changed since they last agreed: this machine's value and the carrier's, as JSON text. Null
// is a removed profile or project.
public sealed record SettingsConflict(string Key, string? Mine, string? Theirs);

// AC-1510: keeps the white-listed settings (AC-540) in step with the carrier a plugin registered, per key against the
// base both sides last agreed on. A change on one side is taken over; a change on both is the operator's choice, never
// merged quietly. What is taken over lands at the next start, before anything holds settings in memory to save back.
public sealed class SettingsCarrierSync : ISingletonService, IDisposable
{
    // The same rhythm as the project checksum poll: a background look, not something anyone waits on.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    // A save of cockpit.json comes in bursts; one push after the burst is enough.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    // A write refused because another machine wrote in between is read and merged again, this many times per run.
    private const int Attempts = 3;

    private static readonly JsonSerializerOptions StateJson = new() { WriteIndented = true };

    private readonly SettingsCarriers _carriers;
    private readonly CockpitConfigFileAccess _configFile;
    private readonly string _statePath;
    private readonly IToastService? _toasts;
    private readonly ILogger<SettingsCarrierSync> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();

    private ITimer? _timer;
    private ITimer? _settle;
    private Task _lastRun = Task.CompletedTask;
    private string? _lastLocal;
    private bool _toldUnreachable;
    private bool _toldConflicts;
    private bool _disposed;

    public SettingsCarrierSync(SettingsCarriers carriers, IToastService? toasts = null, ILogger<SettingsCarrierSync>? logger = null)
        : this(carriers, CockpitConfigPath.Default, toasts, logger, TimeProvider.System)
    {
    }

    // Test seam: a config file of its own, so two machines can live in one test.
    internal SettingsCarrierSync(SettingsCarriers carriers, string configFilePath, IToastService? toasts, ILogger<SettingsCarrierSync>? logger, TimeProvider time)
    {
        _carriers = carriers;
        _configFile = new CockpitConfigFileAccess(configFilePath);
        _statePath = _StatePathFor(configFilePath);
        _toasts = toasts;
        _logger = logger ?? NullLogger<SettingsCarrierSync>.Instance;
        _time = time;
    }

    // Told when the operator has a choice to make; set by the desktop, which owns the dialog. Without it the toast says so.
    public Action<IReadOnlyList<SettingsConflict>>? OnConflicts { get; set; }

    // The run in flight, for the tests to await.
    internal Task Running => _lastRun;

    // Pulls at once and every interval after, and pushes shortly after a save. Never awaited: a start does not wait on it.
    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        CockpitConfigFileAccess.Written += _OnWritten;
        _settle = _time.CreateTimer(_ => _lastRun = _RunLoggedAsync(onlyWhenChanged: true), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer = _time.CreateTimer(_ => _lastRun = _RunLoggedAsync(onlyWhenChanged: false), null, TimeSpan.Zero, Interval);
    }

    public void Dispose()
    {
        _disposed = true;
        CockpitConfigFileAccess.Written -= _OnWritten;
        _stopping.Cancel();
        _timer?.Dispose();
        _settle?.Dispose();
        _timer = null;
    }

    // What a run took over, written while the backend is built: before anything reads the settings into memory, which
    // would save the old value back. A key the operator changed after it was taken over keeps the operator's value.
    public static async Task ApplyIncomingAsync(string configFilePath, ILogger logger, CancellationToken cancellationToken = default)
    {
        var statePath = _StatePathFor(configFilePath);
        var state = _LoadState(statePath, logger);
        if (state.Incoming.Count == 0)
        {
            return;
        }

        var configFile = new CockpitConfigFileAccess(configFilePath);
        var local = await _ReadLocalAsync(configFile, cancellationToken).ConfigureAwait(false);
        var apply = state.Incoming
            .Where(pair => _Same(local.Units.GetValueOrDefault(pair.Key)?.Text, pair.Value.Was) && !local.Managed.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.Ordinal);
        if (apply.Count > 0)
        {
            await configFile.UpdateAsync(file => _Apply(file, apply), cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Took over {Count} settings from the settings carrier: {Keys}", apply.Count, string.Join(", ", apply.Keys));
        }

        state.Incoming.Clear();
        _SaveState(statePath, state);
    }

    public IReadOnlyList<SettingsConflict> Conflicts => [.. _LoadState().Conflicts.Values];

    // The operator's answer per key: true keeps this machine's value (pushed next), false takes the other one (at the next
    // start). A key left out stays a choice to make.
    public async Task ResolveAsync(IReadOnlyDictionary<string, bool> keepMine, CancellationToken cancellationToken = default)
    {
        var tookTheirs = 0;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _LoadState();
            var local = await _ReadLocalAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (key, mine) in keepMine)
            {
                if (!state.Conflicts.Remove(key, out var conflict))
                {
                    continue;
                }

                // The other side becomes the base: keeping mine is then a change of mine, taking theirs one of theirs.
                _SetOrRemove(state.Base ??= new Dictionary<string, string>(StringComparer.Ordinal), key, conflict.Theirs);
                if (!mine)
                {
                    state.Incoming[key] = new _Incoming(conflict.Theirs, local.Units.GetValueOrDefault(key)?.Text);
                    tookTheirs++;
                }
            }

            _SaveState(state);
        }
        finally
        {
            _gate.Release();
        }

        if (tookTheirs > 0)
        {
            _toasts?.Show($"{_Count(tookTheirs)} from your other machine take effect at the next start.", ToastSeverity.Information);
        }

        _lastRun = _RunLoggedAsync(onlyWhenChanged: false);
        await _lastRun.ConfigureAwait(false);
    }

    // One pull, merge and push. Public because the tests drive it directly, like the other planners.
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_carriers.Carrier is not { } carrier)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                if (await _MergeAsync(carrier, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                _logger.LogInformation("Settings carrier {Carrier} changed between read and write; reading again ({Attempt}/{Attempts})", carrier.Id, attempt, Attempts);
            }

            _logger.LogWarning("Settings carrier {Carrier} kept changing; the push waits for the next run", carrier.Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void _OnWritten(string path)
    {
        if (string.Equals(path, _configFile.ConfigFilePath, StringComparison.OrdinalIgnoreCase))
        {
            _settle?.Change(Settle, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task _RunLoggedAsync(bool onlyWhenChanged)
    {
        try
        {
            if (onlyWhenChanged && (await _ReadLocalAsync(_stopping.Token).ConfigureAwait(false)).Text == _lastLocal)
            {
                return;
            }

            await RunOnceAsync(_stopping.Token).ConfigureAwait(false);
            _toldUnreachable = false;
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Stopping: the next start picks up where this left off.
        }
        catch (Exception exception)
        {
            // Offline, signed out, refused: the base is unchanged on disk, so a later run pushes what is waiting.
            _logger.LogWarning(exception, "Settings sync with {Carrier} failed", _carriers.Carrier?.Id);
            if (!_toldUnreachable && _carriers.Carrier is { } carrier)
            {
                _toldUnreachable = true;
                _toasts?.Show($"Your settings could not be synced with {carrier.DisplayName}: {exception.Message} They are sent once it can be reached.", ToastSeverity.Warning);
            }
        }
    }

    // False when the carrier refused the write because it changed after the read.
    private async Task<bool> _MergeAsync(SettingsCarrierRegistration carrier, CancellationToken cancellationToken)
    {
        var remoteBytes = await carrier.ReadAsync(cancellationToken).ConfigureAwait(false);
        var remoteDocument = remoteBytes is null ? new JsonObject() : JsonNode.Parse(remoteBytes) as JsonObject ?? new JsonObject();

        // Only what the white list carries is ever taken from the carrier, whatever else it holds.
        var remote = _Units(_Carried(remoteDocument));
        var local = await _ReadLocalAsync(cancellationToken).ConfigureAwait(false);
        var state = _LoadState();
        var firstContact = state.Base is null;
        var baseline = state.Base ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var push = new Dictionary<string, string?>(StringComparer.Ordinal);
        var incoming = 0;
        var newConflicts = new List<SettingsConflict>();
        foreach (var key in remote.Keys.Union(local.Units.Keys).Union(baseline.Keys).Distinct(StringComparer.Ordinal))
        {
            // A managed key is the manifest's (AC-1507): never pushed, never taken over.
            if (local.Managed.Contains(key) || state.Conflicts.ContainsKey(key))
            {
                continue;
            }

            var isElement = (remote.GetValueOrDefault(key) ?? local.Units.GetValueOrDefault(key))?.IsElement ?? _IsElementKey(key);
            var theirs = remote.GetValueOrDefault(key)?.Text;
            var was = baseline.GetValueOrDefault(key);
            var mine = local.Units.GetValueOrDefault(key)?.Text;
            if (state.Incoming.TryGetValue(key, out var taken))
            {
                if (_Same(mine, taken.Was))
                {
                    mine = taken.Value;
                }
                else
                {
                    state.Incoming.Remove(key);
                }
            }

            if (_Same(mine, theirs))
            {
                continue;
            }

            // A setting that is simply absent on one side has no say; only a profile or project can be removed.
            var theirsChanged = !_Same(theirs, was) && (theirs is not null || isElement);
            var mineChanged = !_Same(mine, was) && (mine is not null || isElement) && !(firstContact && ManagedConfigKeys.IsUnset(key, mine));
            if (mineChanged && !theirsChanged)
            {
                push[key] = mine;
            }
            else if (theirsChanged && !mineChanged && theirs is not null)
            {
                state.Incoming[key] = new _Incoming(theirs, local.Units.GetValueOrDefault(key)?.Text);
                incoming++;
            }
            else if (theirsChanged)
            {
                var conflict = new SettingsConflict(key, mine, theirs);
                state.Conflicts[key] = conflict;
                newConflicts.Add(conflict);
            }
        }

        var written = remoteBytes;
        if (push.Count > 0)
        {
            foreach (var (key, value) in push)
            {
                _Set(remoteDocument, _UnitFor(key, local, remote), value);
            }

            var blob = JsonSerializer.SerializeToUtf8Bytes(remoteDocument);
            var version = remoteBytes is null ? null : Version(remoteBytes);
            if (!await _WriteAsync(carrier, blob, version, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            written = blob;
            _logger.LogInformation("Pushed {Count} settings to {Carrier}: {Keys}", push.Count, carrier.Id, string.Join(", ", push.Keys));
        }

        // The base is what the carrier holds now, so the next run sees only what changed after this one.
        state.Base = written is null ? new Dictionary<string, string>(StringComparer.Ordinal) : _Units(_Carried(JsonNode.Parse(written) as JsonObject ?? new JsonObject())).ToDictionary(pair => pair.Key, pair => pair.Value.Text, StringComparer.Ordinal);
        _SaveState(state);
        _lastLocal = local.Text;

        if (incoming > 0)
        {
            _toasts?.Show($"{_Count(incoming)} from your other machine take effect at the next start.", ToastSeverity.Information);
        }

        _SayConflicts(state, newConflicts.Count > 0);
        return true;
    }

    // Lowercase hex SHA-256 of the blob, the version WriteIfUnchangedAsync is handed.
    internal static string Version(byte[] blob) => Convert.ToHexStringLower(SHA256.HashData(blob));

    private static async Task<bool> _WriteAsync(SettingsCarrierRegistration carrier, byte[] blob, string? version, CancellationToken cancellationToken)
    {
        await carrier.WriteAsync(blob, cancellationToken).ConfigureAwait(false);
        return true;

        // ponytail: a carrier without the conditional write leaves a window between this read and the write.
        var current = await carrier.ReadAsync(cancellationToken).ConfigureAwait(false);
        if ((current is null ? null : Version(current)) != version)
        {
            return false;
        }

        await carrier.WriteAsync(blob, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void _SayConflicts(_State state, bool grew)
    {
        if (state.Conflicts.Count == 0 || (_toldConflicts && !grew))
        {
            return;
        }

        _toldConflicts = true;
        if (OnConflicts is { } onConflicts)
        {
            onConflicts([.. state.Conflicts.Values]);
        }
        else
        {
            _toasts?.Show($"{_Count(state.Conflicts.Count)} changed both here and on your other machine; nothing was overwritten.", ToastSeverity.Warning);
        }
    }

    private static string _Count(int count) => count == 1 ? "1 setting" : $"{count} settings";

    private Task<_Local> _ReadLocalAsync(CancellationToken cancellationToken) => _ReadLocalAsync(_configFile, cancellationToken);

    private static async Task<_Local> _ReadLocalAsync(CockpitConfigFileAccess configFile, CancellationToken cancellationToken)
    {
        var file = await configFile.ReadAsync(cancellationToken).ConfigureAwait(false) ?? new CockpitConfigFile();
        var document = JsonSerializer.SerializeToNode(file, CockpitConfigFileAccess.SerializerOptions) as JsonObject ?? new JsonObject();
        var carried = _Carried(document);
        return new _Local(_Units(carried), carried.ToJsonString(), new HashSet<string>(file.Provisioning?.Managed.Keys ?? Enumerable.Empty<string>(), StringComparer.Ordinal));
    }

    private static JsonObject _Carried(JsonObject document) =>
        JsonNode.Parse(SettingsCarrierWhitelist.Extract(document)) as JsonObject ?? new JsonObject();

    // Writes the taken-over keys into their top-level sections only, so nothing else in the file is touched.
    private static void _Apply(CockpitConfigFile file, IReadOnlyDictionary<string, string?> values)
    {
        foreach (var section in values.GroupBy(pair => _Steps(pair.Key)[0].Name, StringComparer.Ordinal))
        {
            if (typeof(CockpitConfigFile).GetProperty(section.Key) is not { CanWrite: true } property)
            {
                continue;
            }

            var root = new JsonObject { [section.Key] = JsonSerializer.SerializeToNode(property.GetValue(file), property.PropertyType, CockpitConfigFileAccess.SerializerOptions) };
            foreach (var (key, value) in section)
            {
                _Set(root, _Steps(key), value);
            }

            property.SetValue(file, root[section.Key]?.Deserialize(property.PropertyType, CockpitConfigFileAccess.SerializerOptions));
        }
    }

    // A unit is a white-listed leaf, or one whole profile or project, addressed by its label or id.
    private static Dictionary<string, _Unit> _Units(JsonObject carried)
    {
        var units = new Dictionary<string, _Unit>(StringComparer.Ordinal);
        _Walk(carried, [], units);
        return units;
    }

    private static void _Walk(JsonNode? node, List<_Step> steps, Dictionary<string, _Unit> units)
    {
        if (node is JsonObject obj)
        {
            foreach (var (name, value) in obj)
            {
                _Walk(value, [.. steps, new _Step(name, null)], units);
            }

            return;
        }

        if (node is JsonArray array && steps.Count == 1 && _IsElementKey(steps[0].Name + "."))
        {
            foreach (var item in array)
            {
                if (_ElementId(item) is { } id)
                {
                    var at = new List<_Step> { steps[0] with { Element = id } };
                    units.TryAdd(_Key(at), new _Unit(at, item?.ToJsonString() ?? "null", true));
                }
            }

            return;
        }

        units.TryAdd(_Key(steps), new _Unit(steps, node?.ToJsonString() ?? "null", false));
    }

    // The lists whose items are synced one by one: those the white list reaches into with `[]`.
    private static bool _IsElementKey(string key) =>
        SettingsCarrierWhitelist.Carried.Any(path => path.Contains("[].", StringComparison.Ordinal) && key.StartsWith(path[..path.IndexOf("[]", StringComparison.Ordinal)] + ".", StringComparison.Ordinal));

    private static string? _ElementId(JsonNode? item) =>
        item is JsonObject obj && (obj["Id"] ?? obj["Label"]) is JsonValue id && id.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static string _Key(IEnumerable<_Step> steps) => string.Join('.', steps.Select(step => step.Element is null ? step.Name : $"{step.Name}.{step.Element}"));

    private static List<_Step> _UnitFor(string key, _Local local, Dictionary<string, _Unit> remote) =>
        (local.Units.GetValueOrDefault(key) ?? remote.GetValueOrDefault(key))?.Steps ?? _Steps(key);

    // Rebuilt from a key alone (a base or incoming entry), where a profile or project is "<list>.<label>".
    private static List<_Step> _Steps(string key)
    {
        var parts = key.Split('.');
        return _IsElementKey(key)
            ? [new _Step(parts[0], key[(parts[0].Length + 1)..])]
            : [.. parts.Select(part => new _Step(part, null))];
    }

    // Null removes. A profile or project is laid over what is there, so what only this machine has stays as it is.
    private static void _Set(JsonObject root, List<_Step> steps, string? json)
    {
        var parent = root;
        foreach (var step in steps[..^1])
        {
            if (parent[step.Name] is not JsonObject next)
            {
                next = new JsonObject();
                parent[step.Name] = next;
            }

            parent = next;
        }

        var last = steps[^1];
        var value = json is null ? null : JsonNode.Parse(json);
        if (last.Element is null)
        {
            if (json is null)
            {
                parent.Remove(last.Name);
            }
            else
            {
                parent[last.Name] = value;
            }

            return;
        }

        if (parent[last.Name] is not JsonArray list)
        {
            list = [];
            parent[last.Name] = list;
        }

        var existing = list.FirstOrDefault(item => _ElementId(item) == last.Element);
        if (value is null)
        {
            if (existing is not null)
            {
                list.Remove(existing);
            }
        }
        else if (existing is JsonObject target && value is JsonObject source)
        {
            _Overlay(target, source);
        }
        else
        {
            list.Add(value);
        }
    }

    private static void _Overlay(JsonObject target, JsonObject source)
    {
        foreach (var (name, value) in source)
        {
            if (value is JsonObject inner && target[name] is JsonObject existing)
            {
                _Overlay(existing, inner);
            }
            else
            {
                target[name] = value?.DeepClone();
            }
        }
    }

    private static bool _Same(string? left, string? right) =>
        left is null || right is null ? left is null && right is null : JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right));

    private static void _SetOrRemove(Dictionary<string, string> values, string key, string? value)
    {
        if (value is null)
        {
            values.Remove(key);
        }
        else
        {
            values[key] = value;
        }
    }

    private static string _StatePathFor(string configFilePath) => Path.Combine(Path.GetDirectoryName(configFilePath) ?? ".", "settings-sync.json");

    private _State _LoadState() => _LoadState(_statePath, _logger);

    private static _State _LoadState(string statePath, ILogger logger)
    {
        try
        {
            return File.Exists(statePath) ? JsonSerializer.Deserialize<_State>(File.ReadAllText(statePath), StateJson) ?? new _State() : new _State();
        }
        catch (JsonException exception)
        {
            // An unreadable state is a first contact again: a difference becomes a choice, never an overwrite.
            logger.LogWarning(exception, "Settings sync state {Path} is unreadable; starting from no base", statePath);
            return new _State();
        }
    }

    private void _SaveState(_State state) => _SaveState(_statePath, state);

    private static void _SaveState(string statePath, _State state) => CockpitConfigPath.ReplaceAtomicallyPrivate(statePath, JsonSerializer.Serialize(state, StateJson));

    private sealed record _Step(string Name, string? Element);

    private sealed record _Unit(List<_Step> Steps, string Text, bool IsElement);

    private sealed record _Local(Dictionary<string, _Unit> Units, string Text, HashSet<string> Managed);

    private sealed record _Incoming(string? Value, string? Was);

    private sealed class _State
    {
        // Null: this machine never agreed with the carrier yet.
        public Dictionary<string, string>? Base { get; set; }

        public Dictionary<string, _Incoming> Incoming { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, SettingsConflict> Conflicts { get; set; } = new(StringComparer.Ordinal);
    }
}
