using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

// Where the merge gate's last build per collection branch is kept (AC-1338): plugin storage, so the epic runner on
// the next click — a different code path, possibly a different app run — can see that the branch it is about to
// fork a sub from went red at the previous merge. One record per branch; a green build overwrites a red one.
internal sealed class AutopilotMergeBuildLedger(IPluginStorage storage)
{
    private const string KeyPrefix = "mergeGate:lastBuild:";

    public void Record(AutopilotMergeBuildRecord record) => storage.Set(_Key(record.Branch), record);

    public AutopilotMergeBuildRecord? LastBuild(string? branch) =>
        string.IsNullOrWhiteSpace(branch) ? null : storage.Get<AutopilotMergeBuildRecord>(_Key(branch));

    private static string _Key(string branch) => KeyPrefix + branch.ToLowerInvariant();
}
