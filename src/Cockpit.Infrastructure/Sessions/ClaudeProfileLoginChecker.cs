using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Checks login state purely by file existence — never opens or reads
/// <c>.credentials.json</c>, per Iron Law #8 (do not print/inspect secret values).
/// </summary>
internal sealed class ClaudeProfileLoginChecker : IClaudeProfileLoginChecker, ISingletonService
{
    public bool IsLoggedIn(SessionProfile profile)
    {
        if (profile.Claude is null)
        {
            return false;
        }

        // Resolve a blank config directory (a default/config-less Claude profile) to the CLI's own ~/.claude, where its
        // credentials live — checking the literal empty path would report every default profile logged out.
        var configDir = ClaudeConfigDirectory.Resolve(
            profile.Claude,
            Environment.GetEnvironmentVariable(ClaudeConfigDirectory.EnvironmentVariable),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        return File.Exists(Path.Combine(configDir, ".credentials.json"));
    }
}
