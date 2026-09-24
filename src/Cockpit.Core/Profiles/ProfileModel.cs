namespace Cockpit.Core.Profiles;

// The model of a local provider's profile (#26), or null for a Claude or plugin profile. Moved out of the app's
// ProfileDisplay so the assistant gateway reports it without the app (AC-1375).
public static class ProfileModel
{
    public static string? Of(SessionProfile profile) => profile.ProviderConfig switch
    {
        OllamaConfig ollama => ollama.Model,
        LmStudioConfig lmStudio => lmStudio.Model,
        _ => null,
    };
}
