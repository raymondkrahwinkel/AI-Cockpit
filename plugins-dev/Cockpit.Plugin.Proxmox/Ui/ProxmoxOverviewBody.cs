using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Cockpit.Plugin.Proxmox.Engine;
using Cockpit.Plugin.Proxmox.Security;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Proxmox.Ui;

// The Proxmox overview workspace (AC-1038): a read-only view of nodes, VMs, LXC containers and storage, plus the
// start/shutdown/stop buttons a snapshot of running infrastructure invites. It is not a second way to reach the
// API — every read and every button click goes through the same `gate`/`engine` the MCP tools use.
internal sealed class ProxmoxOverviewBody : UserControl
{
    private readonly ProxmoxAccessGate _gate;
    private readonly IProxmoxEngine _engine;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _statusText = new() { FontSize = 11, Opacity = 0.7 };
    private readonly Button _refreshButton = new() { Content = "Refresh" };
    private readonly StackPanel _clusterPanel = new() { Spacing = 2 };
    private readonly StackPanel _nodesPanel = new() { Spacing = 4 };
    private readonly StackPanel _vmsPanel = new() { Spacing = 4 };
    private readonly StackPanel _lxcPanel = new() { Spacing = 4 };
    private readonly StackPanel _storagePanel = new() { Spacing = 4 };
    private bool _isLoading;

    public ProxmoxOverviewBody(IWorkspaceContext context, ProxmoxAccessGate gate, IProxmoxEngine engine)
    {
        _gate = gate;
        _engine = engine;

        _refreshButton.Click += async (_, _) => await _RefreshAsync();
        context.RefreshRequested += async (_, _) => await _RefreshAsync();
        context.Closed += (_, _) => _lifetime.Cancel();

        var header = new DockPanel { Margin = new Thickness(12, 12, 12, 4) };
        var title = new TextBlock { Text = "Proxmox", FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 15 };
        DockPanel.SetDock(_refreshButton, Dock.Right);
        header.Children.Add(_refreshButton);
        header.Children.Add(title);

        var body = new StackPanel
        {
            Margin = new Thickness(12, 0, 12, 12),
            Spacing = 10,
            Children =
            {
                _statusText,
                _Section("Cluster", _clusterPanel),
                _Section("Nodes", _nodesPanel),
                _Section("VMs", _vmsPanel),
                _Section("LXC containers", _lxcPanel),
                _Section("Storage", _storagePanel),
            },
        };

        Content = new DockPanel
        {
            LastChildFill = true,
            Children = { header, new ScrollViewer { Content = body } },
        };
        DockPanel.SetDock(header, Dock.Top);

        _ = _RefreshAsync();
    }

    private async Task _RefreshAsync()
    {
        if (_isLoading || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _isLoading = true;
        _refreshButton.IsEnabled = false;
        _statusText.Text = "Loading…";

        var decision = await _gate.AuthorizeConnectionAsync("show the Proxmox overview", paneId: null);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            _statusText.Text = reason;
            _isLoading = false;
            _refreshButton.IsEnabled = true;
            return;
        }

        try
        {
            var token = _lifetime.Token;
            var clusterTask = _engine.GetClusterInfoAsync(token);
            var nodesTask = _engine.ListNodesAsync(token);
            var vmsTask = _engine.ListVmsAsync(token);
            var lxcTask = _engine.ListLxcAsync(token);
            var storageTask = _engine.ListStorageAsync(token);
            await Task.WhenAll(clusterTask, nodesTask, vmsTask, lxcTask, storageTask);

            _RenderCluster(clusterTask.Result);
            _RenderNodes(nodesTask.Result);
            _RenderGuests(_vmsPanel, vmsTask.Result, isLxc: false);
            _RenderGuests(_lxcPanel, lxcTask.Result, isLxc: true);
            _RenderStorage(storageTask.Result);
            _statusText.Text = $"Updated {DateTimeOffset.Now:T}";
        }
        catch (OperationCanceledException)
        {
            // The workspace closed mid-refresh; nothing left to show it to.
        }
        catch (Exception ex)
        {
            _statusText.Text = ex is ProxmoxApiException apiEx ? apiEx.Message : $"The Proxmox request failed ({ex.GetType().Name}).";
        }
        finally
        {
            _isLoading = false;
            _refreshButton.IsEnabled = true;
        }
    }

    private void _RenderCluster(ProxmoxClusterInfo info)
    {
        _clusterPanel.Children.Clear();
        _clusterPanel.Children.Add(new TextBlock
        {
            Text = info.IsCluster
                ? $"Cluster \"{info.Name}\" — {(info.Quorate ? "quorate" : "NOT quorate")}, {info.NodeCount} node(s)"
                : "Single host (not a cluster)",
        });
    }

