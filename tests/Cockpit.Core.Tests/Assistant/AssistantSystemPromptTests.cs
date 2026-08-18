using Cockpit.Core.Assistant;

namespace Cockpit.Core.Tests.Assistant;

/// <summary>
/// AC-922: the assistant no longer tells itself markdown is unbearable across the board — tables and code
/// blocks render in the chat window and are skipped rather than read aloud, so a text-based Assistant Profile
/// running under <see cref="AssistantSystemPrompt.Default"/> knows it can reach for them.
/// </summary>
public sealed class AssistantSystemPromptTests
{
    [Fact]
    public void Default_DoesNotForbidMarkdownOutright()
    {
        Assert.DoesNotContain("No markdown", AssistantSystemPrompt.Default, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_ExplainsTablesRenderAndAreNeverReadAloud()
    {
        Assert.Contains("pipe-table", AssistantSystemPrompt.Default, StringComparison.Ordinal);
        Assert.Contains("never get read aloud", AssistantSystemPrompt.Default, StringComparison.Ordinal);
    }
}
