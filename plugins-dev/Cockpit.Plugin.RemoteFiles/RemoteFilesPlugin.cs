using System.Text.Json;
using Cockpit.Plugin.RemoteFiles.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Files;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Plugin.RemoteFiles;

// One file connection per server the operator adds; on a Cockpit server the connection runs from the server.
public sealed class RemoteFilesPlugin : ICockpitPlugin, IFileSourceProvider
{
    private const string ConnectionsKey = "connections";
    private static readonly string[] Kinds = [RemoteFilesChannel.Sftp, RemoteFilesChannel.Ftps, RemoteFilesChannel.Ftp];

    private readonly List<IDisposable> _handlers = [];
    private IPluginStorage? _storage;

    public PluginMetadata Metadata { get; } = new(
        Id: "remote-files",
        DisplayName: "Remote files",
        Author: "Cockpit",
        Description: "Browse, view and edit files on SFTP and FTP servers in Cockpit's file browser.");

    public event EventHandler? ConnectionsChanged;

    public IReadOnlyList<FileConnection> Connections =>
        [.. _Connections().Select(connection => new FileConnection(connection.Id, connection.Name, connection.Kind, connection.ReadOnly))];

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        _storage = host.Storage;
        _handlers.Add(host.AddFileSource(this));
        _Handle<JsonElement, RemoteConnectionsAnswer>(host, RemoteFilesChannel.Connections, (_, _) => Task.FromResult(_Answer(null)),
            () => _Answer("The connections could not be read."));
        _Handle<RemoteSaveRequest, RemoteConnectionsAnswer>(host, RemoteFilesChannel.Save, (request, _) => Task.FromResult(_Save(request)),
            () => _Answer("The connections could not be saved."));
        _Handle<RemoteTestRequest, RemoteTestAnswer>(host, RemoteFilesChannel.Test, _TestAsync,
            () => new RemoteTestAnswer(RemoteTestStatus.Failed, null, "The connection could not be tested."));
    }

    public IFileSource Open(string connectionId) =>
        _Connections().FirstOrDefault(connection => connection.Id == connectionId) is { } found
            ? new RemoteFileSource(!found.ReadOnly,
                cancellationToken => RemoteSession.ConnectAsync(found, _Secrets(found.Id), new RemoteTrust(found.Pin), cancellationToken))
            : throw new FileSourceException(FileSourceErrorKind.NotFound, "This connection is no longer in the settings.");

    // Connects with the pin the view sends, so a key trusted in the dialog is used before Apply stores it.
    private async Task<RemoteTestAnswer> _TestAsync(RemoteTestRequest request, CancellationToken cancellationToken)
    {
        if (_Invalid(request.Connection) is { } invalid)
        {
            return new RemoteTestAnswer(RemoteTestStatus.Failed, null, invalid);
        }
        var saved = _Connections().FirstOrDefault(connection => connection.Id == request.Connection.Id);
        var secrets = SecretsFor(saved, request.Connection, _Secrets(request.Connection.Id), request.Secrets);
        var trust = new RemoteTrust(request.Connection.Pin);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using var session = await RemoteSession.ConnectAsync(request.Connection, secrets, trust, timeout.Token).ConfigureAwait(false);
            await session.ListAsync(session.Home, timeout.Token).ConfigureAwait(false);
            return new RemoteTestAnswer(RemoteTestStatus.Connected, trust.Presented, null);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return trust.Verdict switch
            {
                PinVerdict.Unknown => new RemoteTestAnswer(RemoteTestStatus.Untrusted, trust.Presented, null),
                PinVerdict.Changed => new RemoteTestAnswer(RemoteTestStatus.Changed, trust.Presented, null),
                _ => new RemoteTestAnswer(RemoteTestStatus.Failed, null, error is OperationCanceledException
                    ? "The server did not answer within 30 seconds."
                    : RemoteTrust.Translate(error).Message),
            };
        }
    }

    private RemoteConnectionsAnswer _Save(RemoteSaveRequest request)
    {
        var connections = request.Connections ?? [];
        if (connections.DistinctBy(connection => connection?.Id).Count() != connections.Count)
        {
            return _Answer("Every connection needs its own id.");
        }
        foreach (var connection in connections)
        {
            if (_Invalid(connection) is { } invalid)
            {
                return _Answer($"{connection?.Name}: {invalid}");
            }
        }
        var storage = _storage ?? throw new InvalidOperationException("The plugin is not initialized.");
        var previous = _Connections();
        foreach (var gone in previous.Where(old => connections.All(kept => kept.Id != old.Id)))
        {
            _WriteSecrets(storage, gone.Id, new RemoteSecrets("", "", ""));
        }
        // A connection moved to another server keeps neither its old secrets nor its old pin.
        connections = [.. connections.Select(connection => previous.FirstOrDefault(old => old.Id == connection.Id) is { } old && !SameServer(old, connection)
            ? _Moved(storage, old, connection)
            : connection)];
        foreach (var (id, secrets) in request.Secrets ?? new Dictionary<string, RemoteSecrets>())
        {
            if (connections.Any(connection => connection.Id == id) && secrets is not null)
            {
                _WriteSecrets(storage, id, secrets);
            }
        }
        storage.Set(ConnectionsKey, connections.ToArray());
        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
        return _Answer(null);
    }

    private static RemoteConnection _Moved(IPluginStorage storage, RemoteConnection old, RemoteConnection moved)
    {
        _WriteSecrets(storage, moved.Id, new RemoteSecrets("", "", ""));
        return moved.Pin == old.Pin ? moved with { Pin = null } : moved;
    }

    // A stored secret only goes to the server it was entered for, so a request that names another one gets only what it typed.
    internal static RemoteSecrets SecretsFor(RemoteConnection? saved, RemoteConnection requested, RemoteSecrets stored, RemoteSecrets? typed)
    {
        var usable = SameServer(saved, requested) ? stored : new RemoteSecrets();
        return new RemoteSecrets(typed?.Password ?? usable.Password, typed?.Key ?? usable.Key, typed?.Passphrase ?? usable.Passphrase);
    }

    internal static bool SameServer(RemoteConnection? saved, RemoteConnection requested) =>
        saved is not null && saved.Kind == requested.Kind && string.Equals(saved.Host, requested.Host, StringComparison.OrdinalIgnoreCase)
        && saved.Port == requested.Port && string.Equals(saved.User, requested.User, StringComparison.Ordinal);

    private static string? _Invalid(RemoteConnection? connection) => connection switch
    {
        null => "The connection is incomplete.",
        { Id: null or "" } => "Every connection needs its own id.",
        _ when string.IsNullOrWhiteSpace(connection.Name) => "Give the connection a name.",
        _ when !Kinds.Contains(connection.Kind) => "Choose SFTP, FTPS or FTP.",
        _ when string.IsNullOrWhiteSpace(connection.Host) || Uri.CheckHostName(connection.Host) == UriHostNameType.Unknown => "Enter the server's host name or address.",
        _ when connection.Port is < 1 or > 65535 => "The port is a number from 1 to 65535.",
        _ when string.IsNullOrWhiteSpace(connection.User) => "Enter the user name.",
        _ when $"{connection.Name}{connection.Host}{connection.User}".Any(char.IsControl) => "Names cannot contain control characters.",
        _ => null,
    };

    private static void _WriteSecrets(IPluginStorage storage, string id, RemoteSecrets secrets)
    {
        foreach (var (name, value) in new[] { ("password", secrets.Password), ("key", secrets.Key), ("passphrase", secrets.Passphrase) })
        {
            if (value is not null)
            {
                storage.SetSecret(_SecretKey(id, name), value);
            }
        }
    }

    private RemoteSecrets _Secrets(string id) => new(
        _storage?.GetSecret(_SecretKey(id, "password")),
        _storage?.GetSecret(_SecretKey(id, "key")),
        _storage?.GetSecret(_SecretKey(id, "passphrase")));

    private static string _SecretKey(string id, string name) => $"secret.{id}.{name}";

    private RemoteConnectionsAnswer _Answer(string? error)
    {
        var connections = _Connections();
        return new RemoteConnectionsAnswer(connections, connections.ToDictionary(connection => connection.Id, connection => _Set(_Secrets(connection.Id))),
            Environment.MachineName, error);
    }

    private static RemoteSecretsSet _Set(RemoteSecrets secrets) =>
        new(!string.IsNullOrEmpty(secrets.Password), !string.IsNullOrEmpty(secrets.Key), !string.IsNullOrEmpty(secrets.Passphrase));

    private IReadOnlyList<RemoteConnection> _Connections() => _storage?.Get<RemoteConnection[]>(ConnectionsKey) ?? [];

    // No exception text leaves the backend: a library message can carry the host, the user or a secret.
    private void _Handle<TRequest, TAnswer>(ICockpitHost host, string action, Func<TRequest, CancellationToken, Task<TAnswer>> handle, Func<TAnswer> failed) =>
        _handlers.Add(host.Channel.Handle(action, async (payload, cancellationToken) =>
        {
            TAnswer answer;
            try
            {
                var request = payload.Deserialize<TRequest>(RemoteFilesChannel.Json) ?? throw new JsonException();
                answer = await handle(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                answer = failed();
            }
            return JsonSerializer.SerializeToElement(answer, RemoteFilesChannel.Json);
        }));

    public void Dispose()
    {
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }
        _handlers.Clear();
    }
}
