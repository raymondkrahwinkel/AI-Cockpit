namespace Cockpit.Plugin.Depot.ProjectDefinition;

// One wrapped copy of a project's data key (AC-607): the data key, base64-encoded then encrypted under a key
// derived from either the project password or the recovery code, plus the salt that derivation used.
public sealed class CockpitProjectKeyWrapper
{
    public string Salt { get; set; } = string.Empty;

    public string WrappedDataKey { get; set; } = string.Empty;
}
