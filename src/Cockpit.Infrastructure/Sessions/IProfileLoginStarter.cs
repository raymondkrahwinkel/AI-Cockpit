using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Starts an in-app login attempt for a <see cref="SessionProfile"/>, generically: dispatches to whichever
/// provider plugin registered a <c>StartLogin</c> delegate — the same lookup <c>ProfileLoginChecker</c> does for
/// <c>IsLoggedIn</c>. Public (not internal), mirroring <see cref="IPluginProviderRegistry"/>, so the profile-editor
/// and transcript-row view models can resolve it from the container directly.
/// </summary>
public interface IProfileLoginStarter
{
    /// <summary>
    /// Whether this profile's provider declared a <c>StartLogin</c> at all — an existence check, no subprocess
    /// spawned, for a caller deciding whether to show a login affordance for this profile in the first place
    /// (Ollama/LM Studio, Gemini, Kimi and any other provider that declares no gate all read false here).
    /// </summary>
    bool CanStartLogin(SessionProfile profile);

    /// <summary>Starts the flow, or <see langword="null"/> when this profile's provider offers no in-app login.</summary>
    ILoginFlow? StartLogin(SessionProfile profile, CancellationToken cancellationToken);
}
