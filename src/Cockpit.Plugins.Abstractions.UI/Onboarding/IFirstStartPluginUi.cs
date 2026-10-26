namespace Cockpit.Plugins.Abstractions.UI.Onboarding;

// AC-1509: a UI part whose first-start routes exist from the very first start, before the plugins' own initialization.
public interface IFirstStartPluginUi
{
    /// <summary>
    /// The routes this plugin offers on the first-run wizard's "How do you want to start?" step. Called once per start,
    /// on the UI thread, before the wizard opens and before <see cref="ICockpitPluginUi.InitializeUi"/>; the routes stay
    /// offered for "Run setup again" too. A route's content can only reach what <paramref name="host"/> offers.
    /// </summary>
    IReadOnlyList<FirstStartRoute> CreateFirstStartRoutes(IFirstStartHost host);
}
