namespace Cockpit.Plugin.Depot.Secrets;

// Mirrors Cockpit.Core.Secrets.SecretFields.ByName exactly (AC-607) — same six hint substrings, same
// case-insensitive rule. Used to decide which ExtensionData keys CockpitProjectDefinitionExtensionDataGuard must
// refuse unless already-encrypted. No `declared` extensibility here: nothing in this plugin needs it (YAGNI).
public static class SensitiveFieldNameHeuristic
{
    private static readonly string[] _Names =
    [
        "token",
        "apikey",
        "api_key",
        "secret",
        "password",
        "webhook",
    ];

    public static bool IsSecretName(string name) =>
        _Names.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase));
}
