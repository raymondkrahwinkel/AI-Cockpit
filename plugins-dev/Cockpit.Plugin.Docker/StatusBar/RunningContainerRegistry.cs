using System.Collections.Concurrent;
using Cockpit.Plugins.Abstractions.StatusBar;
using Cockpit.Plugin.Docker.Engine;

namespace Cockpit.Plugin.Docker.StatusBar;

/// <summary>
/// Tracks the detached containers this plugin started (<c>docker run -d</c>) and surfaces them in the status bar
/// (AC-82), mirroring the Kubernetes plugin's <c>PortForwardManager</c>. Each shows name · image · ports · session ·
/// uptime with an <b>operator-only</b> Kill: the host renders the Kill button and invokes <c>StopAsync</c>; an agent
/// has no path to it (there is no MCP tool that reaches this — the agent can only stop a container through the gated
/// <c>stop_container</c>/<c>remove_container</c> tools, which ask for consent).
/// </summary>
internal sealed class RunningContainerRegistry(IDockerEngine engine, Func<DateTimeOffset> clock) : ISupervisedActivitySource
{
    private readonly ConcurrentDictionary<string, TrackedContainer> _containers = new(StringComparer.Ordinal);

    public string Label => "Docker containers";

    public event Action? Changed;

    /// <summary>Record a container this plugin just started, so it appears in the status bar.</summary>
    public void Track(string id, string name, string image, string ports, string session)
    {
        _containers[id] = new TrackedContainer(id, name, image, ports, session, clock());
        Changed?.Invoke();
    }

    public IReadOnlyList<SupervisedActivity> Snapshot() =>
        _containers.Values
            .OrderBy(container => container.StartedAt)
            .Select(container => new SupervisedActivity(
                Id: container.Id,
                Title: string.IsNullOrEmpty(container.Name) ? _ShortId(container.Id) : container.Name,
                Details:
                [
                    new ActivityDetail("Image", container.Image),
                    new ActivityDetail("Ports", string.IsNullOrEmpty(container.Ports) ? "—" : container.Ports),
                    new ActivityDetail("Session", container.Session),
                    new ActivityDetail("Uptime", _Uptime(clock() - container.StartedAt)),
                ],
                StopAsync: () => _StopAsync(container.Id)))
            .ToList();

    private async Task _StopAsync(string id)
    {
        try
        {
            await engine.RemoveContainerAsync(id, force: true, CancellationToken.None);
        }
        catch (Exception)
        {
            // Best-effort: an already-gone container (e.g. the Kill button pressed twice) must not surface an error.
        }
        finally
        {
            if (_containers.TryRemove(id, out _))
            {
                Changed?.Invoke();
            }
        }
    }

    private static string _ShortId(string id) => id.Length > 12 ? id[..12] : id;

    private static string _Uptime(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h{span.Minutes:D2}m"
            : $"{span.Minutes}m{span.Seconds:D2}s";
    }

    private sealed record TrackedContainer(
        string Id, string Name, string Image, string Ports, string Session, DateTimeOffset StartedAt);
}
