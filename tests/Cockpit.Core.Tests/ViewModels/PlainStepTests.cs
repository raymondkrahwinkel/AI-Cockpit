using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-1530: a tool call as the step a non-developer reads at the Simple level — a sentence, never the tool name and
/// its raw input, with the folder relative to the working directory.
/// </summary>
public class PlainStepTests
{
    [Theory]
    [InlineData("Read", """{"file_path":"/work/Facturen/2026-Q3/kpn.pdf"}""", 3, "Read 3 files in Facturen › 2026-Q3")]
    [InlineData("Read", """{"file_path":"/work/Facturen/2026-Q3/kpn.pdf"}""", 1, "Read kpn.pdf in Facturen › 2026-Q3")]
    [InlineData("Read", """{"file_path":"/work/notes.md"}""", 1, "Read notes.md")]
    [InlineData("Write", """{"file_path":"/work/Rapportages/2026/Q3.xlsx","content":"x"}""", 1, "Created Q3.xlsx in Rapportages › 2026")]
    [InlineData("Edit", """{"file_path":"/work/ledger.md","old_string":"a","new_string":"b"}""", 1, "Changed ledger.md")]
    [InlineData("NotebookEdit", """{"notebook_path":"/work/sums.ipynb"}""", 1, "Changed sums.ipynb")]
    [InlineData("Bash", """{"command":"dotnet build"}""", 1, "Ran a command")]
    [InlineData("WebSearch", """{"query":"vat rates"}""", 1, "Looked something up online")]
    [InlineData("Task", """{"prompt":"sum it"}""", 1, "Asked a helper")]
    [InlineData("mcp__depot__write", """{"path":"Notes.md"}""", 1, "Used depot")]
    [InlineData("mcp__depot__write", """{"path":"Notes.md"}""", 4, "Used depot 4 times")]
    // Criterion 4: a tool this has no words for — Codex's own, say — is named, never left blank or shown as JSON.
    [InlineData("apply_patch", """{"patch":"*** Begin Patch"}""", 1, "Used apply_patch")]
    [InlineData("Read", "not json", 1, "Read a file")]
    public void Describe_SaysTheStepInPlainWords(string toolName, string inputJson, int count, string expected)
    {
        Assert.Equal(expected, PlainStep.Describe(toolName, inputJson, "/work", count).Sentence);
    }
}