    private void _RenderNodes(IReadOnlyList<ProxmoxNode> nodes)
    {
        _nodesPanel.Children.Clear();
        foreach (var node in nodes)
        {
            _nodesPanel.Children.Add(new TextBlock
            {
                Text = $"{node.Node} — {node.Status}, CPU {node.CpuUsage:0.#}% of {node.MaxCpu}, mem {_Bytes(node.MemUsed)}/{_Bytes(node.MemMax)}, up {_Uptime(node.Uptime)}",
            });
        }

        if (nodes.Count == 0)
        {
            _nodesPanel.Children.Add(new TextBlock { Text = "No nodes.", Opacity = 0.7 });
        }
    }

    private void _RenderGuests(StackPanel panel, IReadOnlyList<ProxmoxGuest> guests, bool isLxc)
    {
        panel.Children.Clear();
        foreach (var guest in guests)
        {
            panel.Children.Add(_GuestRow(guest, isLxc));
        }

        if (guests.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = isLxc ? "No LXC containers." : "No VMs.", Opacity = 0.7 });
        }
    }

    private Control _GuestRow(ProxmoxGuest guest, bool isLxc)
    {
        var label = new TextBlock
        {
            Text = $"{guest.VmId} {guest.Name} ({guest.Node}) — {guest.Status}, CPU {guest.MaxCpu:0.#}, mem {_Bytes(guest.MaxMem)}, disk {_Bytes(guest.MaxDisk)}, up {_Uptime(guest.Uptime)}",
            VerticalAlignment = VerticalAlignment.Center,
        };

        var running = string.Equals(guest.Status, "running", StringComparison.Ordinal);
        var start = new Button { Content = "Start", IsVisible = !running };
        var shutdown = new Button { Content = "Shutdown", IsVisible = running, Margin = new Thickness(4, 0, 0, 0) };
        var stop = new Button { Content = "Stop", IsVisible = running, Margin = new Thickness(4, 0, 0, 0) };

        start.Click += async (_, _) => await _ActAsync(
            isLxc ? ProxmoxActionText.StartLxc(guest.Node, guest.VmId) : ProxmoxActionText.StartVm(guest.Node, guest.VmId),
            ct => isLxc ? _engine.StartLxcAsync(guest.Node, guest.VmId, ct) : _engine.StartVmAsync(guest.Node, guest.VmId, ct));
        shutdown.Click += async (_, _) => await _ActAsync(
            isLxc ? ProxmoxActionText.ShutdownLxc(guest.Node, guest.VmId) : ProxmoxActionText.ShutdownVm(guest.Node, guest.VmId),
            ct => isLxc ? _engine.ShutdownLxcAsync(guest.Node, guest.VmId, ct) : _engine.ShutdownVmAsync(guest.Node, guest.VmId, ct));
        stop.Click += async (_, _) => await _ActAsync(
            isLxc ? ProxmoxActionText.StopLxc(guest.Node, guest.VmId) : ProxmoxActionText.StopVm(guest.Node, guest.VmId),
            ct => isLxc ? _engine.StopLxcAsync(guest.Node, guest.VmId, ct) : _engine.StopVmAsync(guest.Node, guest.VmId, ct));

        var row = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { start, shutdown, stop } };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(label);
        return row;
    }

    // Every button routes through the same gate as the matching MCP tool, with the exact same consent text
    // (`ProxmoxActionText`) — asked afresh on every click, never remembered, exactly like the tool.
    private async Task _ActAsync(string operation, Func<CancellationToken, Task<ProxmoxTaskOutcome>> action)
    {
        var decision = await _gate.AuthorizeMutationAsync(operation, paneId: null);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            _statusText.Text = reason;
            return;
        }

        _statusText.Text = $"Running: {operation}…";
        try
        {
            var outcome = await action(_lifetime.Token);
            _statusText.Text = outcome.TimedOut
                ? $"Still running (upid={outcome.Upid})."
                : outcome.IsSuccess ? "Done." : $"Failed: {outcome.ExitStatus}";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _statusText.Text = ex is ProxmoxApiException apiEx ? apiEx.Message : $"The Proxmox request failed ({ex.GetType().Name}).";
        }

        await _RefreshAsync();
    }

    private void _RenderStorage(IReadOnlyList<ProxmoxStoragePool> pools)
    {
        _storagePanel.Children.Clear();
        foreach (var pool in pools)
        {
            var percent = pool.TotalBytes > 0 ? pool.UsedBytes * 100.0 / pool.TotalBytes : 0;
            _storagePanel.Children.Add(new TextBlock
            {
                Text = $"{pool.Storage} ({pool.Node}, {pool.Type}) — {_Bytes(pool.UsedBytes)}/{_Bytes(pool.TotalBytes)} ({percent:0.#}%){(pool.Enabled ? "" : ", disabled")}",
            });
        }

        if (pools.Count == 0)
        {
            _storagePanel.Children.Add(new TextBlock { Text = "No storage pools.", Opacity = 0.7 });
        }
    }

    private static Control _Section(string title, Control content) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold }, content },
    };

    private static string _Bytes(long bytes) => bytes <= 0 ? "0 B" : $"{bytes / 1024.0 / 1024.0 / 1024.0:0.#} GB";

    private static string _Uptime(long seconds)
    {
        if (seconds <= 0)
        {
            return "-";
        }

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h" : $"{span.Hours}h {span.Minutes}m";
    }
}
