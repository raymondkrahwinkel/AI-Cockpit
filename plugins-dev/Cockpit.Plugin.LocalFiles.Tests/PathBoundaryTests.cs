using System.Diagnostics;
using System.Text;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.Plugin.LocalFiles.Tests;

// Category A, security: a gap in the path limit opens the whole disk without a sound, so every escape is tried both ways.
public sealed class PathBoundaryTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("local-files-").FullName;
    private readonly string _secret;
    private readonly LocalFileSource _source;

    public PathBoundaryTests()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        _secret = Path.Combine(outside, "secret.txt");
        File.WriteAllText(_secret, "secret");
        var root = PathBoundary.RealPath(Directory.CreateDirectory(Path.Combine(_base, "root")).FullName);
        File.WriteAllText(Path.Combine(root, "inside.txt"), "inside");
        var state = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
        File.WriteAllText(Path.Combine(state, "credentials.json"), "{}");
        var link = Path.Combine(root, "link");
        // Windows gets a junction, which needs no privilege and is what the ticket names; elsewhere a symlink is the same escape.
        if (OperatingSystem.IsWindows())
        {
            using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, outside]) { RedirectStandardOutput = true })!;
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        _source = new LocalFileSource("c", root, writable: true, [state]);
    }

    // One row per escape; TheoryData keeps this one test under the project's ceiling of 1 (check-test-count.py).
    public static TheoryData<string> Escapes { get; } =
    [
        "../outside/secret.txt",
        "inside.txt/../../outside/secret.txt",
        "{secret}",
        "//server/share/secret.txt",
        @"\\server\share\secret.txt",
        @"\\?\{secret}",
        @"\\.\{secret}",
        "inside.txt:secret",
        "CON",
        "nul.txt",
        "link/secret.txt",
        "state/credentials.json",
    ];

    [Theory]
    [MemberData(nameof(Escapes))]
    public async Task RefusesEveryEscapeForReadingAndWriting(string escape)
    {
        var path = escape.Replace("{secret}", _secret, StringComparison.Ordinal);
        var file = new FileRef("c", path, path);
        Assert.Equal("inside", await _ReadAsync(new FileRef("c", "inside.txt", "inside.txt")));

        var read = await Assert.ThrowsAsync<FileSourceException>(() => _ReadAsync(file));
        using var content = new MemoryStream("overwritten"u8.ToArray());
        var write = await Assert.ThrowsAsync<FileSourceException>(() => _source.WriteAsync(file, content, null, true, CancellationToken.None));

        Assert.Equal((FileSourceErrorKind.Forbidden, FileSourceErrorKind.Forbidden), (read.Kind, write.Kind));
        Assert.Equal("secret", File.ReadAllText(_secret));
        Assert.Equal([_secret], Directory.GetFiles(Path.GetDirectoryName(_secret)!));
    }

    private async Task<string> _ReadAsync(FileRef file)
    {
        var result = await _source.ReadAsync(file, CancellationToken.None);
        using var reader = new StreamReader(result.Content, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    public void Dispose()
    {
        // The junction goes first, so the recursive delete cannot follow it into the folder outside.
        Directory.Delete(Path.Combine(_base, "root", "link"));
        Directory.Delete(_base, recursive: true);
    }
}
