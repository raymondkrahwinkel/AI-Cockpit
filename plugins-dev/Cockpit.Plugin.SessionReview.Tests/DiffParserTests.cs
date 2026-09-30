namespace Cockpit.Plugin.SessionReview.Tests;

// The structure the review panel is built from (AC-578): one `FileDiff` per file, with the old and new
// line number of every row, and the word-level span that points at what actually changed inside a replaced line.
public class DiffParserTests
{
    private const string TwoFiles = """
        diff --git a/src/Alpha.cs b/src/Alpha.cs
        index f3a3189..d0cf856 100644
        --- a/src/Alpha.cs
        +++ b/src/Alpha.cs
        @@ -10,4 +10,5 @@ public void Load()
             var first = 1;
        -    var second = Old();
        +    var second = New();
        +    var third = 3;
             Use(second);
        diff --git a/README.md b/README.md
        index 111..222 100644
        --- a/README.md
        +++ b/README.md
        @@ -1,2 +1,2 @@
         # Title
        -Runs on .NET 9.
        +Runs on .NET 10.
        """;

    [Fact]
    public void Parse_SplitsPerFileAndKeepsTheirOrder()
    {
        var files = DiffParser.Parse(TwoFiles);

        Assert.Equal(["src/Alpha.cs", "README.md"], files.Select(f => f.Path));
        Assert.All(files, f => Assert.Equal(FileChangeKind.Modified, f.Kind));
    }

    [Fact]
    public void Parse_CountsAddedAndRemovedPerFile()
    {
        var files = DiffParser.Parse(TwoFiles);

        Assert.Equal((2, 1), (files[0].Added, files[0].Removed));
        Assert.Equal((1, 1), (files[1].Added, files[1].Removed));
    }

    [Fact]
    public void Parse_NumbersRowsFromTheHunkHeader()
    {
        // The gutter is the whole point of parsing: a context line carries both numbers, an added line only the new
        // one, a removed line only the old one, and both counters advance independently from the @@ header.
        var rows = DiffParser.Parse(TwoFiles)[0].Rows;

        Assert.Equal((null, (int?)null), (rows[0].OldLine, rows[0].NewLine));      // the @@ header itself
        Assert.Equal(((int?)10, (int?)10), (rows[1].OldLine, rows[1].NewLine));    // context
        Assert.Equal(((int?)11, (int?)null), (rows[2].OldLine, rows[2].NewLine));  // removed
        Assert.Equal(((int?)null, (int?)11), (rows[3].OldLine, rows[3].NewLine));  // added
        Assert.Equal(((int?)null, (int?)12), (rows[4].OldLine, rows[4].NewLine));  // added
        Assert.Equal(((int?)12, (int?)13), (rows[5].OldLine, rows[5].NewLine));    // context after the change
    }

    [Theory]
    [InlineData("new file mode 100644", "Added")]
    [InlineData("deleted file mode 100644", "Deleted")]
    [InlineData("rename to b/x", "Renamed")]
    [InlineData("Binary files a/x and b/x differ", "Binary")]
    public void Parse_ReadsTheFileKindFromItsHeader(string header, string expected)
    {
        // FileChangeKind is internal, so the expectation travels as its name — xunit's InlineData is public API.
        var diff = $"diff --git a/x.bin b/x.bin\n{header}\n";

        Assert.Equal(expected, DiffParser.Parse(diff)[0].Kind.ToString());
    }

    [Fact]
    public void Parse_TakesTheNewNameOfARenamedFile()
    {
        var diff = "diff --git a/old/A.cs b/new/B.cs\nsimilarity index 96%\nrename from old/A.cs\nrename to new/B.cs\n";

        Assert.Equal("new/B.cs", DiffParser.Parse(diff)[0].Path);
    }

    [Fact]
    public void Parse_TreatsPlusAndMinusInsideAHunkAsContentNotAsHeaders()
    {
        // A diff of a diff: the +++/--- lines here belong to the file's own text, not to git's header.
        var diff = "diff --git a/patch.txt b/patch.txt\n--- a/patch.txt\n+++ b/patch.txt\n@@ -1,2 +1,2 @@\n---- a/inner\n++++ b/inner\n";

        var rows = DiffParser.Parse(diff)[0].Rows;

        Assert.Equal(DiffLineKind.Removed, rows[1].Kind);
        Assert.Equal("--- a/inner", rows[1].Text);
        Assert.Equal(DiffLineKind.Added, rows[2].Kind);
        Assert.Equal("+++ b/inner", rows[2].Text);
    }

    [Fact]
    public void Parse_ReturnsNothingForEmptyOrHeaderlessInput()
    {
        Assert.Empty(DiffParser.Parse(string.Empty));
        Assert.Empty(DiffParser.Parse("not a diff at all\njust text\n"));
    }
}
