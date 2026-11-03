using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;

namespace Cockpit.App.ViewTests.Onboarding;

// AC-511: a batch that says less than the per-plugin dialog is a weakened guard, so the two screens are rendered and compared.
[Collection("avalonia")]
public class WorkKindConsentParityTests
{
    [Fact]
    public void TheStandingTerms_ReadTheSameOnBothScreens() => HeadlessAvalonia.Run(() =>
    {
        var dialog = _Texts("plugin-consent");
        var batch = _Texts("first-run-work-kind");

        Assert.Contains(PluginConsentTerms.PermissionsNotice, dialog, StringComparer.Ordinal);
        Assert.Contains(PluginConsentTerms.PermissionsNotice, batch, StringComparer.Ordinal);
    });

    private static IReadOnlyList<string> _Texts(string scene)
    {
        var window = Screenshotter.ShowScene(scene);
        try
        {
            window.UpdateLayout();

            return [.. window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty)];
        }
        finally
        {
            window.Close();
        }
    }
}
