using System.Text.Json;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Configuration;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Layout;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Workspaces;

namespace Cockpit.Infrastructure.Hosting;

// AC-2039: a crashed start, even a native crash (AC-2038), leaves `startup-attempt.json` for the next start to read. A
// plugin is turned off only after two consecutive interrupted starts name it; two unnamed crashes in a row mean safe
// mode (AC-478). Removed once stable or on quit.
public sealed class StartupAttempt
{
    public const string FileName = "startup-attempt.json";

    // After the restore. A crash later than this is not a start that keeps the operator out.
    public static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly HashSet<string> _restoredSurfaces;

    // Null once the start is complete, or when the marker could not be written: then there is nothing to keep up to date.
    private Marker? _marker;

    private StartupAttempt(string path, ILogger logger, Marker? marker, HashSet<string> restoredSurfaces, IReadOnlyList<DisabledPlugin> disabled)
    {
        _path = path;
        _logger = logger;
        _marker = marker;
        _restoredSurfaces = restoredSurfaces;
        Disabled = disabled;
        SafeMode = marker?.Attempts > 2;
    }

    // The plugins this start turned off because the last one crashed while restoring their surface.
    public IReadOnlyList<DisabledPlugin> Disabled { get; }

    // Two unfinished starts in a row before this one, and none of them named a plugin.
    public bool SafeMode { get; }

    // Before the plugins load, so a plugin turned off here is not loaded by this start. Blocks on its file reads, as the
    // plugin pass beside it does. Never throws: a guard that cannot read or write its file must not be what stops a start.
    public static StartupAttempt Begin(ILogger logger)
    {
        var path = Path.Combine(CockpitBuild.StateRoot, FileName);
        try
        {
            var previous = _Read(path);
            var disabled = previous is null ? [] : _Disable(_RepeatedPlugins(previous));

            // A start that turned its cause off counts afresh; one that could not keeps counting toward safe mode.
            var attempts = previous is null || disabled.Count > 0 ? 1 : previous.Attempts + 1;
            if (previous is not null)
            {
                if (disabled.Count > 0)
                {
                    logger.LogWarning(
                        "The previous two starts did not finish while reopening the same plugin; turned off: [{Disabled}].",
                        string.Join(", ", disabled.Select(plugin => plugin.Id)));
                }
                else
                {
                    logger.LogWarning(
                        "The previous start did not finish ({Attempts} in a row); no plugin was turned off without a second matching start.",
                        previous.Attempts);
                }
            }

            var layout = new LayoutSettingsStore().LoadAsync().GetAwaiter().GetResult();
            var desks = new WorkspaceSettingsStore().LoadAsync().GetAwaiter().GetResult();
            HashSet<string> restored = [.. new[] { layout.OpenDockPanelId, layout.SimpleOpenDockPanelId }.OfType<string>(),
                .. layout.FloatingDockPanelIds ?? [], .. desks.Workspaces.Select(desk => desk.Type.Id)];

            var marker = new Marker(attempts, [], previous?.Plugins ?? []);
            CockpitConfigPath.ReplaceAtomicallyPrivate(path, JsonSerializer.Serialize(marker));
            return new StartupAttempt(path, logger, marker, restored, disabled);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The startup marker could not be kept; a crash in this start will not be caught by the next.");
            return new StartupAttempt(path, logger, null, [], []);
        }
    }

    // A plugin registers a dock panel or workspace type: if this start restores it, the plugin goes into the marker
    // before its view can be built.
    public void NoteSurface(string surfaceId, string pluginId, string pluginName)
    {
        lock (_gate)
        {
            if (_marker is null || !_restoredSurfaces.Contains(surfaceId) || _marker.Plugins.Any(plugin => plugin.Id == pluginId))
            {
                return;
            }

            _marker = _marker with { Plugins = [.. _marker.Plugins, new NamedPlugin(pluginId, pluginName)] };
            try
            {
                CockpitConfigPath.ReplaceAtomicallyPrivate(_path, JsonSerializer.Serialize(_marker));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not note {PluginId} in the startup marker.", pluginId);
            }
        }
    }

    // A clean quit or restart, or a stable start: the next start is an ordinary one. Once only, so a late timer of this
    // start never removes a marker a later start wrote.
    public void Complete()
    {
        lock (_gate)
        {
            if (_marker is null)
            {
                return;
            }

            _marker = null;
            try
            {
                File.Delete(_path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not remove the startup marker; the next start will read this one as unfinished.");
            }
        }
    }

    public async Task CompleteAfterAsync(TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        Complete();
    }

    // A marker that cannot be read was still left behind: an unfinished start that names nobody.
    private static Marker? _Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Marker>(File.ReadAllText(path)) ?? new Marker(1, []);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new Marker(1, []);
        }
    }

    // Only a plugin that is on: one already off was not what crashed, and turning nothing off counts toward safe mode.
    private static List<DisabledPlugin> _Disable(IReadOnlyList<NamedPlugin> plugins)
    {
        var registrations = new PluginRegistrationStore();
        var saved = registrations.LoadAllAsync().GetAwaiter().GetResult();
        var disabled = new List<DisabledPlugin>();
        foreach (var plugin in plugins)
        {
            if (saved.TryGetValue(plugin.Id, out var registration) && registration.Enabled)
            {
                registrations.SaveAsync(plugin.Id, registration with { Enabled = false, DisabledByStartupProtection = true }).GetAwaiter().GetResult();
                disabled.Add(new DisabledPlugin(plugin.Id, plugin.Name, registration.PinnedSha256));
            }
        }

        return disabled;
    }

    // A plugin is evidence only when the immediately preceding two incomplete starts both named it. Old marker files
    // have no PreviousPlugins and therefore never turn anything off on their own.
    private static IReadOnlyList<NamedPlugin> _RepeatedPlugins(Marker marker)
    {
        var previous = marker.PreviousPlugins;
        if (previous is not { Count: > 0 } || marker.Plugins.Count == 0)
        {
            return [];
        }

        var previousIds = previous.Select(plugin => plugin.Id).ToHashSet(StringComparer.Ordinal);
        return [.. marker.Plugins.Where(plugin => previousIds.Contains(plugin.Id))];
    }

    private sealed record Marker(int Attempts, IReadOnlyList<NamedPlugin> Plugins, IReadOnlyList<NamedPlugin>? PreviousPlugins = null);

    private sealed record NamedPlugin(string Id, string Name);
}

// What "Turn back on" needs: the same bytes the operator had trusted before the crash.
public sealed record DisabledPlugin(string Id, string Name, string PinnedSha256);
