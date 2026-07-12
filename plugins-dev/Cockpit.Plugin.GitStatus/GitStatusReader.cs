using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Cockpit.Plugin.GitStatus;

/// <summary>
/// Reads one repository's status by running <c>git status --porcelain --branch</c> in it (#1) — a single,
/// cheap, machine-parseable command that yields the branch, its ahead/behind vs upstream (from the first
/// <c>## …</c> line) and the working-tree change count (every other line). Requires <c>git</c> on PATH.
/// Any failure (not a repo, git missing, path gone) is captured on <see cref="GitRepoStatus.Error"/> rather
/// than thrown, so one bad entry never blocks the others.
/// </summary>
internal sealed partial class GitStatusReader
{
    [GeneratedRegex(@"ahead (\d+)")]
    private static partial Regex AheadRegex();

    [GeneratedRegex(@"behind (\d+)")]
    private static partial Regex BehindRegex();

    public async Task<GitRepoStatus> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var name = _RepoName(path);
        try
        {
            var output = await _RunGitAsync(path, ["status", "--porcelain", "--branch"], cancellationToken);
            return _Parse(path, name, output);
        }
        catch (Exception exception)
        {
            return new GitRepoStatus(path, name, "?", 0, 0, 0, HasUpstream: false, exception.Message);
        }
    }

    private static string _RepoName(string path)
    {
        try
        {
            var trimmed = path.TrimEnd('/', '\\');
            var name = new DirectoryInfo(trimmed).Name;
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
        catch
        {
            return path;
        }
    }

    internal static GitRepoStatus _Parse(string path, string name, string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var branchLine = lines.FirstOrDefault(line => line.StartsWith("## ", StringComparison.Ordinal)) ?? "## ?";
        var uncommitted = lines.Count(line => !line.StartsWith("## ", StringComparison.Ordinal));

        var header = branchLine.Length > 3 ? branchLine[3..] : string.Empty;
        string branch;
        var hasUpstream = false;

        if (header.StartsWith("No commits yet on ", StringComparison.Ordinal))
        {
            branch = header["No commits yet on ".Length..].Trim();
        }
        else
        {
            var dots = header.IndexOf("...", StringComparison.Ordinal);
            if (dots >= 0)
            {
                branch = header[..dots];
                hasUpstream = true;
            }
            else
            {
                var space = header.IndexOf(' ');
                branch = (space >= 0 ? header[..space] : header).Trim();
            }
        }

        var ahead = _MatchCount(AheadRegex(), branchLine);
        var behind = _MatchCount(BehindRegex(), branchLine);

        return new GitRepoStatus(path, name, string.IsNullOrEmpty(branch) ? "?" : branch, uncommitted, ahead, behind, hasUpstream, Error: null);
    }

    private static int _MatchCount(Regex regex, string input)
    {
        var match = regex.Match(input);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : 0;
    }

    private static async Task<string> _RunGitAsync(string workingDirectory, string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Could not run 'git' — is it installed and on PATH? ({exception.Message})", exception);
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            var message = stderr.Trim();
            throw new InvalidOperationException(message.Length > 0 ? message : $"git exited with code {process.ExitCode}");
        }

        return stdout;
    }
}
