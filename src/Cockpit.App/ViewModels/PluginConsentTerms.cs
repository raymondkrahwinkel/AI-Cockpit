namespace Cockpit.App.ViewModels;

// The wording every screen that asks for plugin consent uses (AC-511): the per-plugin consent dialog (#14) and
// the first-run wizard's batch, which asks once for a list instead of once per plugin.
// One constant rather than a copy per screen: the batch is only defensible if it says what the four separate
// dialogs it replaces said, and two literals drift the moment one of them is edited.
public static class PluginConsentTerms
{
    // The standing terms of enabling a plugin at all — what it can reach, and that changed bytes ask again.
    public const string PermissionsNotice =
        "A plugin runs with your account's permissions — it is not sandboxed. Only enable plugins you trust. "
        + "If the file changes later, you will be asked to consent again.";

    // The same grant said per plugin, for a list where each row has to carry its own. Identical for every plugin
    // because a plugin's reach is not narrowed per plugin yet — capability grants are EPIC AC-107, still open.
    public const string PermissionSummary =
        "run with your account's permissions — your files, your network, your shell. Not sandboxed.";
}
