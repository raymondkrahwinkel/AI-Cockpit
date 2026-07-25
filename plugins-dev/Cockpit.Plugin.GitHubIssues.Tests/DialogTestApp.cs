using Avalonia;
using Avalonia.Themes.Fluent;

namespace Cockpit.Plugin.GitHubIssues.Tests;

/// <summary>
/// The application the headless platform runs the dialog under. The plugin's controls are built in code and carry no
/// styles of their own — the cockpit supplies the base theme, so a test host that leaves it out gets untemplated
/// controls that measure to nothing.
/// </summary>
public sealed class DialogTestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}
