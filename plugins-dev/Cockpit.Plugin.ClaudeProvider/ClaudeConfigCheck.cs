using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

// The backend half of the profile config view: answers against this machine's file system, which is the profile's.
internal static class ClaudeConfigCheck
{
    public static ClaudeConfigChoicesAnswer Choices(
        IEnumerable<PluginDetectedProfile> detected,
        Func<string, string?> managedResolver,
        Func<string, bool> fileExists)
    {
        var dirs = new List<ClaudeConfigDirChoice>();
        foreach (var profile in detected)
        {
            dirs.Add(new ClaudeConfigDirChoice(profile.Label, ClaudeProviderConfig.Parse(profile.ConfigJson).ConfigDir ?? string.Empty));
        }

        var executables = new List<string>();
        if (managedResolver(ClaudeManagedCli.CliName) is { Length: > 0 } managed)
        {
            executables.Add(managed);
        }

        var onPath = ClaudeExecutableLocator.Resolve("claude");
        if (Path.IsPathRooted(onPath) && fileExists(onPath) && !executables.Contains(onPath))
        {
            executables.Add(onPath);
        }

        return new ClaudeConfigChoicesAnswer(dirs, executables);
    }

    public static ClaudeCheckConfigAnswer Check(
        ClaudeCheckConfigRequest request,
        Func<string, string?> managedResolver,
        Func<string, bool> directoryExists,
        Func<string, bool> fileExists)
    {
        var configDir = request.ConfigDir?.Trim() ?? string.Empty;
        var dirOk = true;
        string? dirText = null;
        if (configDir.Length > 0)
        {
            dirOk = directoryExists(configDir);
            dirText = dirOk ? "Folder found." : "Folder does not exist — the profile cannot be saved until it does.";
        }

        // Resolves exactly as a session spawn will (pin > managed > PATH), so this never contradicts the managed panel.
        var command = request.ExecutablePath?.Trim() is { Length: > 0 } path ? path : "claude";
        var isPinned = Path.IsPathRooted(command);
        var resolved = ClaudeExecutableLocator.Resolve(command, managedResolver);
        var managedPath = managedResolver(ClaudeManagedCli.CliName);

        if (!isPinned && !string.IsNullOrEmpty(managedPath) && string.Equals(resolved, managedPath, StringComparison.Ordinal))
        {
            return new ClaudeCheckConfigAnswer(dirOk, dirText, true, $"Managed by Cockpit — this copy is used: {resolved}");
        }

        if (isPinned && fileExists(resolved))
        {
            return new ClaudeCheckConfigAnswer(dirOk, dirText, true, $"Using pinned path (not managed): {resolved}");
        }

        if (Path.IsPathRooted(resolved) && fileExists(resolved))
        {
            return new ClaudeCheckConfigAnswer(dirOk, dirText, true, $"Found on PATH (not managed): {resolved}");
        }

        return new ClaudeCheckConfigAnswer(dirOk, dirText, false, "Not found — install it below, put claude on PATH, or paste an absolute path.");
    }

    public static JsonElement Serialize<T>(T value) => JsonSerializer.SerializeToElement(value, ClaudeProviderChannel.Json);
}
