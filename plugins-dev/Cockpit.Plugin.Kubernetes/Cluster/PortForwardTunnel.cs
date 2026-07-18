using System.Net.Sockets;
using k8s;

namespace Cockpit.Plugin.Kubernetes.Cluster;

/// <summary>
/// One active port-forward tunnel (AC-80): a local loopback listener bridged to a pod port through a dedicated
/// client the tunnel owns. Held by <see cref="PortForwardManager"/> so it shows in the status bar with the operator's
/// Kill; stopping it cancels the accept loop and any live connections, closes the listener, and disposes the client.
/// </summary>
internal sealed class PortForwardTunnel
{
    private readonly CancellationTokenSource _cancellation;
    private readonly TcpListener _listener;

    // The tunnel owns this client (built dedicated, not from the shared cache), so a settings-save InvalidateAll
    // cannot dispose it out from under a live tunnel; the tunnel disposes it itself on StopAsync.
    private readonly IKubernetes _client;

    public PortForwardTunnel(string id, string clusterLabel, string @namespace, string pod, int localPort, int remotePort, TcpListener listener, CancellationTokenSource cancellation, IKubernetes client)
    {
        Id = id;
        ClusterLabel = clusterLabel;
        Namespace = @namespace;
        Pod = pod;
        LocalPort = localPort;
        RemotePort = remotePort;
        _listener = listener;
        _cancellation = cancellation;
        _client = client;
    }

    public string Id { get; }

    public string ClusterLabel { get; }

    public string Namespace { get; }

    public string Pod { get; }

    public int LocalPort { get; }

    public int RemotePort { get; }

    public CancellationToken CancellationToken => _cancellation.Token;

    public TcpListener Listener => _listener;

    public async Task StopAsync()
    {
        try
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already cancelled or disposed — closing the listener below still tears the tunnel down.
        }

        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
            // Listener already stopped.
        }

        try
        {
            _client.Dispose();
        }
        catch (Exception)
        {
            // Best-effort: a dispose fault must not break teardown of the rest of the tunnel.
        }

        _cancellation.Dispose();
    }
}
