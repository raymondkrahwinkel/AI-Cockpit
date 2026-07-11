namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// One configured YouTrack instance (#48): a friendly label shown in the issues dialog's instance selector, the
/// REST API base URL, a permanent token, and an optional default project short-name preselected in the
/// dialog's project filter when this instance is picked (falls back to "All" when empty). <see cref="ToString"/>
/// is overridden to show only <see cref="Label"/> — the default record <c>ToString</c> would otherwise leak
/// <see cref="Token"/> into the instance-selector <see cref="Avalonia.Controls.ComboBox"/>'s item display.
/// </summary>
public sealed record YouTrackInstance(string Label, string InstanceUrl, string Token, string DefaultProjectTag)
{
    public override string ToString() => Label;
}
