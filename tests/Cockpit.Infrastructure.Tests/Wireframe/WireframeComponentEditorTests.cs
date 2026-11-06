using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Infrastructure.Wireframe;

namespace Cockpit.Infrastructure.Tests.Wireframe;

/// <summary>
/// The per-component line surgery behind cockpit-wireframe (AC-872): one component named by its stable id, the rest
/// of the source left exactly as it was, and every change gated on the result still being readable.
/// </summary>
public class WireframeComponentEditorTests
{



    [Fact]
    public void SetText_ChangesTheTextKeepsEveryModifier_AndLeavesEveryOtherLineExactlyAsItWas()
    {
        var before = WireframeScreens.LinesOf(WireframeScreens.Settings);

        var result = WireframeComponentEditor.Apply(
            WireframeScreens.Settings,
            WireframeComponentEdit.SetText(WireframeScreens.SaveButton, "Bewaren"));

        Assert.Null(result.Refusal);
        Assert.NotNull(result.Text);
        Assert.Equal("        button \"Bewaren\" primary #save", WireframeScreens.LineOf(result.Text, WireframeScreens.SaveButtonLine));

        // The two halves around the one line that was allowed to change, compared whole — a shorter or a shifted
        // document fails on length before it fails on content.
        var after = WireframeScreens.LinesOf(result.Text);
        var at = WireframeScreens.SaveButtonLine - 1;
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(before[..at], after[..at]);
        Assert.Equal(before[(at + 1)..], after[(at + 1)..]);
    }






    // ---- Notes (AC-907) ----






    // ---- A document of several screens (AC-901) ----


    // ---- Flows between screens (AC-902) ----

    [Fact]
    public void SetText_OnAScreen_CarriesEveryGotoThatPointedAtTheOldTitle_ToTheNewOne()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.TwoScreensWithFlow,
            WireframeComponentEdit.SetText(WireframeScreens.SignupScreen, "Account aanmaken"));

        Assert.Null(result.Refusal);
        Assert.Contains("screen \"Account aanmaken\" #signup", result.Text, StringComparison.Ordinal);
        Assert.Contains("goto:\"Account aanmaken\"", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("goto:\"Registreren\"", result.Text, StringComparison.Ordinal);
        Assert.Contains("1 flow", result.Summary);
    }

    [Fact]
    public void Remove_OfAScreenAGotoStillPointsAt_IsRefused_NamingTheScreenAndTheReferrer()
    {
        var result = WireframeComponentEditor.Apply(
            WireframeScreens.TwoScreensWithFlow,
            WireframeComponentEdit.Remove(WireframeScreens.SignupScreen));

        Assert.Null(result.Text);
        Assert.Contains("screen \"Registreren\"", result.Refusal);
        Assert.Contains("button \"Aanmelden\"", result.Refusal);
    }


    // ---- Viewport (AC-915) ----



    // ---- States (AC-914) ----



}
