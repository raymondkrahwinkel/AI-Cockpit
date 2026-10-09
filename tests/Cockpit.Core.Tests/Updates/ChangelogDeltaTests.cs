using Cockpit.Core.Updates;

namespace Cockpit.Core.Tests.Updates;

/// <summary>
/// "What's new" (AC-1515) compares two copies of CHANGELOG.md written by two different builds — a format crossing a
/// version boundary, read by a build that predates the newer copy. Entries known to the running build stay out,
/// entries the offered build added come in, and a re-wrapped old entry does not pass for a new one.
/// </summary>
public class ChangelogDeltaTests
{
    [Fact]
    public void OnlyEntriesAfterTheCurrentBuild_UpToTheOfferedOne_AreListed()
    {
        const string current = """
            # Changelog

            ## [Unreleased]

            ### Added

            - added: shipped in 188, wrapped over
              two lines

            ### Fixed

            - fixed: shipped in 187

            ## [0.89.0] - 2026-09-01

            ### Added

            - added: released long ago
            """;

        const string offered = """
            # Changelog

            ## [Unreleased]

            ### Added

            - added: new in 190
            - added: shipped in 188, wrapped over two lines

            ### Fixed

            - fixed: new in 189, with a
              continuation line
            - fixed: shipped in 187

            ## [0.89.0] - 2026-09-01

            ### Added

            - added: released long ago
            """;

        Assert.Equal(
            "### Added\n\n- added: new in 190\n\n### Fixed\n\n- fixed: new in 189, with a\n  continuation line",
            ChangelogDelta.Between(current, offered).ReplaceLineEndings("\n"));
    }
}
