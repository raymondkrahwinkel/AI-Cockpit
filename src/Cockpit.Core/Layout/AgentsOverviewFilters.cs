namespace Cockpit.Core.Layout;

public sealed record AgentsOverviewFilters(
    bool WaitingOnly = false,
    bool DelegatedTasks = false,
    string Search = "",
    string Machine = "All machines",
    string Project = "All projects");
