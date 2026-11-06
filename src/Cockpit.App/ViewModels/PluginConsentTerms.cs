using Cockpit.Core.Plugins;

namespace Cockpit.App.ViewModels;

// The wording every screen that asks for plugin consent uses (AC-511): the per-plugin consent dialog (#14) and the
// first-run wizard's batch, which asks once for a list instead of once per plugin.
public static class PluginConsentTerms
{
    // The standing terms of enabling a plugin at all — what it can reach, and that changed bytes ask again.
    public const string PermissionsNotice =
        "A plugin runs with your account's permissions — it is not sandboxed. Only enable plugins you trust. "
        + "If the file changes later, you will be asked to consent again.";

    // The same grant said per plugin, for a list where each row has to carry its own. Identical for every plugin:
    // a manifest's capabilities (AC-1730) add to these terms in the per-plugin dialog, they never narrow them.
    public const string PermissionSummary =
        "run with your account's permissions — your files, your network, your shell. Not sandboxed.";

    public static string PublisherMessage(PluginKeyConsentRequest request)
    {
        var change = request.Decision switch
        {
            PluginTrustDecision.KeyChanged => $"Publisher key changed from {request.PreviousFingerprint}. ",
            PluginTrustDecision.SourceChanged => $"Plugin source changed from {request.PreviousSource ?? "an existing or bundled installation"}. ",
            _ => "",
        };
        var publisher = request.Fingerprint is null ? "Unsigned source" : $"Signed by {request.Fingerprint}";
        return $"{change}{request.Id} {request.Version}\n{publisher}\nOrganization: {request.Organization ?? "none"}\nDepot host: {request.DepotHost}\n"
            + $"Source: {request.Repository ?? "not provided"} {request.Tag ?? ""}\nKey name (according to Depot): {request.KeyName ?? "not provided"}\n\n"
            + "The signature identifies the publisher; it does not make the plugin safe. Plugins run in-process with your account's permissions. "
            + "On the first installation, the key comes from the same Depot and does not protect against a compromised Depot.";
    }
}
