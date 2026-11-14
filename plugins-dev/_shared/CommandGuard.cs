using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Cockpit.Core.Profiles;

namespace Cockpit.Plugins.Shared;

internal sealed record GuardDecision(bool Deny, string? Message)
{
    public static readonly GuardDecision Allow = new(false, null);
    public IReadOnlyList<GuardViolation> Violations { get; init; } = [];

    // Deny wins; two warnings are both kept.
    public GuardDecision With(GuardDecision other)
    {
        var result = Deny ? this : other.Deny || Message is null ? other : other.Message is null ? this : new(false, Message + "\n" + other.Message);
        return result with { Violations = [.. Violations, .. other.Violations] };
    }
}

// What a session owns: the processes in its own tree and the containers labelled with its pane.
internal sealed record KillScope(Func<int, bool> OwnsProcess, Func<IReadOnlyList<string>, bool> OwnsContainers, Func<IReadOnlyList<string>, IReadOnlyList<string>?> ComposeContainers);

// AC-2250. Known gap (B4): process objects killed from inside an interpreter (.Kill(), Invoke-CimMethod Terminate, wmic) pass this heuristic.
internal static class CommandGuard
{
    public const string HostPidVariable = "COCKPIT_HOST_PID";
    public const string PaneVariable = "COCKPIT_PANE_ID";
    public const string JobVariable = "COCKPIT_SESSION_JOB";
    public const string PaneLabel = "cockpit.pane";

    private const string OwnPids = "stop only processes in this session's own tree, by literal PID (Stop-Process -Id <pid>, taskkill /PID <pid>, kill <pid>)";
    private static readonly HashSet<string> _Prefixes = new(StringComparer.OrdinalIgnoreCase) { "sudo", "nohup", "xargs", "env", "exec", "time", "nice" };
    private static readonly HashSet<string> _ValueOptions = new(StringComparer.OrdinalIgnoreCase) { "-n", "-i", "-l", "-p", "-d", "-s", "-e", "-u" };
    private static readonly HashSet<string> _DockerGlobals = new(StringComparer.OrdinalIgnoreCase) { "-h", "--host", "-c", "--context", "--config", "-l", "--log-level" };

    public static GuardDecision EvaluateKill(string command, KillScope scope, GuardrailMode mode)
    {
        if (mode == GuardrailMode.Off || _Reason(command, scope, 0) is not { } reason)
        {
            return GuardDecision.Allow;
        }
        return new GuardDecision(mode == GuardrailMode.Strict, mode == GuardrailMode.Strict ? reason : $"Cockpit guard (warn): {reason}")
        {
            Violations = [new(mode.ToString(), "shell", command, mode == GuardrailMode.Strict, "kill")],
        };
    }

    // The real ownership of a session: its Windows job when there is one (it keeps orphans), else the tree below `rootPid`.
    public static KillScope SessionScope(int? rootPid, string? paneId, string cwd, string? jobName)
    {
        var parents = new Lazy<Dictionary<int, int>>(_ParentsByPid);
        return new(
            pid => _InJob(jobName, pid) ?? (rootPid is { } root && _InTree(pid, root, parents.Value)),
            containers => !string.IsNullOrEmpty(paneId) && _Docker(cwd, ["inspect", "--format", $"{{{{index .Config.Labels \"{PaneLabel}\"}}}}", .. containers]) is { } labels
                && labels.Count == containers.Count && labels.All(label => label == paneId),
            globals => _Docker(cwd, ["compose", .. globals, "ps", "-aq"]));
    }

    // The session root for a process running inside it: its ancestor whose parent is the cockpit.
    public static int? RootBelow(int hostPid, int self)
    {
        var parents = _ParentsByPid();
        var seen = new HashSet<int>();
        for (var current = self; seen.Add(current) && parents.TryGetValue(current, out var parent); current = parent)
        {
            if (parent == hostPid)
            {
                return current;
            }
        }
        return null;
    }

