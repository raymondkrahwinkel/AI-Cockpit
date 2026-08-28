using Cockpit.Core.Usage;

namespace Cockpit.Tests.Shared;

public static class UsageSnapshotContract
{
    public static IReadOnlyList<string> CommonFieldDifferences(UsageSnapshot expected, UsageSnapshot actual)
    {
        List<string> differences = [];
        Compare(nameof(actual.PaneId), expected.PaneId, actual.PaneId, differences);
        Compare(nameof(actual.StartedAt), expected.StartedAt, actual.StartedAt, differences);
        Compare(nameof(actual.RecordedAt), expected.RecordedAt, actual.RecordedAt, differences);
        Compare(nameof(actual.ProfileLabel), expected.ProfileLabel, actual.ProfileLabel, differences);
        Compare(nameof(actual.Model), expected.Model, actual.Model, differences);
        Compare(nameof(actual.InputTokens), expected.InputTokens, actual.InputTokens, differences);
        Compare(nameof(actual.OutputTokens), expected.OutputTokens, actual.OutputTokens, differences);
        Compare(nameof(actual.CacheReadInputTokens), expected.CacheReadInputTokens, actual.CacheReadInputTokens, differences);
        Compare(nameof(actual.CacheCreationInputTokens), expected.CacheCreationInputTokens, actual.CacheCreationInputTokens, differences);
        Compare(nameof(actual.TotalCostUsd), expected.TotalCostUsd, actual.TotalCostUsd, differences);
        Compare(nameof(actual.Turns), expected.Turns, actual.Turns, differences);
        return differences;
    }

    private static void Compare<T>(string name, T expected, T actual, ICollection<string> differences)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            differences.Add(name);
        }
    }
}
