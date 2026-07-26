namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// Optional interface a plugin's settings view (the control passed to <see cref="ICockpitHost.AddSettings"/>)
/// can implement to have its settings split into named sections instead of one long scroll (AC-316): the host
/// draws the same left navigation rail the Options dialog uses, and asks the view to show a section when the
/// operator picks one.
/// <para>
/// The view stays the one control the host renders — it is not replaced or taken apart. Only
/// <see cref="ShowSection"/> is called, and swapping its own content is the view's business, so everything a
/// settings view already relies on (its attach/detach lifetime, the fields it saves from) is untouched. Save
/// stays one shared footer over all sections: a section is a page of the same form, not a form of its own.
/// </para>
/// <para>
/// The rail appears only from two sections up — a rail beside a single page costs width and navigates nothing —
/// and a view that does not implement this gets exactly the dialog it has today.
/// </para>
/// <para>
/// <strong>Set <c>minHostVersion</c> to <c>0.7.0</c> in your manifest when you implement this.</strong> Your plugin
/// does not ship <c>Cockpit.Plugins.Abstractions</c> — it binds to the host's own copy — so on a host that predates
/// this interface your settings control cannot be loaded at all, and the gear that opens it does nothing at all.
/// The version gate is what keeps the plugin off those hosts.
/// </para>
/// </summary>
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
