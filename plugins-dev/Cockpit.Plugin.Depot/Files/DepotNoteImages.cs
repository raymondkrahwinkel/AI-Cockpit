using System.Net;
using System.Net.Http.Headers;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.Plugin.Depot.Files;

internal sealed class DepotNoteImages(ICockpitHost host, Func<IReadOnlyList<DepotConnectionRegistration>> connections)
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _downloads = new(2);
    private readonly Dictionary<string, (byte[] Bytes, string? ETag)> _cache = [];
    private readonly Dictionary<string, Pending> _pending = [];
    private int _generation;
    private CancellationTokenSource _lifetime = new();

    public void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _lifetime.Cancel();
            _lifetime.Dispose();
            _lifetime = new CancellationTokenSource();
            _cache.Clear();
            _pending.Clear();
        }
    }

    internal static string Resolve(string note, string target)
    {
        var path = target.Split('?', '#')[0].Replace('\\', '/');
        var decoded = Uri.UnescapeDataString(path).Replace('\\', '/');
        if (decoded.Length == 0 || decoded.StartsWith('/') || decoded.Contains(':')
            || target.Any(char.IsControl) || decoded.Any(char.IsControl) || decoded.Contains('%')
            || decoded.Split('/').Count(part => part is "." or "..") != path.Split('/').Count(part => part is "." or ".."))
        {
            throw new FileSourceException(FileSourceErrorKind.Unsupported, "This image path is not allowed.");
        }
        var parts = note.Split('/').SkipLast(1).ToList();
        foreach (var part in decoded.Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }
            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    throw new FileSourceException(FileSourceErrorKind.Unsupported, "This image path leaves the project.");
                }
                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(part);
            }
        }
        return string.Join('/', parts);
    }

    public async Task<byte[]> ReadAsync(FileRef file, string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (project, note) = DepotFileRefs.Parse(file, file.ConnectionId);
        var path = Resolve(note, target);
        var connection = connections().FirstOrDefault(c => c.Id == file.ConnectionId)
            ?? throw new FileSourceException(FileSourceErrorKind.NotFound, "This connection is unavailable.");
        var key = $"{connection.Id}|{connection.Url}|{project}|{path}";
        Pending load;
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out var existing))
            {
                var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                var task = _LoadAsync(connection, project, path, key, _generation, lifetime.Token);
                // A cancelled last reader still leaves the shared task's eventual exception observed.
                _ = task.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                load = new Pending(task, lifetime);
                _pending[key] = load;
            }
            else
            {
                load = existing;
            }
            load.Readers++;
        }
        try
        {
            return await load.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (--load.Readers == 0)
                {
                    if (_pending.GetValueOrDefault(key) == load)
                    {
                        _pending.Remove(key);
                    }
                    load.Lifetime.Cancel();
                    load.Lifetime.Dispose();
                }
            }
        }
    }

    private async Task<byte[]> _LoadAsync(DepotConnectionRegistration connection, string project, string path, string key, int generation, CancellationToken ct)
    {
        // Yield before taking the gate: the caller registers this task while holding it.
        await Task.Yield();
        await _downloads.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            (byte[] Bytes, string? ETag) cached;
            lock (_gate)
            {
                _CheckGeneration(generation);
                cached = _cache.GetValueOrDefault(key);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var http = new HttpClient(host.CreateAuthorizedHandler(connection.McpServerName));
            var address = project.Split('/');
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{connection.Url.TrimEnd('/')}/sync/{Uri.EscapeDataString(address[0])}/{Uri.EscapeDataString(address[1])}/artifact?path={Uri.EscapeDataString(path)}");
            if (cached.ETag is { } etag)
            {
                request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
            }
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            byte[] bytes;
            if (response.StatusCode == HttpStatusCode.NotModified && cached.Bytes is not null)
            {
                bytes = cached.Bytes;
            }
            else
            {
                if (!response.IsSuccessStatusCode)
                {
                    lock (_gate)
                    {
                        _CheckGeneration(generation);
                        _cache.Remove(key);
                    }
                    throw new FileSourceException(FileSourceErrorKind.Unavailable, "This image is unavailable.");
                }
                if (response.Content.Headers.ContentLength > FileImageChannel.MaxBytes
                    || response.Content.Headers.ContentType?.MediaType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp"))
                {
                    throw new FileSourceException(FileSourceErrorKind.Unsupported, "This image format or size is unsupported.");
                }
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                bytes = await FileSourceChannel.ReadContentAsync(stream, FileImageChannel.MaxBytes, timeout.Token).ConfigureAwait(false);
            }
            lock (_gate)
            {
                _CheckGeneration(generation);
                if (connections().FirstOrDefault(c => c.Id == connection.Id) != connection)
                {
                    throw new FileSourceException(FileSourceErrorKind.Unavailable, "The image connection changed.");
                }
                while (_cache.Count > 0 && _cache.Sum(entry => entry.Value.Bytes.Length) + bytes.Length > 16 * 1024 * 1024)
                {
                    _cache.Remove(_cache.Keys.First());
                }
                _cache[key] = (bytes, response.StatusCode == HttpStatusCode.NotModified ? cached.ETag : response.Headers.ETag?.ToString());
                return bytes;
            }
        }
        finally
        {
            _downloads.Release();
        }
    }

    private void _CheckGeneration(int generation)
    {
        if (generation != _generation)
        {
            throw new FileSourceException(FileSourceErrorKind.Unavailable, "The image account changed.");
        }
    }

    private sealed class Pending(Task<byte[]> task, CancellationTokenSource lifetime)
    {
        public Task<byte[]> Task { get; } = task;
        public CancellationTokenSource Lifetime { get; } = lifetime;
        public int Readers { get; set; }
    }
}
