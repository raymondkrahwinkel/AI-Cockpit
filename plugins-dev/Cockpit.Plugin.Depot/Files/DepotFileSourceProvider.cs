using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Files;
using Cockpit.Plugin.Depot.Browser;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Notes;
using Cockpit.Plugin.Depot.Notes.Live;
using System.Text;
using System.Text.Json;

namespace Cockpit.Plugin.Depot.Files;

internal sealed class DepotFileSourceProvider(ICockpitHost host,
    Func<IReadOnlyList<DepotConnectionRegistration>> connections,
    Func<DepotNoteLive?>? noteLive = null, Func<DepotNotes?>? notes = null) : IFileSourceProvider
{
    public IReadOnlyList<FileConnection> Connections =>
        [.. connections().Select(c => new FileConnection(c.Id, c.Name, "depot", false))];
    public event EventHandler? ConnectionsChanged;
    public void NotifyChanged() => ConnectionsChanged?.Invoke(this, EventArgs.Empty);
    public IFileSource Open(string connectionId) => new DepotFileSource(host, connections, connectionId, noteLive, notes);

    private sealed class DepotFileSource(ICockpitHost host,
        Func<IReadOnlyList<DepotConnectionRegistration>> connections, string connectionId,
        Func<DepotNoteLive?>? noteLive, Func<DepotNotes?>? notes) : IFileSource, ILiveFileSource
    {
        public FileSourceCapabilities Capabilities { get; } = new(true, FileSourceChannel.MaxContentBytes);
        public Task<FileReadResult> ReadAsync(FileRef file, CancellationToken cancellationToken) =>
            _RunAsync(async () =>
            {
                var (project, path) = DepotFileRefs.Parse(file, connectionId);
                var connection = _Connection();
                var role = await _AuthorizeAsync(connection, project, false, cancellationToken).ConfigureAwait(false);
                using var response = await _PostAsync(connection, project, "read", new[] { path }, cancellationToken).ConfigureAwait(false);
                if (response.RootElement.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
                {
                    throw new FileSourceException(FileSourceErrorKind.TooLarge, "Depot truncated the file; no partial text was opened.");
                }
                var entry = _Entries(response.RootElement, "files").FirstOrDefault(e => DepotRequests.String(e, "path") == path);
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw new FileSourceException(FileSourceErrorKind.NotFound, "The file is no longer available in Depot.");
                }
                var content = DepotRequests.String(entry, "content");
                var version = DepotRequests.String(entry, "checksum");
                if (content is null || string.IsNullOrWhiteSpace(version))
                {
                    throw new FileSourceException(FileSourceErrorKind.Unavailable, "Depot did not return the file and its version.");
                }
                var bytes = Encoding.UTF8.GetBytes(content);
                if (bytes.Length > FileSourceChannel.MaxContentBytes)
                {
                    throw new FileSourceException(FileSourceErrorKind.TooLarge, "This file is too large to edit in Cockpit.");
                }
                return new FileReadResult(new MemoryStream(bytes, writable: false), version, bytes.LongLength,
                    DateTimeOffset.TryParse(DepotRequests.String(entry, "updatedAt"), out var modified) ? modified : null,
                    "text/markdown")
                {
                    CanWrite = role.CanWrite(),
                    ModifiedBy = DepotRequests.String(entry, "updatedBy"),
                };
            }, cancellationToken);

        public Task<FileWriteResult> WriteAsync(FileRef file, Stream content, string? ifVersion,
            bool mustNotExist, CancellationToken cancellationToken) =>
            _RunAsync(async () =>
            {
                FileSourceChannel.ValidateWrite(ifVersion, mustNotExist);
                var (project, path) = DepotFileRefs.Parse(file, connectionId);
                var connection = _Connection();
                await _AuthorizeAsync(connection, project, true, cancellationToken).ConfigureAwait(false);
                var bytes = await FileSourceChannel.ReadContentAsync(content, Capabilities.MaxWriteBytes, cancellationToken).ConfigureAwait(false);
                var text = new UTF8Encoding(false, true).GetString(bytes);
                // Depot dev d9d5a756, DiskFileStore.cs:107-125: only a missing file has an empty checksum, under the write gate.
                var baseChecksum = mustNotExist ? string.Empty : ifVersion;
                using var response = await _PostAsync(connection, project, "write",
                    new[] { new { path, content = text, baseChecksum } }, cancellationToken).ConfigureAwait(false);
                var entry = _Entries(response.RootElement, "results").FirstOrDefault(e => DepotRequests.String(e, "path") == path);
                var version = DepotRequests.String(entry, "checksum");
                return DepotRequests.String(entry, "status") switch
                {
                    "written" when !string.IsNullOrWhiteSpace(version) => new FileWriteResult(FileWriteStatus.Written, version),
                    "conflict" => new FileWriteResult(FileWriteStatus.Conflict, version),
                    "quota" => throw new FileSourceException(FileSourceErrorKind.TooLarge, "Depot has no space for this file."),
                    _ => throw new FileSourceException(FileSourceErrorKind.Unavailable, "Depot did not confirm the saved file."),
                };
            }, cancellationToken);

        // A note joins live when Join or Open in Cockpit asked for it, or Depot lists a live room for it; any other file is saved as a snapshot.
        public Task<ILiveFileSession> OpenLiveAsync(FileRef file, CancellationToken cancellationToken)
        {
            var (project, path) = DepotFileRefs.Parse(file, connectionId);
            _Connection();
            var address = project.Split('/');
            var request = new DepotNoteLiveRequest(connectionId, address[0], address[1], path);
            if (noteLive?.Invoke() is not { } live || !(live.IsOpen(request) || notes?.Invoke()?.IsLive(connectionId, project, path) == true))
            {
                throw new FileSourceException(FileSourceErrorKind.Unsupported, "This file is not a live note.");
            }

            return Task.FromResult<ILiveFileSession>(live.Open(request));
        }

        private DepotConnectionRegistration _Connection() => connections().FirstOrDefault(c => c.Id == connectionId)
            ?? throw new FileSourceException(FileSourceErrorKind.NotFound, "This Depot connection is no longer configured.");

        private async Task<DepotProjectRole> _AuthorizeAsync(DepotConnectionRegistration connection, string project, bool write, CancellationToken cancellationToken)
        {
            var role = await new DepotRequests(host, () => [connection]).RoleAsync(connectionId, project, cancellationToken).ConfigureAwait(false);
            if (role == DepotProjectRole.Unknown || (write && !role.CanWrite()))
            {
                throw new FileSourceException(FileSourceErrorKind.Forbidden, "Your role does not allow this file operation.");
            }
            return role;
        }

        private async Task<JsonDocument> _PostAsync<T>(DepotConnectionRegistration connection, string project, string operation, T request, CancellationToken cancellationToken)
        {
            if (_Connection() != connection)
            {
                throw new FileSourceException(FileSourceErrorKind.Unavailable, "The Depot connection changed during this file operation. Open the file again.");
            }
            var address = project.Split('/');
            using var api = new DepotApi(host, connection);
            var result = await api.PostJsonAsync($"sync/{Uri.EscapeDataString(address[0])}/{Uri.EscapeDataString(address[1])}/{operation}",
                request, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != DepotApiOutcome.Success)
            {
                throw new FileSourceException(result.Outcome switch
                {
                    DepotApiOutcome.NotSignedIn => FileSourceErrorKind.SignIn,
                    DepotApiOutcome.Forbidden => FileSourceErrorKind.Forbidden,
                    DepotApiOutcome.TooLarge => FileSourceErrorKind.TooLarge,
                    _ => FileSourceErrorKind.Unavailable,
                }, "Depot could not complete this file operation.");
            }
            return JsonDocument.Parse(result.Content);
        }

        private static IEnumerable<JsonElement> _Entries(JsonElement root, string name) =>
            root.TryGetProperty(name, out var entries) && entries.ValueKind == JsonValueKind.Array ? entries.EnumerateArray() : [];

        private static async Task<T> _RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
        {
            try { return await action().ConfigureAwait(false); }
            catch (DepotFailure failure)
            {
                throw new FileSourceException(failure.Status == DepotBrowserStatus.SignIn ? FileSourceErrorKind.SignIn : FileSourceErrorKind.Unavailable,
                    "Depot could not authorize this file operation.");
            }
            catch (Exception error) when (error is JsonException or DecoderFallbackException)
            {
                throw new FileSourceException(FileSourceErrorKind.Unsupported, "Depot could not read this file as UTF-8 text.");
            }
            catch (Exception error) when (error is HttpRequestException || error is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new FileSourceException(FileSourceErrorKind.Unavailable, "Depot could not be reached.");
            }
        }
    }
}
