using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

// AC-491: a job together with the project offering it. A button hands its command one parameter, and starting a
// job needs both — the project for the folder, profile and servers, the job for the prompt.
public sealed record ProjectJobChoice(Project Project, ProjectJob Job);
