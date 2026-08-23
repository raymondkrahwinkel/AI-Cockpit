namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// One choice offered for a <see cref="ProjectFieldRegistration"/> (AC-317): what gets stored on the project,
/// and what the operator reads while picking it.
/// </summary>
/// <remarks>
/// The two are separate because they answer to different readers — e.g. a YouTrack project is picked as
/// <c>"AI-Cockpit — AC"</c> and stored as <c>AC</c>.
/// </remarks>
/// <param name="Value">
/// What is stored on the project and handed back to the plugin — the identifier it resolves.
/// </param>
/// <param name="Display">
/// What the operator sees in the list and in the box once it is picked.
/// </param>
public sealed record ProjectFieldOption(string Value, string Display);
