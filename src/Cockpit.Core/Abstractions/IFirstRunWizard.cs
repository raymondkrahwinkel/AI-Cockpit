namespace Cockpit.Core.Abstractions;

/// <summary>
/// Shows the first-run wizard on demand (AC-508).
/// </summary>
/// <remarks>
/// The wizard runs once on a fresh install, but the screen that explains what this is has to be reachable again
/// afterwards — a one-off screen whose content is gone after a single click is content that was never delivered
/// (AC-509 criterion 3). The Help menu is where the operator goes looking for it (AC-512), and that menu lives in
/// the main view while the wizard owns its own window and its own completion flag. This interface is the seam
/// between the two so that neither has to know how the other is built.
/// </remarks>
public interface IFirstRunWizard
{
    /// <summary>
    /// Opens the wizard and returns when the operator has finished or dismissed it. Running it again does not
    /// undo anything that an earlier run installed; it starts the same steps over with the current state.
    /// </summary>
    Task ShowAsync(CancellationToken cancellationToken = default);
}