    // Splits on ; && || | and newlines, keeping quoted text whole; backslashes stay literal for Windows paths.
    public static IEnumerable<List<string>> Segments(string command)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var quoted = false;
        char quote = '\0';
        void EndWord()
        {
            if (word.Length > 0 || quoted)
            {
                words.Add(word.ToString());
            }
            word.Clear();
            quoted = false;
        }
        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    word.Append(character);
                }
            }
            else if (character is '\'' or '"')
            {
                quote = character;
                quoted = true;
            }
            else if (character is ';' or '|' or '\n' or '\r' or '(' or ')' || character == '&' && (index == 0 || command[index - 1] != '>'))
            {
                EndWord();
                if (words.Count > 0)
                {
                    yield return words;
                    words = [];
                }
            }
            else if (char.IsWhiteSpace(character))
            {
                EndWord();
            }
            else if (character == '>' && (index == 0 || command[index - 1] != '>') && !word.ToString().All(char.IsAsciiDigit))
            {
                EndWord();
                word.Append(character);
            }
            else
            {
                word.Append(character);
            }
        }
        EndWord();
        if (words.Count > 0)
        {
            yield return words;
        }
    }

    private static string? _Reason(string command, KillScope scope, int depth)
    {
        foreach (var segment in Segments(command))
        {
            var words = _WithoutPrefixes(segment);
            if (words.Count == 0)
            {
                continue;
            }
            var name = Path.GetFileNameWithoutExtension(words[0]).ToLowerInvariant();
            var rest = words.Skip(1).Select(word => word.StartsWith("//") ? word[1..] : word).ToList();
            var reason = name switch
            {
                "powershell" or "pwsh" or "bash" or "sh" or "zsh" or "cmd" when depth < 3 && _Inner(name, rest) is { } inner => _Reason(inner, scope, depth + 1),
                "dotnet" when rest is ["build-server", ..] && rest.Contains("shutdown", StringComparer.OrdinalIgnoreCase) =>
                    "'dotnet build-server shutdown' stops the build servers of every session; build with -p:UseSharedCompilation=false or leave them running.",
                "pkill" or "killall" => $"'{name}' stops processes by name, which can hit other sessions; {OwnPids}.",
                "taskkill" => _TaskKill(rest, scope),
                "stop-process" or "spps" or "kill" => _StopProcess(rest, scope),
                "docker" => _DockerCommand(rest, scope),
                "docker-compose" => _Compose(rest, scope),
                _ => null,
            };
            if (reason is not null)
            {
                return reason;
            }
        }
        return null;
    }

    private static List<string> _WithoutPrefixes(List<string> words)
    {
        var index = 0;
        while (index < words.Count && _Prefixes.Contains(Path.GetFileNameWithoutExtension(words[index])))
        {
            for (index++; index < words.Count && (words[index].StartsWith('-') || words[index].Contains('=')); index++)
            {
                index += _ValueOptions.Contains(words[index]) ? 1 : 0;
            }
        }
        return words.Skip(index).ToList();
    }

    // The command a shell wrapper runs: -Command / -c / /c take the rest, -EncodedCommand is decoded.
    private static string? _Inner(string shell, List<string> rest)
    {
        for (var index = 0; index < rest.Count; index++)
        {
            var flag = rest[index].ToLowerInvariant();
            if (shell == "cmd" ? flag is "/c" or "/k" : flag is "-c" or "-lc" || shell is "powershell" or "pwsh" && "-command".StartsWith(flag) && flag.Length > 1)
            {
                return string.Join(' ', rest.Skip(index + 1));
            }
            if (shell is "powershell" or "pwsh" && flag is "-encodedcommand" or "-enc" or "-e" or "-ec" && index + 1 < rest.Count)
            {
                try
                {
                    return Encoding.Unicode.GetString(Convert.FromBase64String(rest[index + 1]));
                }
                catch (FormatException)
                {
                    return "Stop-Process $unreadable";
                }
            }
        }
        return null;
    }

    private static string? _TaskKill(List<string> rest, KillScope scope)
    {
        var pids = new List<string>();
        for (var index = 0; index < rest.Count; index++)
        {
            var flag = rest[index].ToLowerInvariant();
            if (flag is "/im" or "-im" or "/fi" or "-fi")
            {
                return $"'taskkill {rest[index]}' stops processes by name or filter, which can hit other sessions; {OwnPids}.";
            }
            if (flag is "/pid" or "-pid" && index + 1 < rest.Count)
            {
                pids.Add(rest[++index]);
            }
        }
        return _Pids(pids, scope);
    }

    // Stop-Process and both kills: PowerShell's alias and the Unix command.
    private static string? _StopProcess(List<string> rest, KillScope scope)
    {
        var pids = new List<string>();
        var signalSeen = false;
        var jobSeen = false;
        var targetsOnly = false;
        for (var index = 0; index < rest.Count; index++)
        {
            var word = rest[index];
            var flag = word.ToLowerInvariant();
            if (targetsOnly || !word.StartsWith('-') || word.Length > 1 && char.IsAsciiDigit(word[1]) && signalSeen)
            {
                jobSeen |= word.StartsWith('%');
                pids.AddRange(word.StartsWith('%') ? [] : word.Split(',', StringSplitOptions.RemoveEmptyEntries));
            }
            else if (flag == "--")
            {
                targetsOnly = true;
            }
            else if (flag is "-name" or "-processname" || flag.Length > 2 && "-name".StartsWith(flag))
            {
                return $"'Stop-Process -Name' stops processes by name, which can hit other sessions; {OwnPids}.";
            }
            else if (flag is "-id" or "-inputobject" && index + 1 < rest.Count)
            {
                pids.AddRange(flag == "-id" ? rest[++index].Split(',', StringSplitOptions.RemoveEmptyEntries) : [rest[++index]]);
            }
            else if (flag is "-l" or "-list")
            {
                return null;
            }
            else if (!signalSeen && !flag.StartsWith("--") && flag is not "-force" and not "-passthru" and not "-whatif" and not "-confirm")
            {
                signalSeen = true;
                index += flag is "-s" or "-n" ? 1 : 0;
            }
        }
        return pids.Count == 0 && !jobSeen ? $"the process to stop is not named in the command (piped or computed); {OwnPids}." : _Pids(pids, scope);
    }

    private static string? _Pids(List<string> pids, KillScope scope)
    {
        foreach (var text in pids)
        {
            if (!int.TryParse(text, out var pid) || pid <= 0)
            {
                return $"'{text}' is not a literal PID, so its owner cannot be checked; {OwnPids}.";
            }
            if (!scope.OwnsProcess(pid))
            {
                return $"PID {pid} is not in this session's process tree; {OwnPids}.";
            }
        }
        return null;
    }

    private static string? _DockerCommand(List<string> rest, KillScope scope)
    {
        var index = 0;
        while (index < rest.Count && rest[index].StartsWith('-'))
        {
            index += _DockerGlobals.Contains(rest[index]) ? 2 : 1;
        }
        if (index < rest.Count && rest[index].Equals("container", StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }
        if (index >= rest.Count)
        {
            return null;
        }
        var verb = rest[index].ToLowerInvariant();
        var arguments = rest.Skip(index + 1).ToList();
        if (verb == "compose")
        {
            return _Compose(arguments, scope);
        }
        var force = arguments.Any(word => word == "--force" || word.StartsWith('-') && !word.StartsWith("--") && word.Contains('f'));
        if (verb is not ("stop" or "kill" or "restart") && !(verb is "rm" && force))
        {
            return null;
        }
        var containers = new List<string>();
        for (var at = 0; at < arguments.Count; at++)
        {
            if (arguments[at].StartsWith('-'))
            {
                at += arguments[at] is "-t" or "--time" or "-s" or "--signal" or "--timeout" ? 1 : 0;
            }
            else
            {
                containers.Add(arguments[at]);
            }
        }
        return _Containers(containers, scope);
    }

    private static string? _Compose(List<string> arguments, KillScope scope)
    {
        var at = arguments.FindIndex(word => word is "down" or "stop" or "kill" or "rm" or "restart");
        if (at < 0)
        {
            return null;
        }
        var containers = scope.ComposeContainers(arguments.Take(at).ToList());
        return containers is null
            ? $"the containers of this compose project cannot be listed; stop only containers labelled {PaneLabel}=$COCKPIT_PANE_ID."
            : _Containers(containers, scope);
    }

    private static string? _Containers(IReadOnlyList<string> containers, KillScope scope) =>
        containers.Count == 0 ? null
        : containers.Any(container => container.Contains('$') || container.Contains('`')) || !scope.OwnsContainers(containers)
            ? $"'{string.Join(' ', containers)}' is not labelled {PaneLabel}=$COCKPIT_PANE_ID; stop only containers this session started with --label {PaneLabel}=$COCKPIT_PANE_ID."
            : null;

    // ponytail: walks parent ids, so a stale parent id reused inside the tree counts as owned; the OS-level boundary is [h].
    private static bool _InTree(int pid, int root, Dictionary<int, int> parents)
    {
        var seen = new HashSet<int>();
        for (var current = pid; seen.Add(current); )
        {
            if (current == root)
            {
                return true;
            }
            if (!parents.TryGetValue(current, out current))
            {
                return false;
            }
        }
        return false;
    }

    private static bool? _InJob(string? jobName, int pid)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(jobName) || OpenJobObjectW(JobObjectQuery, false, jobName) is var job && job == 0)
        {
            return null;
        }
        try
        {
            var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (process == 0)
            {
                return false;
            }
            try
            {
                return IsProcessInJob(process, job, out var inJob) && inJob;
            }
            finally
            {
                CloseHandle(process);
            }
        }
        finally
        {
            CloseHandle(job);
        }
    }

    private static IReadOnlyList<string>? _Docker(string cwd, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (Directory.Exists(cwd))
        {
            start.WorkingDirectory = cwd;
        }
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return process.ExitCode == 0 ? output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static Dictionary<int, int> _ParentsByPid()
    {
        var parents = new Dictionary<int, int>();
        if (OperatingSystem.IsWindows())
        {
            var snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot == -1)
            {
                return parents;
            }
            try
            {
                var entry = new ProcessEntry32 { Size = Marshal.SizeOf<ProcessEntry32>() };
                for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                {
                    parents[entry.ProcessId] = entry.ParentProcessId;
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }
        else if (Directory.Exists("/proc/self"))
        {
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                try
                {
                    if (int.TryParse(Path.GetFileName(directory), out var pid)
                        && File.ReadAllText(Path.Combine(directory, "stat")) is var stat
                        && stat[(stat.LastIndexOf(')') + 2)..].Split(' ') is [_, var parent, ..])
                    {
                        parents[pid] = int.Parse(parent);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
                {
                }
            }
        }
        else
        {
            using var process = Process.Start(new ProcessStartInfo("ps", "-A -o pid=,ppid=") { RedirectStandardOutput = true, UseShellExecute = false });
            foreach (var line in process?.StandardOutput.ReadToEnd().Split('\n') ?? [])
            {
                if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var pid, var parent] && int.TryParse(pid, out var child) && int.TryParse(parent, out var parentId))
                {
                    parents[child] = parentId;
                }
            }
        }
        return parents;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public int Size;
        public int Usage;
        public int ProcessId;
        public nint DefaultHeapId;
        public int ModuleId;
        public int Threads;
        public int ParentProcessId;
        public int PriorityClassBase;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(int flags, int processId);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    private const uint JobObjectQuery = 0x0004;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenJobObjectW(uint access, bool inherit, string name);

    [DllImport("kernel32", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool IsProcessInJob(nint process, nint job, out bool result);
}
