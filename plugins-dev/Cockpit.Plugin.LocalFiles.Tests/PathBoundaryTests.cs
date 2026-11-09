using System.Diagnostics;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.Plugin.LocalFiles.Tests;

// Category A, security: a gap in the path limit opens the whole disk without a sound, so every escape meets every operation.
public sealed class PathBoundaryTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("local-files-").FullName;
    private readonly string _secret;
    private readonly string _state;
    private readonly string _root;
    private readonly LocalFileSource _source;

    public PathBoundaryTests()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        _secret = Path.Combine(outside, "secret.txt");
        File.WriteAllText(_secret, "secret");
        var root = PathBoundary.RealPath(Directory.CreateDirectory(Path.Combine(_base, "root")).FullName);
        // Its name starts with the root's, so only a separator-aware prefix check keeps it out.
        var sibling = Directory.CreateDirectory(root + "2").FullName;
        File.WriteAllText(Path.Combine(sibling, "secret.txt"), "secret");
        File.WriteAllText(Path.Combine(root, "inside.txt"), "inside");
        _state = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
        File.WriteAllText(Path.Combine(_state, "credentials.json"), "{}");
        _Link(Path.Combine(root, "link"), outside);
        _Link(Path.Combine(root, "leak"), outside);
        _Link(Path.Combine(root, "sibling"), sibling);
        _Link(Path.Combine(_base, "statelink"), _state);
        _root = root;
        _source = new LocalFileSource("c", root, writable: true, [_state]);
    }

    [Theory]
    [InlineData("../outside/secret.txt")]
    [InlineData("{secret}")]
    [InlineData(@"\\server\share\secret.txt|\\?\{secret}")]
    [InlineData("inside.txt:secret")]
    [InlineData("CON")]
    [InlineData("link/secret.txt")]
    [InlineData("leak")]
    [InlineData("sibling/secret.txt")]
    [InlineData("state/credentials.json")]
    [InlineData("rootforms")]
    public async Task RefusesEveryEscapeForEveryOperation(string escape)
    {
        if (escape == "rootforms")
        {
            _RefusesEveryFormOfTheStateFolderAsARoot();
            return;
        }
        var inside = await _source.ReadAsync(new FileRef("c", "inside.txt", "inside.txt"), CancellationToken.None);
        Assert.Equal(6, inside.Content.Length);
        foreach (var path in escape.Split('|').Select(part => part.Replace("{secret}", _secret, StringComparison.Ordinal)))
        {
            await _RefusesEveryOperationAsync(new FileRef("c", path, path), inside.Version);
        }
    }

    private async Task _RefusesEveryOperationAsync(FileRef file, string version)
    {
        Func<Task>[] operations =
        [
            () => _source.ReadAsync(file, CancellationToken.None),
            () => _source.ReadRangeAsync(file, 0, 16, CancellationToken.None),
            () => _source.ListAsync(file, null, CancellationToken.None),
            () => _source.WriteAsync(file, new MemoryStream("x"u8.ToArray()), version, false, CancellationToken.None),
            () => _source.WriteAsync(file, new MemoryStream("x"u8.ToArray()), null, true, CancellationToken.None),
            () => _source.CreateFileAsync(file, "new.md", new MemoryStream("x"u8.ToArray()), CancellationToken.None),
            () => _source.CreateFolderAsync(file, "new", CancellationToken.None),
        ];
        var kinds = new List<FileSourceErrorKind>();
        foreach (var operation in operations)
        {
            kinds.Add((await Assert.ThrowsAsync<FileSourceException>(operation)).Kind);
        }

        Assert.All(kinds, kind => Assert.Equal(FileSourceErrorKind.Forbidden, kind));
        Assert.Equal("secret", File.ReadAllText(_secret));
        Assert.Equal(["secret.txt"], Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(_secret)!).Select(Path.GetFileName));
        Assert.Equal(["secret.txt"], Directory.EnumerateFileSystemEntries(Path.Combine(_base, "root2")).Select(Path.GetFileName));
        Assert.Equal(["credentials.json"], Directory.EnumerateFileSystemEntries(_state).Select(Path.GetFileName));
    }

    // Adding a root is the other door: every spelling of the state folder, or of a folder around it, must be refused.
    private void _RefusesEveryFormOfTheStateFolderAsARoot()
    {
        var plugin = new LocalFilesPlugin([_state]);
        string[] forms = OperatingSystem.IsWindows()
            ?
            [
                _state, @"\\?\" + _state, @"\\.\" + _state, $@"\\localhost\{_state[0]}${_state[2..]}",
                _state + "::$INDEX_ALLOCATION", _state + ":$I30:$INDEX_ALLOCATION", @"\\?\" + _root, Path.Combine(_base, "statelink"),
            ]
            : [_state, "/", "/proc/self/root" + _state, Path.Combine(_base, "statelink")];
        var accepted = forms.Where(form => plugin.Check(form).Path is not null).ToList();

        Assert.Empty(accepted);
        Assert.Equal((_root, _state), (plugin.Check(_root).Path, Assert.Single(plugin.Check(_root).Hidden)));
    }

    // Windows gets a junction, which needs no privilege; elsewhere a directory symlink is the same escape.
    private static void _Link(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { RedirectStandardOutput = true })!;
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }

    public void Dispose()
    {
        // Links go first, so the recursive delete cannot follow one out of the test folder.
        foreach (var link in new[] { Path.Combine("root", "link"), Path.Combine("root", "leak"), Path.Combine("root", "sibling"), "statelink" })
        {
            Directory.Delete(Path.Combine(_base, link));
        }
        Directory.Delete(_base, recursive: true);
    }
}
