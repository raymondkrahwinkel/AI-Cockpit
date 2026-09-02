using System.Text.Json.Nodes;
using Cockpit.Plugin.Kubernetes.Argo;

namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-576 phase 5, AC 6: argo_sync's approval must show what actually changes, not just the app name — these
// pin the shaping that turns Argo's managed-resources response into the consent card's lines.
public class ArgoManagedResourcesDiffTests
{
    private const string TwoModifiedOneUnchanged = """
        [
          {"kind":"Deployment","name":"cert-manager","namespace":"system-secrets","modified":true,"diff":"- replicas: 1\n+ replicas: 2"},
          {"kind":"ServiceAccount","name":"cert-manager","namespace":"system-secrets","modified":true,"diff":""},
          {"kind":"Service","name":"cert-manager","namespace":"system-secrets","modified":false}
        ]
        """;

    [Fact]
    public void Summarize_HeadlinesTheCounts_ThenListsOnlyWhatDiffers_WithItsLiteralDiffLines()
    {
        var root = JsonNode.Parse(TwoModifiedOneUnchanged);

        var (lines, modifiedCount) = ArgoManagedResourcesDiff.Summarize(root, maxLength: 10_000);

        Assert.Equal(2, modifiedCount);
        Assert.Equal("2 resource(s) differ from Git (1 unchanged)", lines[0]);
        Assert.Contains(lines, line => line.Contains("Deployment/cert-manager"));
        Assert.DoesNotContain(lines, line => line.Contains("Service/cert-manager") && !line.Contains("ServiceAccount"));
        // The card has to show what actually changes, not merely that something did.
        Assert.Contains("- replicas: 1", lines);
        Assert.Contains("+ replicas: 2", lines);
    }

    [Fact]
    public void Summarize_NoModifiedResources_ZeroCount_HeadlineOnly()
    {
        var root = JsonNode.Parse("""[{"kind":"Service","name":"x","modified":false}]""");

        var (lines, modifiedCount) = ArgoManagedResourcesDiff.Summarize(root, maxLength: 10_000);

        Assert.Equal(0, modifiedCount);
        Assert.Single(lines);
    }

    [Fact]
    public void Summarize_AcceptsAnItemsWrapper_NotOnlyABareArray()
    {
        var root = JsonNode.Parse("""{"items":[{"kind":"Service","name":"x","modified":true,"diff":"changed"}]}""");

        var (_, modifiedCount) = ArgoManagedResourcesDiff.Summarize(root, maxLength: 10_000);

        Assert.Equal(1, modifiedCount);
    }

    [Fact]
    public void Summarize_EmptyOrUnrecognizedPayload_IsZeroModified_NotAThrow()
    {
        Assert.Equal(0, ArgoManagedResourcesDiff.Summarize(JsonNode.Parse("{}"), 10_000).ModifiedCount);
        Assert.Equal(0, ArgoManagedResourcesDiff.Summarize(null, 10_000).ModifiedCount);
    }

    [Fact]
    public void Summarize_PastMaxLength_TruncatesAndSaysHowMany()
    {
        var root = JsonNode.Parse(TwoModifiedOneUnchanged);

        var (lines, _) = ArgoManagedResourcesDiff.Summarize(root, maxLength: 40);

        Assert.Contains(lines, line => line.Contains("more resource(s) not shown"));
    }
}
