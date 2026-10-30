using System.Runtime.InteropServices;
using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Shell;

namespace Cockpit.Infrastructure.Diagnostics;

internal sealed record DockerDataDisk(string Path, long Length, long AllocatedBytes);

internal interface IDockerStoragePlatform
{
    bool IsWindows { get; }
    DockerDataDisk? ReadDataDisk();
    Task<IReadOnlyList<string>> RunningDistrosAsync(CancellationToken cancellationToken);
    Task ShutdownAsync(CancellationToken cancellationToken);
}

internal sealed class DockerStoragePlatform(IShellCommandRunner shell) : IDockerStoragePlatform, ISingletonService
{
    public bool IsWindows => OperatingSystem.IsWindows();

    public DockerDataDisk? ReadDataDisk()
    {
        if (!IsWindows)
        {
            return null;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        List<string> roots = [System.IO.Path.Combine(local, "Docker", "wsl")];
        var settings = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Docker");
        foreach (var name in new[] { "settings-store.json", "settings.json" })
        {
            var file = System.IO.Path.Combine(settings, name);
            if (!File.Exists(file))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.TryGetProperty("dataFolder", out var folder) && folder.GetString() is { Length: > 0 } path)
            {
                roots.Insert(0, Environment.ExpandEnvironmentVariables(path));
            }
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var relative in new[] { "disk/docker_data.vhdx", "data/ext4.vhdx", "docker_data.vhdx", "wsl/disk/docker_data.vhdx", "wsl/data/ext4.vhdx" })
            {
                var file = new FileInfo(System.IO.Path.Combine(root, relative));
                if (!file.Exists)
                {
                    continue;
                }

                var low = GetCompressedFileSizeW(file.FullName, out var high);
                if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0)
                {
                    return null;
                }

                return new DockerDataDisk(file.FullName, file.Length, checked((long)(((ulong)high << 32) | low)));
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<string>> RunningDistrosAsync(CancellationToken cancellationToken)
    {
        var result = await shell.RunAsync(Environment.CurrentDirectory, "wsl.exe", ["--list", "--running", "--quiet"], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.TimedOut)
        {
            throw new InvalidOperationException("Could not list running WSL distributions: " + result.StandardError);
        }

        return result.StandardOutput.Replace("\0", string.Empty, StringComparison.Ordinal).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal).ToArray();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        var result = await shell.RunAsync(Environment.CurrentDirectory, "wsl.exe", ["--shutdown"], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.TimedOut)
        {
            throw new InvalidOperationException("WSL shutdown failed: " + result.StandardError);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string fileName, out uint high);
}
