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

    [Fact]
    public void Default_ExplainsWhatClearConversationKeepsAndArchives()
    {
        Assert.Contains("clear_conversation", AssistantSystemPrompt.Default, StringComparison.Ordinal);
        Assert.Contains("transcript is rolled aside", AssistantSystemPrompt.Default, StringComparison.Ordinal);
        Assert.Contains("memory and `note_state` stay", AssistantSystemPrompt.Default, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC-932: a project can pin its issue-tracker repo via `github.repository`/`GH_REPO`. The system prompt
    /// must tell the assistant to defer to that pin rather than pick a repo by its own content-based reading.
    /// </summary>
    [Fact]
    public void Default_DefersToPinnedIssueTrackerRepoOverOwnJudgment()
    {
        Assert.Contains("GH_REPO", AssistantSystemPrompt.Default, StringComparison.Ordinal);
        Assert.Contains("do not pass your own `--repo`", AssistantSystemPrompt.Default, StringComparison.Ordinal);
        Assert.Contains("No pin set: choosing by content, as before, is still right", AssistantSystemPrompt.Default, StringComparison.Ordinal);
    }
}
