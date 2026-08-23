namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// Optional interface a plugin's settings view (the control passed to <see cref="ICockpitHost.AddSettings"/>)
/// can implement to have its settings split into named sections instead of one long scroll (AC-316): the host
/// draws the left navigation rail and asks the view to show a section when the operator picks one.
/// </summary>
/// <remarks>
/// The view stays the one control the host renders; only <see cref="ShowSection"/> is called. The rail appears
/// only from two sections up. <strong>Set <c>minHostVersion</c> to <c>0.7.0</c> in your manifest when you
/// implement this</strong> — on a host that predates this interface your settings control cannot be loaded at
/// all.
/// </remarks>
public interface IPluginSettingsSections
{
    /// <summary>
    /// The section names, in the order they appear in the rail. Read when the dialog opens and expected to stay
    /// as they were for as long as it is open. Fewer than two leaves the dialog flat.
    /// </summary>
    IReadOnlyList<string> SectionTitles { get; }

    /// <summary>
    /// Shows the section at <paramref name="index"/> in <see cref="SectionTitles"/> — typically by swapping the
    /// view's own content. The host shows section 0 when the dialog opens and calls this again on every rail
    /// selection, so make it idempotent: a control that also picks its own opening section in its constructor
    /// (the usual way to be something on a host that draws no rail) is asked for that same section twice.
    /// </summary>
    void ShowSection(int index);
}
