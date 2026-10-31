using Cockpit.Core.Sessions;
using Cockpit.Core.Swarm;

namespace Cockpit.Core.Tests.Swarm;

public class ConversationMergeTests
{
    [Fact]
    public void SplitConversationsMergeWithoutLossOrDuplicatesAndAskOnce()
    {
        var laptop = SwarmMemberId.FromPublicKey([1]);
        var desktop = SwarmMemberId.FromPublicKey([2]);
        var start = DateTimeOffset.UnixEpoch;
        TranscriptSnapshotEntry Row(string id, int minute) =>
            new(id, "UserText", id, null, null, null, null, false, start.AddMinutes(minute));
        MergeField Field(string value, int minute) => new(value, start.AddMinutes(minute));
        TranscriptSnapshotEntry[] shared = [Row("before-1", 0), Row("before-2", 1)];
        var laptopSide = new MergeSide(laptop, "Laptop", [.. shared, Row("laptop-1", 2), Row("laptop-2", 4), Row("laptop-3", 6)],
            new Dictionary<string, MergeField> { ["note_state"] = Field("laptop plan", 6), ["decision"] = Field("laptop decision", 4) });
        var desktopSide = new MergeSide(desktop, "Desktop", [.. shared, Row("desktop-1", 2), Row("desktop-2", 3), Row("desktop-3", 5)],
            new Dictionary<string, MergeField> { ["note_state"] = Field("desktop plan", 5), ["decision"] = Field("old decision", 0) });

        var merged = ConversationMerge.Merge(desktopSide, laptopSide, []);

        var firstAtTwo = string.CompareOrdinal(desktop.Value, laptop.Value) < 0 ? "desktop-1" : "laptop-1";
        var secondAtTwo = firstAtTwo == "desktop-1" ? "laptop-1" : "desktop-1";
        Assert.Equal(["before-1", "before-2", merged.Rows[2].Id, firstAtTwo, secondAtTwo, "desktop-2", "laptop-2", "desktop-3", "laptop-3"],
            merged.Rows.Select(row => row.Id));
        Assert.Equal("2 leaders seen — Laptop steps down, conversation merged", merged.Rows[2].Text);
        Assert.Equal(["Desktop", "Desktop", "Desktop", "Laptop", "Laptop", "Laptop"],
            merged.Rows.Skip(3).Where(row => row.WrittenWhileApartOn is not null).Select(row => row.WrittenWhileApartOn).Order());
        Assert.Equal("laptop decision", merged.Fields["decision"].Value);
        Assert.Equal("laptop plan", merged.Fields["note_state"].Value);
        var kept = Assert.Single(merged.Kept);
        Assert.Equal(("note_state", "desktop plan", "Desktop"), (kept.Key, kept.Version.Value, kept.Origin));
        Assert.True(merged.Asks);

        var again = ConversationMerge.Merge(desktopSide with { Rows = merged.Rows, Fields = merged.Fields }, laptopSide, merged.Kept);

        Assert.Equal(merged.Rows.Select(row => row.Id), again.Rows.Select(row => row.Id));
        Assert.Equal(again.Rows.Count, again.Rows.Select(row => row.Id).Distinct().Count());
        Assert.Single(again.Kept);
        Assert.False(again.Asks);
    }
}
