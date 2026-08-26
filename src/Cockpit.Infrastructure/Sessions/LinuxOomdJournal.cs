using System.ComponentModel;
using System.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.Infrastructure.Sessions;

// AC-1060: asks `systemd-oomd`'s own journal whether it killed a session's cgroup. These lines are readable
// without root, and the unit filter keeps Cockpit's own log line naming the same group from reading as a kill.
internal static class LinuxOomdJournal
{
    // Long enough to cover a session whose stream took a moment to end after the kill, short enough that a
    // group name reused by a later pid cannot be answered with an older kill.
    private static readonly TimeSpan LookBack = TimeSpan.FromMinutes(5);

    // journalctl on a machine with a large journal is not instant, and this runs on the way out of a session.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // Null means "cannot say it was oomd", never "it was not" — the caller stays silent on null rather than
    // guessing at a cause, which is the whole point of criterion 2.
    public static async Task<OomdKillLine?> FindKillAsync(string cgroupName, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            var start = new ProcessStartInfo("journalctl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-u");
            start.ArgumentList.Add("systemd-oomd");
            start.ArgumentList.Add("--since");
            start.ArgumentList.Add($"-{(int)LookBack.TotalMinutes} min");
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add("cat");

            using var journal = Process.Start(start);
            if (journal is null)
            {
                return null;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Timeout);

            var output = await journal.StandardOutput.ReadToEndAsync(deadline.Token);
            await journal.WaitForExitAsync(deadline.Token);

            return _LastKillOf(output, cgroupName);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or Win32Exception or InvalidOperationException)
        {
            // No journalctl, no permission, or it took too long. Saying nothing is the honest answer; the
            // session ended either way and a guessed reason is worse than none.
            return null;
        }
    }

    // The last one, not the first: a group name is a pid, and a pid comes round again.
    private static OomdKillLine? _LastKillOf(string output, string cgroupName)
    {
        OomdKillLine? found = null;
        foreach (var line in output.Split('\n'))
        {
            if (OomdKillLine.Parse(line) is { } kill && kill.CgroupName == cgroupName)
            {
                found = kill;
            }
        }

        return found;
    }
}
