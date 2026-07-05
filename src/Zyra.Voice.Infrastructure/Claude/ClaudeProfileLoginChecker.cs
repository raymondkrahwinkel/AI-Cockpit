using Zyra.Voice.Core.Abstractions;
using Zyra.Voice.Core.Abstractions.Profiles;
using Zyra.Voice.Core.Profiles;

namespace Zyra.Voice.Infrastructure.Claude;

/// <summary>
/// Checks login state purely by file existence — never opens or reads
/// <c>.credentials.json</c>, per Iron Law #8 (do not print/inspect secret values).
/// </summary>
internal sealed class ClaudeProfileLoginChecker : IClaudeProfileLoginChecker, ISingletonService
{
    public bool IsLoggedIn(ClaudeProfile profile) =>
        File.Exists(Path.Combine(profile.ConfigDir, ".credentials.json"));
}
