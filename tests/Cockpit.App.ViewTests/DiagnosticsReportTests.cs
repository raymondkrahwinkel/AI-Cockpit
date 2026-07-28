using Cockpit.App.ViewModels;
using Cockpit.Core.Configuration;

namespace Cockpit.App.ViewTests;

/// <summary>
/// What the diagnostics report has to name. Its own file rather than another case in
/// <see cref="DiagnosticsCollectorTests"/>: that one is about assembling the snapshot, this is about what the
/// operator can read off the text — and it is written against xunit's own asserts rather than the assertion library
/// the older file still carries.
/// </summary>
public class DiagnosticsReportTests
{
    [Fact]
    public void TheReport_NamesTheLogTheCockpitTellsTheOperatorToLookAt()
    {
        var diagnostics = new DiagnosticsViewModel(collector: null, sessions: () => []);

        diagnostics.Refresh();

        // Several messages send the operator to "the log" — a failed MCP sign-in, a hotkey that could not be
        // registered, a screenshot shortcut that did not take. None of them says where it is, and nothing else in
        // the cockpit did either: the path was assembled at startup and never surfaced. A referral to a file the UI
        // never names is barely a referral, so the one panel built to be read and copied carries it.
        Assert.Contains(CockpitBuild.LogPath, diagnostics.Report, StringComparison.Ordinal);
    }
}
