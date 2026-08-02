using Avalonia.Controls;
using Cockpit.App.Views.Onboarding;

namespace Cockpit.App.ViewTests.Onboarding;

/// <summary>A step with no real content, for tests that only care about the shell's own navigation and window wiring (AC-509).</summary>
internal sealed class StubFirstRunWizardStep(int order, string title, bool isSkipped) : IFirstRunWizardStep
{
    public int Order => order;

    public string Title => title;

    public bool IsSkipped => isSkipped;

    public Control BuildContent() => new Border();
}
