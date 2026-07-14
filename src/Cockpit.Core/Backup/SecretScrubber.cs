using System.Text.Json.Nodes;
using Cockpit.Core.Secrets;

namespace Cockpit.Core.Backup;

/// <summary>
/// Takes the secrets out of the settings before they go into an archive (#70). A backup without credentials is the
/// default, and this is what makes that claim true rather than merely intended.
/// <para>
/// Which fields count as secret (<see cref="SecretFields"/>) and how the settings are traversed
/// (<see cref="SecretJsonWalker"/>) are shared with the encryption layer, which encrypts exactly the fields this
/// empties. Two lists would drift, and a field the protector encrypts but the scrubber misses is a token in a
/// backup that says it carries none.
/// </para>
/// </summary>
public static class SecretScrubber
{
    /// <summary>Empties every secret-looking field in <paramref name="settings"/>, in place, and returns the paths it emptied — which is what the restore tells the operator they must type in again.</summary>
    public static IReadOnlyList<string> Scrub(JsonNode settings) => Scrub(settings, SecretFields.ByName);

    /// <summary>Overload taking the field rule, so a backup also empties the fields the plugins declared as secret.</summary>
    public static IReadOnlyList<string> Scrub(JsonNode settings, SecretFields fields) =>
        SecretJsonWalker.Transform(settings, fields, (_, _) => string.Empty);

    /// <summary>Whether a field's name says it holds a credential.</summary>
    public static bool IsSecret(string name) => SecretFields.ByName.IsSecret(name);
}
