using System.Text.Json;
using Cockpit.Core.Configuration;
using Cockpit.Plugin.LocalFiles.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Files;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Plugin.LocalFiles;

// One file connection per added folder; on a server these are the server's folders, so every check runs here.
public sealed class LocalFilesPlugin : ICockpitPlugin, IFileSourceProvider
{
    private const string RootsKey = "roots";

    private readonly List<IDisposable> _handlers = [];
    private IPluginStorage? _storage;

    // Cockpit's state folders hold credentials, unencrypted on a server; no root may show them, not even a parent.
    private readonly IReadOnlyCollection<string> _denied = new[]
        {
            CockpitBuild.StateRoot,
            Path.Combine(CockpitBuild.RoamingApplicationData, CockpitBuild.ProductionStateFolder),
            Path.Combine(CockpitBuild.RoamingApplicationData, CockpitBuild.DevelopmentStateFolder),
        }
        .Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
        .Select(path => Directory.Exists(path) ? PathBoundary.RealPath(Path.GetFullPath(path)) : Path.GetFullPath(path))
        .Distinct()
        .ToArray();

    public PluginMetadata Metadata { get; } = new(
        Id: "local-files",
        DisplayName: "Local files",
        Author: "Cockpit",
        Description: "Browse, view and edit folders you add yourself in Cockpit's file browser.");

    public event EventHandler? ConnectionsChanged;

    public IReadOnlyList<FileConnection> Connections =>
        [.. _Roots().Select(root => new FileConnection(root.Id, _Label(root.Path), "local", !root.Writable))];

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        _storage = host.Storage;
        _handlers.Add(host.AddFileSource(this));
        _Handle(host, LocalFilesChannel.Roots, (LocalPathRequest _) => _Answer(null));
        _Handle(host, LocalFilesChannel.Check, (LocalPathRequest request) => Check(request.Path));
        _Handle(host, LocalFilesChannel.Save, (LocalSaveRequest request) => _Save(request.Roots ?? []));
        _Handle(host, LocalFilesChannel.Folders, (LocalPathRequest request) => _Folders(request.Path));
    }

    public IFileSource Open(string connectionId) =>
        _Roots().FirstOrDefault(root => root.Id == connectionId) is { } found
            ? new LocalFileSource(found.Id, found.Path, found.Writable, _denied)
            : throw new FileSourceException(FileSourceErrorKind.NotFound, "This folder is no longer in the settings.");

    // A root is stored as its physical path, so a link that is moved later cannot carry it somewhere else.
    internal LocalRootCheck Check(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            {
                return new LocalRootCheck(null, null, "This folder does not exist.");
            }
            var real = PathBoundary.RealPath(Path.GetFullPath(path));
            if (!Directory.Exists(real))
            {
                return new LocalRootCheck(null, null, "This folder does not exist.");
            }
            if (_denied.Any(folder => PathBoundary.IsWithin(folder, real)))
            {
                return new LocalRootCheck(null, null, $"{real} is Cockpit's own state folder, or lies inside it. It holds credentials, so it cannot be added.");
            }
            return new LocalRootCheck(real, _denied.FirstOrDefault(folder => PathBoundary.IsWithin(real, folder)), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FileSourceException)
        {
            return new LocalRootCheck(null, null, "This folder cannot be read.");
        }
    }

    private LocalRootsAnswer _Save(IReadOnlyList<LocalRoot> roots)
    {
        var saved = new List<LocalRoot>();
        // The id names the connection in open tabs and in the browser, so it must be set and unique.
        if (roots.Any(root => root is null || string.IsNullOrWhiteSpace(root.Id)) || roots.DistinctBy(root => root.Id).Count() != roots.Count)
        {
            return _Answer("Every folder needs its own id.");
        }
        foreach (var root in roots)
        {
            var check = Check(root.Path);
            if (check.Error is not null || check.Path is null)
            {
                return _Answer($"{root.Path}: {check.Error}");
            }
            if (!saved.Any(other => other.Path.Equals(check.Path, PathBoundary.Comparison)))
            {
                saved.Add(root with { Path = check.Path });
            }
        }
        _storage?.Set(RootsKey, saved.ToArray());
        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
        return _Answer(null);
    }

    // The native picker sees only the desktop's disk, so a server lists its own folders; Check still refuses.
    private static LocalFolders _Folders(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return new LocalFolders(null, [.. DriveInfo.GetDrives().Where(drive => drive.IsReady).Select(drive => drive.RootDirectory.FullName)], null);
            }
            var children = new DirectoryInfo(path).EnumerateDirectories()
                .Select(folder => folder.FullName)
                .Order(StringComparer.OrdinalIgnoreCase);
            return new LocalFolders(path, [.. children], null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new LocalFolders(path, [], "This folder cannot be read.");
        }
    }

    private LocalRootsAnswer _Answer(string? error)
    {
        var roots = _Roots();
        var hidden = new Dictionary<string, string>();
        foreach (var root in roots)
        {
            if (_denied.FirstOrDefault(folder => PathBoundary.IsWithin(root.Path, folder)) is { } inside)
            {
                hidden[root.Id] = inside;
            }
        }
        return new LocalRootsAnswer(roots, hidden, Environment.MachineName, error);
    }

    private IReadOnlyList<LocalRoot> _Roots() => _storage?.Get<LocalRoot[]>(RootsKey) ?? [];

    private static string _Label(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;

    private void _Handle<TRequest, TAnswer>(ICockpitHost host, string action, Func<TRequest, TAnswer> handle) =>
        _handlers.Add(host.Channel.Handle(action, (payload, _) =>
        {
            var request = payload.Deserialize<TRequest>(LocalFilesChannel.Json) ?? throw new JsonException($"{action} needs a request.");
            return Task.FromResult(JsonSerializer.SerializeToElement(handle(request), LocalFilesChannel.Json));
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
