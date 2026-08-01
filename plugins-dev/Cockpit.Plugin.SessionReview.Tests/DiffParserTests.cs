namespace Cockpit.Plugin.SessionReview.Tests;

/// <summary>
/// The structure the review panel is built from (AC-578): one <see cref="FileDiff"/> per file, with the old and new
/// line number of every row, and the word-level span that points at what actually changed inside a replaced line.
/// </summary>
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

    [Fact]
    public void Parse_StripsTheMarkerFromTheText()
    {
        var rows = DiffParser.Parse(TwoFiles)[0].Rows;

        Assert.Equal("    var second = Old();", rows[2].Text);
        Assert.Equal("    var second = New();", rows[3].Text);
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
    public void Parse_NamesADeletedFileFromItsOldSide()
    {
        // A deletion has "+++ /dev/null", which names no file — the name has to come off the --- line.
        var diff = "diff --git a/src/Gone.cs b/src/Gone.cs\ndeleted file mode 100644\n--- a/src/Gone.cs\n+++ /dev/null\n@@ -1,1 +0,0 @@\n-one\n";

        var file = DiffParser.Parse(diff)[0];

        Assert.Equal("src/Gone.cs", file.Path);
        Assert.Equal(FileChangeKind.Deleted, file.Kind);
        Assert.Equal(1, file.Removed);
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
    public void Parse_IgnoresTheNoNewlineNote()
    {
        var diff = "diff --git a/x b/x\n--- a/x\n+++ b/x\n@@ -1 +1 @@\n-one\n\\ No newline at end of file\n+two\n";

        var file = DiffParser.Parse(diff)[0];

        Assert.Equal((1, 1), (file.Added, file.Removed));
        Assert.DoesNotContain(file.Rows, r => r.Text.StartsWith(" No newline", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_KeepsEmptyContextLines()
    {
        // git writes a bare empty line for an empty context line; dropping it would shift every number after it.
        var diff = "diff --git a/x b/x\n--- a/x\n+++ b/x\n@@ -1,3 +1,3 @@\n one\n\n-two\n+TWO\n";

        var rows = DiffParser.Parse(diff)[0].Rows;

        Assert.Equal(DiffLineKind.Context, rows[2].Kind);
        Assert.Equal(string.Empty, rows[2].Text);
        Assert.Equal(3, rows[3].OldLine);
    }

    [Fact]
    public void Parse_ReturnsNothingForEmptyOrHeaderlessInput()
    {
        Assert.Empty(DiffParser.Parse(string.Empty));
        Assert.Empty(DiffParser.Parse("not a diff at all\njust text\n"));
    }

    [Theory]
    [InlineData("@@ -16,31 +18,46 @@ public class X", 16, 18)]
    [InlineData("@@ -1 +1 @@", 1, 1)]
    [InlineData("@@ -0,0 +1,12 @@", 0, 1)]
    public void ParseHunkStart_ReadsBothSides(string header, int expectedOld, int expectedNew)
    {
        Assert.Equal((expectedOld, expectedNew), DiffParser.ParseHunkStart(header));
    }

    [Fact]
    public void SplitHunkHeader_SeparatesTheRangeFromTheEnclosingDeclaration()
    {
        Assert.Equal(("@@ -16,31 +18,46 @@", "public class X : Y"), DiffParser.SplitHunkHeader("@@ -16,31 +18,46 @@ public class X : Y"));
        Assert.Equal(("@@ -1,2 +1,2 @@", string.Empty), DiffParser.SplitHunkHeader("@@ -1,2 +1,2 @@"));
    }

    [Fact]
    public void WordSpan_PointsAtOnlyWhatDiffers()
    {
        var (start, oldEnd, newEnd) = DiffParser.WordSpan("Runs on .NET 9.", "Runs on .NET 10.");

        Assert.Equal("9", "Runs on .NET 9."[start..oldEnd]);
        Assert.Equal("10", "Runs on .NET 10."[start..newEnd]);
    }

    [Fact]
    public void WordSpan_HandlesAPureInsertionAtTheStart()
    {
        var (start, oldEnd, newEnd) = DiffParser.WordSpan("using System;", "﻿using System;");

        Assert.Equal(0, start);
        Assert.Equal(0, oldEnd);            // nothing was removed
        Assert.Equal("﻿", "﻿using System;"[start..newEnd]);
    }

    [Fact]
    public void WordSpan_IsEmptyWhenTheLinesAreEqual()
    {
        // Equal lines run the prefix walk to the end; what the panel checks is that the span covers nothing, which
        // is what stops it drawing a highlight over a line that did not change (a whitespace-only edit, say).
        var (start, oldEnd, newEnd) = DiffParser.WordSpan("same", "same");

        Assert.Equal(start, oldEnd);
        Assert.Equal(start, newEnd);
    }

    [Fact]
    public void Parse_DoesNotTurnTheFinalNewlineIntoABlankRow()
    {
        // Every real git diff ends with a newline. Counting the empty string after it as a context line put a blank
        // row under the last file of every review, numbered one past the end of the file.
        var rows = DiffParser.Parse("diff --git a/x b/x\n--- a/x\n+++ b/x\n@@ -1 +1 @@\n+one\n")[0].Rows;

        Assert.Equal(DiffLineKind.Added, rows[^1].Kind);
    }

    [Fact]
    public void WordSpan_NeverCutsASurrogatePairInHalf()
    {
        // 🎉 and 🎊 share a high surrogate, so the shared prefix ends *inside* the pair. Slicing there would put half
        // a character outside the highlight and render a replacement glyph on both sides of the cut.
        var (start, _, _) = DiffParser.WordSpan("🎉x", "🎊x");

        Assert.Equal(0, start);
        Assert.False(char.IsLowSurrogate("🎉x"[start]));
    }

    [Fact]
    public void IsIsolatedReplacement_OnlyForALoneMinusPlusPair()
    {
        var pair = DiffParser.Parse(TwoFiles)[1].Rows;              // context, minus, plus
        Assert.True(DiffParser.IsIsolatedReplacement(pair, 2));

        // A block of removals followed by a block of additions: the lines do not correspond one to one.
        var block = DiffParser.Parse("diff --git a/x b/x\n--- a/x\n+++ b/x\n@@ -1,4 +1,4 @@\n-a\n-b\n+c\n+d\n")[0].Rows;
        Assert.False(DiffParser.IsIsolatedReplacement(block, 1));
        Assert.False(DiffParser.IsIsolatedReplacement(block, 2));
    }
}
