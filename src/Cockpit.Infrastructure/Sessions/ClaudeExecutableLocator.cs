using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Finds the bundled <c>claude</c> executable installed by the Claude desktop app, under
/// <c>%APPDATA%\Claude\claude-code\&lt;version&gt;\claude.exe</c> — picks the highest version
/// folder when several are present (e.g. after an app update left an older one behind). The
/// version-comparison itself lives in <see cref="BundledClaudeExecutableSelector"/> (Core),
/// kept free of real filesystem access so it is testable against a fake directory tree.
/// </summary>
internal sealed class ClaudeExecutableLocator : IClaudeExecutableLocator, ISingletonService
{
    private const string ExecutableFileName = "claude.exe";

    public string? FindBundledExecutable()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            return null;
        }

        var claudeCodeRoot = Path.Combine(appData, "Claude", "claude-code");
        if (!Directory.Exists(claudeCodeRoot))
        {
            return null;
        }

        var versionFolderNames = Directory.EnumerateDirectories(claudeCodeRoot).Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!);

        return BundledClaudeExecutableSelector.SelectNewestExecutable(claudeCodeRoot, versionFolderNames, ExecutableFileName, File.Exists);
    }
}
