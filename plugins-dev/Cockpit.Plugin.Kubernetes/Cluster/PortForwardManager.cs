using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using k8s;
using Cockpit.Plugins.Abstractions.StatusBar;

namespace Cockpit.Plugin.Kubernetes.Cluster;

/// <summary>
/// Owns the active port-forward tunnels (AC-80) and surfaces them to the cockpit status bar as supervised activities
/// (<see cref="ISupervisedActivitySource"/>, AC-82): each tunnel shows source/target/cluster and an operator-only
/// Kill. A tunnel is opened only after the danger-gate approved it. Each accepted local connection gets its own
/// WebSocket to the pod (the way kubectl multiplexes), so several clients can use one tunnel; a bounded lifetime
/// closes it as a backstop.
/// </summary>
internal sealed class PortForwardManager : ISupervisedActivitySource
{
    private const string PortForwardSubProtocol = "v4.channel.k8s.io";

    private readonly ConcurrentDictionary<string, PortForwardTunnel> _tunnels = new(StringComparer.Ordinal);

    public string Label => "Port-forwards";

    public event Action? Changed;

    public IReadOnlyList<SupervisedActivity> Snapshot() =>
        _tunnels.Values
            .Select(tunnel => new SupervisedActivity(
                tunnel.Id,
                $"{tunnel.Pod}  127.0.0.1:{tunnel.LocalPort} → {tunnel.RemotePort}",
                [
                    new ActivityDetail("cluster", tunnel.ClusterLabel),
                    new ActivityDetail("namespace", tunnel.Namespace),
                    new ActivityDetail("pod", tunnel.Pod),
                    new ActivityDetail("local", $"127.0.0.1:{tunnel.LocalPort}"),
                    new ActivityDetail("remote port", tunnel.RemotePort.ToString()),
                ],
                () => StopAsync(tunnel.Id)))
            .ToList();

    /// <summary>
    /// Opens a tunnel from a loopback port to <paramref name="remotePort"/> on the pod. A <paramref name="requestedLocalPort"/>
    /// of 0 lets the OS pick a free port. Returns the tunnel (with its bound local port); it runs until it is killed
    /// or <paramref name="maxLifetime"/> elapses.
    /// </summary>
    public PortForwardTunnel Start(IKubernetes client, string clusterLabel, string @namespace, string pod, int remotePort, int requestedLocalPort, TimeSpan maxLifetime)
    {
        var cancellation = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, requestedLocalPort);
        listener.Start();
        var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        var tunnel = new PortForwardTunnel(Guid.NewGuid().ToString("n"), clusterLabel, @namespace, pod, localPort, remotePort, listener, cancellation);
        _tunnels[tunnel.Id] = tunnel;

        _ = _AcceptLoopAsync(tunnel, client);
        _ = _CloseAfterAsync(tunnel.Id, maxLifetime, cancellation.Token);
        Changed?.Invoke();
        return tunnel;
    }

    public async Task StopAsync(string id)
    {
        if (_tunnels.TryRemove(id, out var tunnel))
        {
            await tunnel.StopAsync();
            Changed?.Invoke();
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var id in _tunnels.Keys.ToArray())
        {
            await StopAsync(id);
        }
    }

    private async Task _AcceptLoopAsync(PortForwardTunnel tunnel, IKubernetes client)
    {
        var token = tunnel.CancellationToken;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var tcp = await tunnel.Listener.AcceptTcpClientAsync(token);
                _ = _PumpAsync(tcp, client, tunnel, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Killed or timed out — expected.
        }
        catch (Exception)
        {
            // The listener faulted (e.g. closed under us); reconcile by removing the tunnel below.
        }
        finally
        {
            await StopAsync(tunnel.Id);
        }
    }

    // Each local connection gets its own WebSocket + demuxer to the pod, so concurrent clients do not share one
    // stream. Both directions are copied until either side closes or the tunnel is killed.
    private static async Task _PumpAsync(TcpClient tcp, IKubernetes client, PortForwardTunnel tunnel, CancellationToken token)
    {
        using (tcp)
        {
            try
            {
                var socket = await ((k8s.Kubernetes)client).WebSocketNamespacedPodPortForwardAsync(
                    tunnel.Pod, tunnel.Namespace, [tunnel.RemotePort], PortForwardSubProtocol, cancellationToken: token);
                // ownsSocket: true so disposing the demuxer disposes the WebSocket — without it the apiserver socket
                // leaks on every accepted connection.
                using var demuxer = new StreamDemuxer(socket, StreamType.PortForward, ownsSocket: true);
                demuxer.Start();
                using var podStream = demuxer.GetStream((byte?)0, (byte?)0);
                var tcpStream = tcp.GetStream();

                // Copy both directions until one side closes; disposing the streams (the usings) unblocks the other.
                var toPod = tcpStream.CopyToAsync(podStream, token);
                var fromPod = podStream.CopyToAsync(tcpStream, token);
                await Task.WhenAny(toPod, fromPod);

                // Observe both tasks so a broken pipe on the finishing side is not an unobserved exception.
                _ = toPod.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
                _ = fromPod.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
            }
            catch (Exception)
            {
                // A failed connection closes its own tcp client (the using) and leaves the tunnel up for the next one.
            }
        }
    }

    private async Task _CloseAfterAsync(string id, TimeSpan lifetime, CancellationToken token)
    {
        try
        {
            await Task.Delay(lifetime, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await StopAsync(id);
    }
}
