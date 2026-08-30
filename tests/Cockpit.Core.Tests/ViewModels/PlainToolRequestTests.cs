using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-489: the plain-language approval sentence is derived from the call, not written about it. These cover the
/// three things that has to mean — the sentence is read off the tool's own structural keys, nothing the model
/// composed can reach it, and a call that cannot be read reliably produces no sentence at all.
/// </summary>
public class PlainToolRequestTests
{
    // A value no template here contains, planted in every key the model fills in. If it shows up in a sentence,
    // the sentence is quoting the agent about its own request.
    private const string Poison = "ZYRAPOISON";

    [Theory]
    // The ticket's own example: a wildcard names the pattern rather than a count, because what it expands to is
    // known only where the command runs.
    [InlineData("Bash", """{"command":"mv ./inbox/*.pdf ./archive/2026-06/"}""", "Move the files matching ./inbox/*.pdf into ./archive/2026-06")]
    [InlineData("Bash", """{"command":"mv KPN.pdf Vattenfall.pdf Hosting.pdf ./archive/2026-06/"}""", "Move 3 files into ./archive/2026-06")]
    [InlineData("Bash", """{"command":"cp report.xlsx ./backup/"}""", "Copy report.xlsx into ./backup")]
    [InlineData("Bash", """{"command":"rm draft.txt notes.txt"}""", "Delete 2 files")]
    [InlineData("Bash", """{"command":"mkdir ./archive/2026-07/"}""", "Create the folder ./archive/2026-07")]
    [InlineData("Read", """{"file_path":"/work/invoices/june.csv"}""", "Read the file june.csv")]
    [InlineData("Write", """{"file_path":"/work/export.csv","content":"a,b,c"}""", "Create or replace the file export.csv")]
    [InlineData("Edit", """{"file_path":"/work/ledger.md","old_string":"x","new_string":"y"}""", "Change the file ledger.md")]
    public void Describe_ReadsTheSentenceOffTheCall(string toolName, string inputJson, string expected)
    {
        // Exact text, not a substring: the sentence is a fixed template plus values taken from the input, so
        // there is nothing here for a free-form phrasing to hide in.
        Assert.Equal(expected, PlainToolRequest.Describe(toolName, inputJson)?.Sentence);
    }

    [Theory]
    [InlineData("Bash", """{"command":"mv a.pdf b.pdf ./archive/","description":"ZYRAPOISON tidying the inbox"}""")]
    [InlineData("Bash", """{"command":"rm old.log","description":"ZYRAPOISON just a bit of cleanup, nothing important"}""")]
    [InlineData("Read", """{"file_path":"/work/june.csv","description":"ZYRAPOISON","prompt":"ZYRAPOISON"}""")]
    [InlineData("Write", """{"file_path":"/work/out.csv","content":"ZYRAPOISON","description":"ZYRAPOISON"}""")]
    [InlineData("Edit", """{"file_path":"/work/out.csv","old_string":"ZYRAPOISON","new_string":"ZYRAPOISON","explanation":"ZYRAPOISON"}""")]
    public void Describe_NeverRepeatsAnythingTheModelWrote(string toolName, string inputJson)
    {
        var request = PlainToolRequest.Describe(toolName, inputJson);

        // Asserted before the two below on purpose: without it this whole case would pass by describing nothing,
        // which is exactly the way a guarantee like this goes quietly missing.
        Assert.NotNull(request);

        Assert.DoesNotContain(Poison, request.Sentence, StringComparison.Ordinal);
        Assert.DoesNotContain(Poison, string.Join(" ", request.Paths), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_ListsTheFilesTheCallNames()
    {
        var request = PlainToolRequest.Describe("Bash", """{"command":"mv KPN.pdf Vattenfall.pdf ./archive/2026-06/"}""");

        Assert.Equal(["KPN.pdf", "Vattenfall.pdf"], request?.Paths);
    }

    [Fact]
    public void Describe_ListsNoFilesForACallThatTouchesNone()
    {
        // `mkdir` names a folder that does not exist yet — listing it under "the files this touches" would be a
        // claim about something that is not there.
        var request = PlainToolRequest.Describe("Bash", """{"command":"mkdir ./archive/2026-07/"}""");

        Assert.NotNull(request);
        Assert.Empty(request.Paths);
    }

    [Theory]
    // Anything that makes the line more than one plain call: the tokens would describe a fragment of what runs.
    [InlineData("""{"command":"mv ./inbox/*.pdf ./archive/ && rm -rf ./inbox"}""")]
    // Every token here is a path or a verb this could otherwise read, so the sequence itself is all that stands
    // between it and "Delete 4 files" — counting the shell's own operator as a file, on the heaviest verb there is.
    [InlineData("""{"command":"rm ./inbox/notes.txt && rm ./inbox/old-draft.txt"}""")]
    [InlineData("""{"command":"cat notes.txt | mail boss@example.com"}""")]
    [InlineData("""{"command":"rm notes.txt; rm ledger.md"}""")]
    [InlineData("""{"command":"mv $(ls inbox) ./archive/"}""")]
    [InlineData("""{"command":"cp report.csv ./backup/ > log.txt"}""")]
    [InlineData("""{"command":"mv 'my invoices.pdf' ./archive/"}""")]
    // A flag changes what the verb takes — `rm -r` a whole tree — and reading flags properly is a shell parser.
    [InlineData("""{"command":"rm -rf ./inbox"}""")]
    // No trailing separator: `mv a b` renames rather than moves into, and telling the two apart needs a
    // filesystem this never reads.
    [InlineData("""{"command":"mv old-name.pdf new-name.pdf"}""")]
    // Verbs outside the handful covered here.
    [InlineData("""{"command":"git push --force"}""")]
    [InlineData("""{"command":"dotnet build"}""")]
    // Not a command at all.
    [InlineData("""{"description":"tidy up"}""")]
    [InlineData("""{"command":""}""")]
    public void Describe_SaysNothingAboutACommandItCannotReadPlainly(string inputJson)
    {
        // Falling back to the raw call is the point: someone reading "move 12 pdfs to archive" while something
        // else happens is worse off than someone looking at a command line they do not understand.
        Assert.Null(PlainToolRequest.Describe("Bash", inputJson));
    }

    [Theory]
    [InlineData(null, """{"file_path":"/work/june.csv"}""")]
    [InlineData("WebFetch", """{"url":"https://example.com"}""")]
    [InlineData("Task", """{"prompt":"do the thing","description":"a subagent"}""")]
    [InlineData("mcp__depot__write", """{"path":"Notes.md","content":"..."}""")]
    [InlineData("Read", null)]
    [InlineData("Read", "")]
    [InlineData("Read", "not json at all")]
    [InlineData("Read", "[1,2,3]")]
    [InlineData("Read", """{"file_path":42}""")]
    [InlineData("Read", """{"pattern":"*.cs"}""")]
    public void Describe_SaysNothingAboutACallItDoesNotCover(string? toolName, string? inputJson)
    {
        Assert.Null(PlainToolRequest.Describe(toolName, inputJson));
    }
}
