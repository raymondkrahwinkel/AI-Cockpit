using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Cockpit.Plugin.Proxmox.Contracts;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Proxmox.UI;

// The Proxmox overview workspace (AC-1038): a read-only view of nodes, VMs, LXC containers and storage, plus the
// start/shutdown/stop buttons a snapshot of running infrastructure invites. It is not a second way to reach the
// API — every read and every button click goes through the backend part's channel handlers, which run the same
// `gate`/`engine` the MCP tools use (AC-1394: this assembly runs no Proxmox call itself).
internal sealed class ProxmoxOverviewBody : UserControl
{
    private readonly ICockpitUiHost _host;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _statusText = new() { FontSize = 11, Opacity = 0.7 };
    private readonly Button _refreshButton = new() { Content = "Refresh" };
    private readonly StackPanel _clusterPanel = new() { Spacing = 2 };
    private readonly StackPanel _nodesPanel = new() { Spacing = 4 };
    private readonly StackPanel _vmsPanel = new() { Spacing = 4 };
    private readonly StackPanel _lxcPanel = new() { Spacing = 4 };
    private readonly StackPanel _storagePanel = new() { Spacing = 4 };
    private bool _isLoading;

    public ProxmoxOverviewBody(IWorkspaceContext context, ICockpitUiHost host)
    {
        _host = host;

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

        try
        {
            var answer = await _AskAsync<ProxmoxEmptyRequest, ProxmoxOverviewAnswer>(ProxmoxChannel.Overview, new ProxmoxEmptyRequest());
            if (answer.DeniedReason is { } denied)
            {
                _statusText.Text = denied;
                return;
            }

            if (answer.ErrorMessage is { } error)
            {
                _statusText.Text = error;
                return;
            }

            _RenderCluster(answer.Cluster ?? throw new InvalidOperationException("The backend part answered no cluster data."));
            _RenderNodes(answer.Nodes ?? []);
            _RenderGuests(_vmsPanel, answer.Vms ?? [], isLxc: false);
            _RenderGuests(_lxcPanel, answer.Lxc ?? [], isLxc: true);
            _RenderStorage(answer.Storage ?? []);
            _statusText.Text = $"Updated {DateTimeOffset.Now:T}";
        }
        catch (OperationCanceledException)
        {
            // The workspace closed mid-refresh; nothing left to show it to.
        }
        catch (Exception ex)
        {
            _statusText.Text = $"The Proxmox request failed ({ex.GetType().Name}).";
        }
        finally
        {
            _isLoading = false;
            _refreshButton.IsEnabled = true;
        }
    }

    private void _RenderCluster(ProxmoxClusterData info)
    {
        _clusterPanel.Children.Clear();
        _clusterPanel.Children.Add(new TextBlock
        {
            Text = info.IsCluster
                ? $"Cluster \"{info.Name}\" — {(info.Quorate ? "quorate" : "NOT quorate")}, {info.NodeCount} node(s)"
                : "Single host (not a cluster)",
        });
    }

    private void _RenderNodes(IReadOnlyList<ProxmoxNodeData> nodes)
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

    private void _RenderGuests(StackPanel panel, IReadOnlyList<ProxmoxGuestData> guests, bool isLxc)
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

    private Control _GuestRow(ProxmoxGuestData guest, bool isLxc)
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

        start.Click += async (_, _) => await _ActAsync(ProxmoxChannel.StartGuest, guest.Node, guest.VmId, isLxc, "start");
        shutdown.Click += async (_, _) => await _ActAsync(ProxmoxChannel.ShutdownGuest, guest.Node, guest.VmId, isLxc, "gracefully shut down");
        stop.Click += async (_, _) => await _ActAsync(ProxmoxChannel.StopGuest, guest.Node, guest.VmId, isLxc, "hard power off");

        var row = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { start, shutdown, stop } };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(label);
        return row;
    }

    // Every button routes through the same gate as the matching MCP tool: the backend handler composes the exact
    // consent text (`ProxmoxActionText`) and asks afresh on every click, never remembered, exactly like the tool.
    // `verb` here is only for the optimistic "Running: …" status line, shown before the answer (and its possible
    // denial) comes back.
    private async Task _ActAsync(string action, string node, string vmId, bool isLxc, string verb)
    {
        _statusText.Text = $"Running: {verb} {(isLxc ? "LXC container" : "VM")} {vmId} on node \"{node}\"…";

        ProxmoxActionAnswer answer;
        try
        {
            answer = await _AskAsync<ProxmoxGuestActionRequest, ProxmoxActionAnswer>(action, new ProxmoxGuestActionRequest(node, vmId, isLxc));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _statusText.Text = $"The Proxmox request failed ({ex.GetType().Name}).";
            await _RefreshAsync();
            return;
        }

        if (answer.DeniedReason is { } denied)
        {
            _statusText.Text = denied;
            return;
        }

        if (answer.ErrorMessage is { } error)
        {
            _statusText.Text = error;
            await _RefreshAsync();
            return;
        }

        var outcome = answer.Outcome ?? throw new InvalidOperationException("The backend part answered no task outcome.");
        _statusText.Text = outcome.TimedOut
            ? $"Still running (upid={outcome.Upid})."
            : outcome.IsSuccess ? "Done." : $"Failed: {outcome.ExitStatus}";
        await _RefreshAsync();
    }

    private void _RenderStorage(IReadOnlyList<ProxmoxStorageData> pools)
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

    private async Task<TAnswer> _AskAsync<TRequest, TAnswer>(string action, TRequest request)
    {
        var payload = JsonSerializer.SerializeToElement(request, ProxmoxChannel.Json);
        var answer = await _host.Channel.InvokeAsync(action, payload, _lifetime.Token);
        return answer.Deserialize<TAnswer>(ProxmoxChannel.Json)
            ?? throw new InvalidOperationException($"The backend part answered no data for '{action}'.");
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
