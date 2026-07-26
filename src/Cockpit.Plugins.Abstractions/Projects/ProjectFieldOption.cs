namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// One choice offered for a <see cref="ProjectFieldRegistration"/> (AC-317): what gets stored on the project, and
/// what the operator reads while picking it.
/// <para>
/// The two are separate because they answer to different readers. A YouTrack project is picked as
/// <c>"AI-Cockpit — AC"</c> and used as <c>AC</c>; a repository is picked by its name and used as
/// <c>owner/repo</c>. Storing the display text would leave the plugin parsing its own label back apart, and
/// showing the value alone would ask the operator to know the tag by heart.
/// </para>
/// </summary>
/// <param name="Value">What is stored on the project and handed back to the plugin — the identifier it resolves.</param>
/// <param name="Display">What the operator sees in the list and in the box once it is picked.</param>
public sealed record ProjectFieldOption(string Value, string Display);
