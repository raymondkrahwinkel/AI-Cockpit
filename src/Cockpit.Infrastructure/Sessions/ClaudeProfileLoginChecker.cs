using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Checks login state purely by file existence — never opens or reads
/// <c>.credentials.json</c>, per Iron Law #8 (do not print/inspect secret values).
/// </summary>
internal sealed class ClaudeProfileLoginChecker : IClaudeProfileLoginChecker, ISingletonService
{
    public bool IsLoggedIn(SessionProfile profile) =>
        profile.Claude is { ConfigDir: { } configDir } && File.Exists(Path.Combine(configDir, ".credentials.json"));
}
