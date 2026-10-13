using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

// AC-1528: a project on Home, with the conversation its stand line opens — null while none of its conversations runs.
public sealed record SimpleHomeProjectCard(Project Project, SessionPanelViewModel? Running, string? Stand, bool IsWaiting);
