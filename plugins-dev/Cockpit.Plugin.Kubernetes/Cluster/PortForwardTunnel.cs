using System.Net.Sockets;

namespace Cockpit.Plugin.Kubernetes.Cluster;

/// <summary>
/// One active port-forward tunnel (AC-80): a local loopback listener bridged to a pod port through the plugin-held
/// client. Held by <see cref="PortForwardManager"/> so it shows in the status bar with the operator's Kill; stopping
/// it cancels the accept loop and any live connections and closes the listener.
/// </summary>
internal sealed class PortForwardTunnel
{
    private readonly CancellationTokenSource _cancellation;
    private readonly TcpListener _listener;

    public PortForwardTunnel(string id, string clusterLabel, string @namespace, string pod, int localPort, int remotePort, TcpListener listener, CancellationTokenSource cancellation)
    {
        Id = id;
        ClusterLabel = clusterLabel;
        Namespace = @namespace;
        Pod = pod;
        LocalPort = localPort;
        RemotePort = remotePort;
        _listener = listener;
        _cancellation = cancellation;
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
            await _cancellation.CancelAsync();
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

        _cancellation.Dispose();
    }
}
