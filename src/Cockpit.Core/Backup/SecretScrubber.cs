using System.Text.Json.Nodes;
using Cockpit.Core.Secrets;

namespace Cockpit.Core.Backup;

// Takes the secrets out of the settings before they go into an archive (#70). Which fields count as
// secret (`SecretFields`) and how settings are traversed (`SecretJsonWalker`) are shared with the
// encryption layer, so the two lists can't drift and leave a token behind.
public static class SecretScrubber
{
    // Empties every secret-looking field in `settings`, in place, and returns the paths it emptied — which is what the restore tells the operator they must type in again.
    public static IReadOnlyList<string> Scrub(JsonNode settings) => Scrub(settings, SecretFields.ByName);

    // Overload taking the field rule, so a backup also empties the fields the plugins declared as secret.
    public static IReadOnlyList<string> Scrub(JsonNode settings, SecretFields fields) =>
        SecretJsonWalker.Transform(settings, fields, (_, _) => string.Empty);

    // Whether a field's name says it holds a credential.
    public static bool IsSecret(string name) => SecretFields.ByName.IsSecret(name);
}
