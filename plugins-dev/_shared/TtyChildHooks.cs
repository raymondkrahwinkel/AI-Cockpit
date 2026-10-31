using System.Text;

namespace Cockpit.Plugins.Shared;

internal static class TtyChildHooks
{
    public static string Journal(string statusFile) => statusFile + ".children.json";

    public static object Settings(string statusFile, string provider, IDictionary<string, string?> environment)
    {
        environment["COCKPIT_SUBAGENT_FILE"] = Journal(statusFile);
        var relay = Path.Combine(Path.GetDirectoryName(typeof(TtyChildHooks).Assembly.Location) ?? "", "HookRelay", OperatingSystem.IsWindows() ? "HookRelay.exe" : "HookRelay");
        string Quote(string value) => "'" + value.Replace("'", OperatingSystem.IsWindows() ? "''" : "'\\''") + "'";
        var command = $"{Quote(relay)} --environment {provider}";
        if (OperatingSystem.IsWindows())
        {
            command = "powershell.exe -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes("& " + command));
        }
        return new Dictionary<string, object>
        {
            ["SubagentStart"] = new[] { new { hooks = new[] { new { type = "command", command, timeout = 3, statusMessage = "Cockpit: record subagent start (metadata only)" } } } },
            ["SubagentStop"] = new[] { new { hooks = new[] { new { type = "command", command, timeout = 3, statusMessage = "Cockpit: reconcile subagent history (metadata only)" } } } },
        };
    }

    public static void RegisterFiles(string statusFile, ICollection<string> files)
    {
        files.Add(Journal(statusFile));
        files.Add(Journal(statusFile) + ".tmp");
        files.Add(statusFile + ".tmp");
    }
}
