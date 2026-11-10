using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Plugin.ClaudeProvider;

internal static class ClaudeSandbox
{
    public static void Apply(JsonObject settings, IReadOnlyDictionary<string, string>? environment)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) || environment?.GetValueOrDefault("COCKPIT_GUARD_MODE") != "Strict")
        {
            return;
        }

        settings["sandbox"] = new JsonObject
        {
            ["enabled"] = true,
            ["autoAllowBashIfSandboxed"] = true,
            ["allowUnsandboxedCommands"] = false,
            ["failIfUnavailable"] = false,
            ["filesystem"] = new JsonObject
            {
                ["allowWrite"] = JsonSerializer.SerializeToNode(
                    (environment.GetValueOrDefault("COCKPIT_GUARD_ROOTS") ?? string.Empty)
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            },
        };
        // AC-2252: the runtime rejects allowedDomains ["*"]; Claude Code accepts this permission for every sandbox host.
        settings["permissions"] = new JsonObject { ["allow"] = new JsonArray("WebFetch(domain:*)") };
        Console.Error.WriteLine("cockpit-guard: warning: Claude OS sandbox requested with failIfUnavailable=false; if unavailable, commands run without OS isolation. Cockpit guards remain active.");
    }
}
