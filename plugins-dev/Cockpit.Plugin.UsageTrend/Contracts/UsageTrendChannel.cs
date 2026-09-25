using System.Text.Json;

namespace Cockpit.Plugin.UsageTrend.Contracts;

// AC-1395: the widget's cache-backed history lives behind ICockpitHost.Cache, which ICockpitUiHost does not
// expose, so the UI part asks the backend over the plugin's own channel. Compiled into both assemblies as a
// linked source file (AC-1390's pattern), so the UI part never references the backend part.
internal static class UsageTrendChannel
{
    // Payload: UsageTrendHistoryRequest. Answers this instance's pruned history.
    public const string Get = "history.get";

    // Payload: UsageTrendAppendRequest. Appends (or debounces) the candidate; answers the resulting history.
    public const string Append = "history.append";

    // Payload: UsageTrendSeedRequest. Writes the history only when this instance's cache is still empty — the
    // one-time migration of what a pre-cache install left in the widget's settings storage.
    public const string Seed = "history.seed";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

internal sealed record UsageTrendHistoryRequest(string InstanceId);

internal sealed record UsageTrendAppendRequest(string InstanceId, UsageTrendSample Candidate);

internal sealed record UsageTrendSeedRequest(string InstanceId, IReadOnlyList<UsageTrendSample> History);
