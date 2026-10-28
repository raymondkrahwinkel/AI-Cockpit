using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Configuration;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Plugins.Abstractions.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Plugins;

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

    public SettingsCarrierSync(SettingsCarriers carriers, IToastService? toasts = null, ILogger<SettingsCarrierSync>? logger = null, PluginSignInStanding? signIns = null)
        : this(carriers, CockpitConfigPath.Default, toasts, logger, TimeProvider.System)
    {
        WaitsForSignIn = token => signIns is null ? Task.FromResult(false) : signIns.WaitsAsync(carriers.PluginId, token);
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

    // AC-1663: whether the carrier's plugin waits for the operator to sign in. Replaced by the tests.
    internal Func<CancellationToken, Task<bool>> WaitsForSignIn { get; set; } = _ => Task.FromResult(false);

    // Told when the operator has a choice to make; set by the desktop, which owns the dialog. Without it the toast says so.
    public Action<IReadOnlyList<SettingsConflict>>? OnConflicts { get; set; }

    // Restarts the cockpit, offered beside "active after a restart"; set by the desktop, which owns the restart.
    public Action? Restart { get; set; }

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
    // would save the old value back. A key changed here since, or one this build cannot write, becomes a choice instead.
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
        var apply = new Dictionary<string, _Incoming>(StringComparer.Ordinal);
        foreach (var (key, taken) in state.Incoming)
        {
            var mine = local.Units.GetValueOrDefault(key)?.Text;
            if (_IsManaged(key, local.Managed))
            {
                continue;
            }

            // Already written by a start that stopped before it could say so: nothing left to do.
            if (_Same(mine, taken.Was))
            {
                apply[key] = taken;
            }
            else if (!_Same(mine, taken.Value))
            {
                state.Conflicts[key] = new _Conflict(mine, taken.Value, taken.Steps);
            }
        }

        var failed = new List<string>();
        if (apply.Count > 0)
        {
            await configFile.UpdateAsync(file => failed = _Apply(file, apply, logger), cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Took over {Count} settings from the settings carrier: {Keys}", apply.Count - failed.Count, string.Join(", ", apply.Keys.Except(failed)));
        }

        foreach (var key in failed)
        {
            state.Conflicts[key] = new _Conflict(local.Units.GetValueOrDefault(key)?.Text, apply[key].Value, apply[key].Steps);
        }

        state.Incoming.Clear();
        _SaveState(statePath, state);
    }

    public IReadOnlyList<SettingsConflict> Conflicts => [.. _LoadState().Conflicts.Select(pair => new SettingsConflict(pair.Key, pair.Value.Mine, pair.Value.Theirs))];

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
                    state.Incoming[key] = new _Incoming(conflict.Theirs, local.Units.GetValueOrDefault(key)?.Text, conflict.Steps);
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
            _SayFetched();
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
            // AC-1663: signed out is said once by the host, with a Sign in button, so it is not a second toast here.
            if (!_toldUnreachable && _carriers.Carrier is { } carrier && !await WaitsForSignIn(CancellationToken.None).ConfigureAwait(false))
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

        var local = await _ReadLocalAsync(cancellationToken).ConfigureAwait(false);
        var state = _LoadState();
        // Only what the white list carries is ever taken from the carrier, whatever else it holds.
        var remoteCarried = _Carried(remoteDocument);
        var remote = _Units(remoteCarried);
        var firstContact = state.Base is null;
        var baseline = state.Base ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var push = new Dictionary<string, string?>(StringComparer.Ordinal);
        var incoming = 0;
        var newConflicts = 0;
        foreach (var key in remote.Keys.Union(local.Units.Keys).Union(baseline.Keys).Distinct(StringComparer.Ordinal))
        {
            // A managed key is the manifest's (AC-1507): never pushed, never taken over.
            if (_IsManaged(key, local.Managed) || state.Conflicts.ContainsKey(key))
            {
                continue;
            }

            var unit = remote.GetValueOrDefault(key) ?? local.Units.GetValueOrDefault(key);
            var isElement = unit?.IsElement ?? _IsElementKey(key);
            var theirs = remote.GetValueOrDefault(key)?.Text;
            var was = baseline.GetValueOrDefault(key);
            var mine = local.Units.GetValueOrDefault(key)?.Text;

            // A key taken over and then changed here before the next start: the base already moved, so it is a choice,
            // unless both now agree or the other side has no say in it.
            if (state.Incoming.Remove(key, out var taken))
            {
                if (_Same(mine, taken.Was))
                {
                    state.Incoming[key] = taken;
                    mine = taken.Value;
                }
                else if (!_Same(mine, theirs) && (theirs is not null || isElement))
                {
                    state.Conflicts[key] = new _Conflict(mine, theirs, taken.Steps);
                    newConflicts++;
                    continue;
                }
            }

            if (_Same(mine, theirs) || unit is null)
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
                state.Incoming[key] = new _Incoming(theirs, local.Units.GetValueOrDefault(key)?.Text, unit.Steps);
                incoming++;
            }
            else if (theirsChanged)
            {
                state.Conflicts[key] = new _Conflict(mine, theirs, unit.Steps);
                newConflicts++;
            }
        }

        var written = remoteBytes;
        if (push.Count > 0)
        {
            remoteDocument.Remove("Projects");
            remoteDocument.Remove("CategoryOrder");
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
            _SayFetched();
        }

        _SayConflicts(state, newConflicts > 0);
        return true;
    }

    // Lowercase hex SHA-256 of the blob, the version WriteIfUnchangedAsync is handed.
    internal static string Version(byte[] blob) => Convert.ToHexStringLower(SHA256.HashData(blob));

    private static async Task<bool> _WriteAsync(SettingsCarrierRegistration carrier, byte[] blob, string? version, CancellationToken cancellationToken)
    {
        if (carrier.WriteIfUnchangedAsync is { } conditional)
        {
            return await conditional(blob, version, cancellationToken).ConfigureAwait(false);
        }

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
            onConflicts([.. state.Conflicts.Select(pair => new SettingsConflict(pair.Key, pair.Value.Mine, pair.Value.Theirs))]);
        }
        else
        {
            _toasts?.Show($"{_Count(state.Conflicts.Count)} changed both here and on your other machine; nothing was overwritten.", ToastSeverity.Warning);
        }
    }

    // Once per pull that brought something new: the change waits for a restart, so it is never a surprise at the next start.
    private void _SayFetched()
    {
        var message = $"Settings fetched from {_carriers.Carrier?.DisplayName ?? "your other machine"}; active after a restart.";
        if (Restart is { } restart)
        {
            _toasts?.Show(message, ToastSeverity.Information, "Restart now", restart);
        }
        else
        {
            _toasts?.Show(message, ToastSeverity.Information);
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

    // The white list's cut, with every address cut to scheme, host and path: a token in a git URL stays here.
    private static JsonObject _Carried(JsonObject document)
    {
        var carried = JsonNode.Parse(SettingsCarrierWhitelist.Extract(document)) as JsonObject ?? new JsonObject();
        _StripCredentials(carried);
        return carried;
    }

    private static void _StripCredentials(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                _StripCredentials(item);
            }
        }

        if (node is not JsonObject obj)
        {
            return;
        }

        foreach (var (name, value) in obj.ToList())
        {
            if (value is JsonValue text && text.TryGetValue<string>(out var address) && _Bare(address) is { } bare)
            {
                obj[name] = bare;
            }
            else
            {
                _StripCredentials(value);
            }
        }
    }

    // Null when the text is no address with a user, password or query in it.
    private static string? _Bare(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.UserInfo.Length > 0 || uri.Query.Length > 1)
            ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : null;

    // Writes each taken-over key into its own top-level section, one key at a time; a key this build cannot write is
    // returned rather than thrown, so a value from a newer build never stops the cockpit from starting.
    private static List<string> _Apply(CockpitConfigFile file, IReadOnlyDictionary<string, _Incoming> values, ILogger logger)
    {
        var failed = new List<string>();
        foreach (var (key, taken) in values)
        {
            if (typeof(CockpitConfigFile).GetProperty(taken.Steps[0].Name) is not { CanWrite: true } property)
            {
                failed.Add(key);
                continue;
            }

            try
            {
                var root = new JsonObject { [property.Name] = JsonSerializer.SerializeToNode(property.GetValue(file), property.PropertyType, CockpitConfigFileAccess.SerializerOptions) };
                _Set(root, taken.Steps, taken.Value);
                property.SetValue(file, root[property.Name]?.Deserialize(property.PropertyType, CockpitConfigFileAccess.SerializerOptions));
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                logger.LogWarning("Could not take over {Key} from the settings carrier: {Reason}", key, exception.Message);
                failed.Add(key);
            }
        }

        return failed;
    }

    // A unit is a white-listed leaf as a whole, or one whole profile addressed by its label.
    private static Dictionary<string, _Unit> _Units(JsonObject carried)
    {
        var units = new Dictionary<string, _Unit>(StringComparer.Ordinal);
        _Walk(carried, [], string.Empty, units);
        return units;
    }

    // `pattern` is the white-list path the node sits at, with `*` for a dictionary key.
    private static void _Walk(JsonNode? node, List<_Step> steps, string pattern, Dictionary<string, _Unit> units)
    {
        if (steps.Count > 0 && SettingsCarrierWhitelist.IsLeaf(pattern))
        {
            units.TryAdd(_Key(steps), new _Unit(steps, node?.ToJsonString() ?? "null", false));
            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var (name, value) in obj)
            {
                var named = pattern.Length == 0 ? name : $"{pattern}.{name}";
                _Walk(value, [.. steps, new _Step(name, null)], SettingsCarrierWhitelist.Reaches(named) ? named : $"{pattern}.*", units);
            }

            return;
        }

        if (node is JsonArray array && steps.Count == 1)
        {
            foreach (var item in array)
            {
                if (_ElementId(item) is { } id)
                {
                    var at = new List<_Step> { steps[0] with { Element = id } };
                    units.TryAdd(_Key(at), new _Unit(at, item?.ToJsonString() ?? "null", true));
                }
            }
        }
    }

    // The lists whose items are synced one by one: those the white list reaches into with `[]`.
    private static bool _IsElementKey(string key) =>
        SettingsCarrierWhitelist.Carried.Any(path => path.Contains("[].", StringComparison.Ordinal) && key.StartsWith(path[..path.IndexOf("[]", StringComparison.Ordinal)] + ".", StringComparison.Ordinal));

    // A manifest may manage a whole section or dictionary as well as one leaf in it.
    private static bool _IsManaged(string key, HashSet<string> managed) =>
        managed.Contains(key) || managed.Any(path => key.StartsWith(path + ".", StringComparison.Ordinal));

    private static string? _ElementId(JsonNode? item) =>
        item is JsonObject obj && (obj["Id"] ?? obj["Label"]) is JsonValue id && id.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static string _Key(IEnumerable<_Step> steps) => string.Join('.', steps.Select(step => step.Element is null ? step.Name : $"{step.Name}.{step.Element}"));

    private static List<_Step> _UnitFor(string key, _Local local, Dictionary<string, _Unit> remote) =>
        (local.Units.GetValueOrDefault(key) ?? remote.GetValueOrDefault(key))?.Steps
        ?? throw new InvalidOperationException($"No unit for {key}.");

    // Null removes. A profile is laid over what is there, so what only this machine has stays as it is.
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
            _Overlay(target, source, $"{last.Name}[]");
        }
        else
        {
            list.Add(value);
        }
    }

    // A white-listed leaf is replaced whole, so an entry removed from a dictionary in it stays removed; only the objects
    // that lead to a leaf are walked into.
    private static void _Overlay(JsonObject target, JsonObject source, string pattern)
    {
        foreach (var (name, value) in source)
        {
            var path = $"{pattern}.{name}";
            if (!SettingsCarrierWhitelist.IsLeaf(path) && value is JsonObject inner && target[name] is JsonObject existing)
            {
                _Overlay(existing, inner, path);
            }
            else if (target[name] is JsonValue kept && kept.TryGetValue<string>(out var address) && _Bare(address) is { } bare
                && value is JsonValue incoming && incoming.TryGetValue<string>(out var same) && same == bare)
            {
                // The same address without the user or query this machine keeps for it: what is here stays.
                continue;
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
            var state = File.Exists(statePath) ? JsonSerializer.Deserialize<_State>(File.ReadAllText(statePath), StateJson) ?? new _State() : new _State();
            // AC-1861: old project sync state must never add, change or delete local projects.
            foreach (var key in (state.Base?.Keys ?? Enumerable.Empty<string>()).Union(state.Incoming.Keys).Union(state.Conflicts.Keys).ToList())
            {
                if (key is "Projects" or "CategoryOrder" || key.StartsWith("Projects.", StringComparison.Ordinal))
                {
                    state.Base?.Remove(key);
                    state.Incoming.Remove(key);
                    state.Conflicts.Remove(key);
                }
            }

            return state;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
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

    private sealed record _Incoming(string? Value, string? Was, List<_Step> Steps);

    private sealed record _Conflict(string? Mine, string? Theirs, List<_Step> Steps);

    private sealed class _State
    {
        // Null: this machine never agreed with the carrier yet.
        public Dictionary<string, string>? Base { get; set; }

        public Dictionary<string, _Incoming> Incoming { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, _Conflict> Conflicts { get; set; } = new(StringComparer.Ordinal);
    }
}
