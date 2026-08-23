using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text.Json;
using Cockpit.Plugin.Proxmox.Settings;

namespace Cockpit.Plugin.Proxmox.Engine;

// `IProxmoxEngine` backed directly by `HttpClient` against the Proxmox REST API (no community client library — the
// API is a well-documented JSON-Schema-driven REST surface, and a token needs no CSRF/ticket dance). The client is
// built lazily from the configured host/port/token and cached; a settings save calls `Invalidate` so the next call
// rebuilds against the new target. Mirrors `DockerEngine`'s caching shape.
internal sealed class ProxmoxEngine(ProxmoxSettings settings) : IProxmoxEngine, IDisposable
{
    private static readonly TimeSpan TaskPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TaskTimeout = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private HttpClient? _client;

    private HttpClient _Client()
    {
        if (!settings.IsConfigured)
        {
            throw new ProxmoxApiException("No Proxmox target is configured yet. Set the host, port and API token in the plugin settings.");
        }

        lock (_lock)
        {
            if (_client is not null)
            {
                return _client;
            }

            var handler = new HttpClientHandler
            {
                // Trust-on-first-use, never accept-all: the operator confirms a fingerprint once in the settings UI
                // (see `ProxmoxCertificateProbe`), and every connection after that is checked against exactly that
                // fingerprint — read live from settings, so a re-trust takes effect without rebuilding the client.
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && ProxmoxCertificateProbe.Fingerprint(certificate) == settings.TrustedCertFingerprint,
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri($"https://{settings.Host}:{settings.Port}/api2/json/"),
                Timeout = TimeSpan.FromSeconds(20),
            };

            // Proxmox's own scheme: the whole "PVEAPIToken=user@realm!id=secret" is the header value, not a
            // standard "scheme value" pair — TryAddWithoutValidation avoids AuthenticationHeaderValue rejecting it.
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "Authorization", $"PVEAPIToken={settings.TokenId}={settings.ApiToken}");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            _client = client;
            return _client;
        }
    }

    // Drops the cached client so the next call rebuilds it — the host/port/token/fingerprint may have changed.
    public void Invalidate()
    {
        lock (_lock)
        {
            _client?.Dispose();
            _client = null;
        }
    }

    public async Task<ProxmoxVersion> GetVersionAsync(CancellationToken cancellationToken)
    {
        var data = await _GetAsync("version", cancellationToken);
        return new ProxmoxVersion(_Str(data, "release"), _Str(data, "repoid"), _Str(data, "version"));
    }

    public async Task<IReadOnlyList<ProxmoxNode>> ListNodesAsync(CancellationToken cancellationToken)
    {
        var data = await _GetAsync("nodes", cancellationToken);
        return data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Select(node => new ProxmoxNode(
                _Str(node, "node"), _Str(node, "status"), _Double(node, "cpu") * 100, (int)_Long(node, "maxcpu"),
                _Long(node, "mem"), _Long(node, "maxmem"), _Long(node, "uptime"))).ToList()
            : [];
    }

    public async Task<ProxmoxClusterInfo> GetClusterInfoAsync(CancellationToken cancellationToken)
    {
        var data = await _GetAsync("cluster/status", cancellationToken);
        if (data.ValueKind != JsonValueKind.Array)
        {
            return new ProxmoxClusterInfo(IsCluster: false, Name: null, Quorate: true, NodeCount: 1);
        }

        var items = data.EnumerateArray().ToList();
        var cluster = items.FirstOrDefault(item => _Str(item, "type") == "cluster");
        if (cluster.ValueKind == JsonValueKind.Undefined)
        {
            var nodeCount = items.Count(item => _Str(item, "type") == "node");
            return new ProxmoxClusterInfo(IsCluster: false, Name: null, Quorate: true, NodeCount: Math.Max(1, nodeCount));
        }

        return new ProxmoxClusterInfo(
            IsCluster: true,
            Name: _Str(cluster, "name"),
            Quorate: _Bool(cluster, "quorate"),
            NodeCount: (int)_Long(cluster, "nodes", 1));
    }

    public async Task<IReadOnlyList<ProxmoxGuest>> ListVmsAsync(CancellationToken cancellationToken) =>
        (await _ListGuestsAsync(cancellationToken)).Where(guest => guest.Type == "qemu").ToList();

    public async Task<IReadOnlyList<ProxmoxGuest>> ListLxcAsync(CancellationToken cancellationToken) =>
        (await _ListGuestsAsync(cancellationToken)).Where(guest => guest.Type == "lxc").ToList();

    private async Task<IReadOnlyList<ProxmoxGuest>> _ListGuestsAsync(CancellationToken cancellationToken)
    {
        // `/cluster/resources` has no separate "lxc" filter — `type=vm` returns both kinds, distinguished by each
        // entry's own `type` field ("qemu" or "lxc"). Works the same for a single host or a cluster.
        var data = await _GetAsync("cluster/resources?type=vm", cancellationToken);
        return data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Select(resource => new ProxmoxGuest(
                _Str(resource, "vmid"), _Str(resource, "name"), _Str(resource, "node"), _Str(resource, "type"),
                _Str(resource, "status"), _Long(resource, "maxmem"), _Long(resource, "maxdisk"),
                _Double(resource, "maxcpu"), _Long(resource, "uptime"))).ToList()
            : [];
    }

    public Task<ProxmoxTaskOutcome> StartVmAsync(string node, string vmId, CancellationToken cancellationToken) =>
        _GuestActionAsync(node, "qemu", vmId, "start", cancellationToken);

    public Task<ProxmoxTaskOutcome> ShutdownVmAsync(string node, string vmId, CancellationToken cancellationToken) =>
        _GuestActionAsync(node, "qemu", vmId, "shutdown", cancellationToken);

    public Task<ProxmoxTaskOutcome> StopVmAsync(string node, string vmId, CancellationToken cancellationToken) =>
        _GuestActionAsync(node, "qemu", vmId, "stop", cancellationToken);

    public Task<ProxmoxTaskOutcome> RebootVmAsync(string node, string vmId, CancellationToken cancellationToken) =>
        _GuestActionAsync(node, "qemu", vmId, "reboot", cancellationToken);

    public Task<ProxmoxTaskOutcome> DeleteVmAsync(string node, string vmId, CancellationToken cancellationToken) =>
        _DeleteGuestAsync(node, "qemu", vmId, cancellationToken);

    public Task<ProxmoxTaskOutcome> StartLxcAsync(string node, string vmId, CancellationToken cancellationToken) =>
        _GuestActionAsync(node, "lxc", vmId, "start", cancellationToken);

    public Task<ProxmoxTaskOutcome> ShutdownLxcAsync(string node, string vmId, CancellationToken cancellationToken) =>
        _GuestActionAsync(node, "lxc", vmId, "shutdown", cancellationToken);

    public Task<ProxmoxTaskOutcome> StopLxcAsync(string node, string vmId, CancellationToken cancellationToken) =>
        _GuestActionAsync(node, "lxc", vmId, "stop", cancellationToken);

    public Task<ProxmoxTaskOutcome> RebootLxcAsync(string node, string vmId, CancellationToken cancellationToken) =>
        _GuestActionAsync(node, "lxc", vmId, "reboot", cancellationToken);

    public Task<ProxmoxTaskOutcome> DeleteLxcAsync(string node, string vmId, CancellationToken cancellationToken) =>
        _DeleteGuestAsync(node, "lxc", vmId, cancellationToken);

    public async Task<IReadOnlyList<ProxmoxSnapshot>> ListVmSnapshotsAsync(string node, string vmId, CancellationToken cancellationToken) =>
        await _ListSnapshotsAsync(node, "qemu", vmId, cancellationToken);

    public async Task<IReadOnlyList<ProxmoxSnapshot>> ListLxcSnapshotsAsync(string node, string vmId, CancellationToken cancellationToken) =>
        await _ListSnapshotsAsync(node, "lxc", vmId, cancellationToken);

    public Task<ProxmoxTaskOutcome> SnapshotVmAsync(string node, string vmId, string name, string? description, CancellationToken cancellationToken) =>
        _SnapshotAsync(node, "qemu", vmId, name, description, cancellationToken);

    public Task<ProxmoxTaskOutcome> SnapshotLxcAsync(string node, string vmId, string name, string? description, CancellationToken cancellationToken) =>
        _SnapshotAsync(node, "lxc", vmId, name, description, cancellationToken);

    public Task<ProxmoxTaskOutcome> RollbackVmSnapshotAsync(string node, string vmId, string name, CancellationToken cancellationToken) =>
        _RollbackAsync(node, "qemu", vmId, name, cancellationToken);

    public Task<ProxmoxTaskOutcome> RollbackLxcSnapshotAsync(string node, string vmId, string name, CancellationToken cancellationToken) =>
        _RollbackAsync(node, "lxc", vmId, name, cancellationToken);

    public async Task<IReadOnlyList<ProxmoxStoragePool>> ListStorageAsync(CancellationToken cancellationToken)
    {
        var data = await _GetAsync("cluster/resources?type=storage", cancellationToken);
        return data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Select(resource => new ProxmoxStoragePool(
                _Str(resource, "storage"), _Str(resource, "node"), _Str(resource, "plugintype"),
                _Long(resource, "maxdisk"), _Long(resource, "disk"), _Str(resource, "status") == "available")).ToList()
            : [];
    }

    public async Task<IReadOnlyList<ProxmoxTaskSummary>> ListTasksAsync(string node, CancellationToken cancellationToken)
    {
        var data = await _GetAsync($"nodes/{_Enc(node)}/tasks", cancellationToken);
        return data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Select(task => new ProxmoxTaskSummary(
                _Str(task, "upid"), _Str(task, "type"), task.TryGetProperty("status", out _) ? _Str(task, "status") : null,
                _Str(task, "user"), _Long(task, "starttime"),
                task.TryGetProperty("endtime", out var end) ? end.GetInt64() : null)).ToList()
            : [];
    }

    private async Task<ProxmoxTaskOutcome> _GuestActionAsync(string node, string type, string vmId, string action, CancellationToken cancellationToken)
    {
        var data = await _PostAsync($"nodes/{_Enc(node)}/{type}/{_Enc(vmId)}/status/{action}", null, cancellationToken);
        return await _WaitForTaskAsync(node, _Upid(data), cancellationToken);
    }

    private async Task<ProxmoxTaskOutcome> _DeleteGuestAsync(string node, string type, string vmId, CancellationToken cancellationToken)
    {
        var data = await _DeleteAsync($"nodes/{_Enc(node)}/{type}/{_Enc(vmId)}", cancellationToken);
        return await _WaitForTaskAsync(node, _Upid(data), cancellationToken);
    }

    private async Task<IReadOnlyList<ProxmoxSnapshot>> _ListSnapshotsAsync(string node, string type, string vmId, CancellationToken cancellationToken)
    {
        var data = await _GetAsync($"nodes/{_Enc(node)}/{type}/{_Enc(vmId)}/snapshot", cancellationToken);
        return data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Select(snapshot =>
            {
                var name = _Str(snapshot, "name");
                return new ProxmoxSnapshot(
                    name,
                    snapshot.TryGetProperty("description", out var description) ? description.GetString() : null,
                    snapshot.TryGetProperty("snaptime", out var time) ? time.GetInt64() : null,
                    IsCurrent: name == "current");
            }).ToList()
            : [];
    }

    private async Task<ProxmoxTaskOutcome> _SnapshotAsync(string node, string type, string vmId, string name, string? description, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string> { ["snapname"] = name };
        if (!string.IsNullOrWhiteSpace(description))
        {
            form["description"] = description;
        }

        var data = await _PostAsync($"nodes/{_Enc(node)}/{type}/{_Enc(vmId)}/snapshot", form, cancellationToken);
        return await _WaitForTaskAsync(node, _Upid(data), cancellationToken);
    }

    private async Task<ProxmoxTaskOutcome> _RollbackAsync(string node, string type, string vmId, string name, CancellationToken cancellationToken)
    {
        var data = await _PostAsync($"nodes/{_Enc(node)}/{type}/{_Enc(vmId)}/snapshot/{_Enc(name)}/rollback", null, cancellationToken);
        return await _WaitForTaskAsync(node, _Upid(data), cancellationToken);
    }

    // Polls the task's status until it exits or the timeout elapses. `Task.Delay(..., cancellationToken)` throws
    // immediately on cancellation, so an aborted call stops right away rather than counting down first.
    private async Task<ProxmoxTaskOutcome> _WaitForTaskAsync(string node, string upid, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TaskTimeout;
        while (true)
        {
            var data = await _GetAsync($"nodes/{_Enc(node)}/tasks/{_Enc(upid)}/status", cancellationToken);
            if (_Str(data, "status") != "running")
            {
                var exitStatus = _Str(data, "exitstatus");
                return new ProxmoxTaskOutcome(upid, IsSuccess: exitStatus == "OK", ExitStatus: exitStatus, TimedOut: false);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return new ProxmoxTaskOutcome(upid, IsSuccess: false, ExitStatus: "still running", TimedOut: true);
            }

            await Task.Delay(TaskPollInterval, cancellationToken);
        }
    }

    private static string _Upid(JsonElement data) =>
        data.ValueKind == JsonValueKind.String ? data.GetString()! : throw new ProxmoxApiException("Proxmox did not return a task id for this action.");

    private Task<JsonElement> _GetAsync(string path, CancellationToken cancellationToken) =>
        _SendAsync(HttpMethod.Get, path, null, cancellationToken);

    private Task<JsonElement> _PostAsync(string path, IReadOnlyDictionary<string, string>? form, CancellationToken cancellationToken) =>
        _SendAsync(HttpMethod.Post, path, form, cancellationToken);

    private Task<JsonElement> _DeleteAsync(string path, CancellationToken cancellationToken) =>
        _SendAsync(HttpMethod.Delete, path, null, cancellationToken);

    private async Task<JsonElement> _SendAsync(HttpMethod method, string path, IReadOnlyDictionary<string, string>? form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        HttpResponseMessage response;
        try
        {
            response = await _Client().SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProxmoxApiException)
        {
            // Already a readable, specific message (e.g. "not configured yet") — pass it through unwrapped.
            throw;
        }
        catch (TaskCanceledException)
        {
            throw new ProxmoxApiException($"The Proxmox API at {settings.Host}:{settings.Port} did not respond in time.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            throw new ProxmoxApiException(
                $"The certificate presented by {settings.Host}:{settings.Port} does not match the trusted fingerprint. " +
                "It may have changed — verify and re-trust it in the Proxmox plugin settings.");
        }
        catch (Exception ex)
        {
            throw new ProxmoxApiException(
                $"The Proxmox API at {settings.Host}:{settings.Port} could not be reached ({ex.GetType().Name}). Check that the host and port are correct and reachable.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw _ErrorFor(response.StatusCode, body);
            }

            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            return document.RootElement.TryGetProperty("data", out var data) ? data.Clone() : default;
        }
    }

    private static ProxmoxApiException _ErrorFor(HttpStatusCode status, string body)
    {
        var detail = _TryExtractErrorDetail(body);
        var suffix = detail is null ? string.Empty : $" {detail}";
        return status switch
        {
            HttpStatusCode.Unauthorized => new ProxmoxApiException(
                "The Proxmox API token was rejected (401 Unauthorized). It may be wrong or revoked — check it in the plugin settings."),
            HttpStatusCode.Forbidden => new ProxmoxApiException(
                "The Proxmox API token does not have permission for this (403 Forbidden). Check its ACLs in Proxmox under Datacenter → Permissions."),
            _ => new ProxmoxApiException($"The Proxmox API returned an error ({(int)status} {status}).{suffix}"),
        };
    }

    // Proxmox's own validation errors (e.g. a bad snapshot name) come back as `{"errors": {"field": "reason"}}` —
    // safe to surface since it is the trusted API's own text, not agent-supplied.
    private static string? _TryExtractErrorDetail(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                return string.Join("; ", errors.EnumerateObject().Select(property => $"{property.Name}: {property.Value}"));
            }
        }
        catch (JsonException)
        {
            // Not JSON, or no "errors" object — nothing more to add.
        }

        return null;
    }

    private static string _Enc(string value) => Uri.EscapeDataString(value);

    private static string _Str(JsonElement element, string name, string fallback = "") =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch { JsonValueKind.String => value.GetString() ?? fallback, JsonValueKind.Number => value.GetRawText(), _ => fallback }
            : fallback;

    private static long _Long(JsonElement element, string name, long fallback = 0) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number => value.GetInt64(),
                JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
                _ => fallback,
            }
            : fallback;

    private static double _Double(JsonElement element, string name, double fallback = 0) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDouble(),
                JsonValueKind.String when double.TryParse(value.GetString(), out var parsed) => parsed,
                _ => fallback,
            }
            : fallback;

    private static bool _Bool(JsonElement element, string name, bool fallback = false) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Number => value.GetInt32() != 0, _ => fallback }
            : fallback;

    public void Dispose() => Invalidate();
}

// A readable, safe-to-show Proxmox API error — never a stack trace, never the raw exception text (AC-1038 criterion 6).
internal sealed class ProxmoxApiException(string message) : Exception(message);
