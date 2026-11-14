using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.CliAgentProvider;

// Launch modes use kebab-case; app-server turn policies use camelCase discriminators.
internal static class CodexSandbox
{
    public static IReadOnlyList<string> Choices { get; } = ["read-only", "workspace-write", "danger-full-access"];

    public static string? Clamp(string? mode, bool strict, ILogger? logger)
    {
        if (!strict || mode == "read-only")
        {
            return mode;
        }
        if (mode == "danger-full-access")
        {
            logger?.LogWarning("Strict guardrails replace Codex danger-full-access with workspace-write.");
        }
        return "workspace-write";
    }

    public static IReadOnlyList<string>? StrictWritableRoots(IReadOnlyDictionary<string, string>? environment, string workingDirectory, ILogger? logger)
    {
        if (environment?.GetValueOrDefault("COCKPIT_GUARD_MODE") != "Strict")
        {
            return null;
        }
        var roots = environment["COCKPIT_GUARD_ROOTS"].Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFullPath).ToList();
        DirectoryInfo? directory = new(workingDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")) && !File.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }
        if (directory is null)
        {
            return roots;
        }
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir" })
        {
            startInfo.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not resolve the Codex workspace's git common directory.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Resolving the Codex workspace's git common directory timed out.");
        }
        if (process.ExitCode == 0)
        {
            string[] gitDirectories = stdout.GetAwaiter().GetResult().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            stderr.GetAwaiter().GetResult();
            if (gitDirectories.Length != 2)
            {
                throw new InvalidOperationException("Git did not return both the git directory and the common directory.");
            }
            string gitDirectory = Path.GetFullPath(gitDirectories[0], workingDirectory);
            string commonDirectory = Path.GetFullPath(gitDirectories[1], workingDirectory);
            if (!string.Equals(gitDirectory, commonDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                logger?.LogWarning("Codex protects the worktree gitdir; strict sandbox skipped so git commit keeps working");
                return null;
            }
            roots.Add(commonDirectory);
        }
        else
        {
            throw new InvalidOperationException("Could not resolve the Codex workspace's git common directory: " + stderr.GetAwaiter().GetResult().Trim());
        }
        return roots.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<string> ConfigArguments(IReadOnlyList<string> roots) =>
        ["-c", "sandbox_workspace_write.writable_roots=[" + string.Join(",", roots.Select(CodexMcpConfig.TomlString)) + "]",
         "-c", "sandbox_workspace_write.network_access=true"];

    public static object? Policy(string? mode, IReadOnlyList<string>? roots)
    {
        if (ToPolicyType(mode) is not { } type)
        {
            return null;
        }
        return mode == "workspace-write" && roots is not null
            ? new { type, writableRoots = roots, networkAccess = true }
            : (object)new { type };
    }

    // Maps a chosen kebab sandbox mode to the SandboxPolicy `type` discriminator, or `null` for an unknown one.
    public static string? ToPolicyType(string? mode) => mode switch
    {
        "read-only" => "readOnly",
        "workspace-write" => "workspaceWrite",
        "danger-full-access" => "dangerFullAccess",
        _ => null,
    };

    // AC-112: edit ceilings permit bounded writes; other ceilings preserve the profile's default.
    public static string? ForCeiling(string? permissionMode) => permissionMode switch
    {
        "acceptEdits" or "bypassPermissions" => "workspace-write",
        _ => null,
    };
}
