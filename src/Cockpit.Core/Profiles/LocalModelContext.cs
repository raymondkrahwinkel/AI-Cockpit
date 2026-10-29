namespace Cockpit.Core.Profiles;

// One context value for the local model runtime and Cockpit's own turn guard. Invalid legacy values fall back to
// the profile default rather than disabling either boundary.
public static class LocalModelContext
{
    public const int DefaultTokens = 32_768;

    public static int Normalize(int tokens) => tokens > 0 ? tokens : DefaultTokens;
}
