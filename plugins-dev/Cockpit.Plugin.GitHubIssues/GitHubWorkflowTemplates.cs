using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// The flows this plugin knows how to draw (#69). Its steps are the pieces — a trigger that hands over an issue, a
/// step that starts one, one that comments — and "pick an issue, cut the branch, start it, put the agent on it" is
/// what they are for. Offered ready-made, because an empty canvas asks the operator to work out something the plugin
/// already knows.
/// </summary>
internal static class GitHubWorkflowTemplates
{
    public static IEnumerable<WorkflowTemplate> All =>
    [
        new(
            Id: "github.issue-to-agent",
            Name: "Issue → branch → agent",
            Description: "When you pick an issue for a session: cut its branch, mark the issue started, and put the agent to work on it.",
            Json: IssueToAgent),
    ];

    private const string IssueToAgent = """
    {
      "Id": "github-issue-to-agent",
      "Name": "Issue → branch → agent",
      "IsActive": false,
      "Nodes": [
        {
          "Id": "g1",
          "TypeId": "github.picked",
          "Name": "Issue picked for a session",
          "X": 80,
          "Y": 160
        },
        {
          "Id": "g2",
          "TypeId": "cockpit.command",
          "Name": "Cut the branch",
          "X": 360,
          "Y": 160,
          "Parameters": {
            "Command": "git switch -c {branch}",
            "Working directory": "{directory}"
          }
        },
        {
          "Id": "g3",
          "TypeId": "github.start",
          "Name": "Mark it started",
          "X": 640,
          "Y": 160,
          "Parameters": {
            "Issue": "{issue}",
            "Repository": "{repository}"
          }
        },
        {
          "Id": "g4",
          "TypeId": "cockpit.inject",
          "Name": "Put the agent on it",
          "X": 920,
          "Y": 160,
          "Parameters": {
            "Text": "Work on issue #{issue}: {title}. The branch {branch} is cut and checked out. {url}"
          }
        }
      ],
      "Connections": [
        { "FromNodeId": "g1", "FromOutput": 0, "ToNodeId": "g2" },
        { "FromNodeId": "g2", "FromOutput": 0, "ToNodeId": "g3" },
        { "FromNodeId": "g3", "FromOutput": 0, "ToNodeId": "g4" }
      ]
    }
    """;
}
