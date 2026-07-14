using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The flows this plugin knows how to draw (#69). Its steps are only half the offer: a trigger that hands over a
/// ticket and a step that moves one are the pieces, and "pick a ticket, cut the branch, put an agent on it, move it to
/// in progress" is what they are for. An operator staring at an empty canvas has to work that out; here it is, ready
/// to open and read.
/// <para>
/// Written as the workflows plugin's own JSON — the same text a flow is exported to — so a template that ships with a
/// plugin, one shared as a file, and one you drew yourself are the same kind of thing. The ids inside are rewritten on
/// use, so a template can be started from twice.
/// </para>
/// </summary>
internal static class YouTrackWorkflowTemplates
{
    public static IEnumerable<WorkflowTemplate> All =>
    [
        new(
            Id: "youtrack.ticket-to-agent",
            Name: "Ticket → branch → agent",
            Description: "When you pick a ticket for a session: cut its branch, move the ticket to in progress, and put the agent to work on it.",
            Json: TicketToAgent),

        new(
            Id: "youtrack.review-to-discord",
            Name: "Ticket reaches Review → tell me",
            Description: "When you move a ticket to Review, say so on Discord — the handover nobody remembers to make.",
            Json: ReviewToDiscord),
    ];

    // A flow, as the workflows plugin writes one. Laid out left to right, because that is how it reads.
    private const string TicketToAgent = """
    {
      "Id": "youtrack-ticket-to-agent",
      "Name": "Ticket → branch → agent",
      "IsActive": false,
      "Nodes": [
        {
          "Id": "t1",
          "TypeId": "youtrack.picked",
          "Name": "Ticket picked for a session",
          "X": 80,
          "Y": 160
        },
        {
          "Id": "t2",
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
          "Id": "t3",
          "TypeId": "youtrack.status",
          "Name": "Move it to in progress",
          "X": 640,
          "Y": 160,
          "Parameters": {
            "Ticket": "{ticket}",
            "Status": "",
            "Assign to me": "yes"
          }
        },
        {
          "Id": "t4",
          "TypeId": "cockpit.inject",
          "Name": "Put the agent on it",
          "X": 920,
          "Y": 160,
          "Parameters": {
            "Text": "Work on {ticket}: {summary}. The branch {branch} is cut and checked out."
          }
        }
      ],
      "Connections": [
        { "FromNodeId": "t1", "FromOutput": 0, "ToNodeId": "t2" },
        { "FromNodeId": "t2", "FromOutput": 0, "ToNodeId": "t3" },
        { "FromNodeId": "t3", "FromOutput": 0, "ToNodeId": "t4" }
      ]
    }
    """;

    private const string ReviewToDiscord = """
    {
      "Id": "youtrack-review-to-discord",
      "Name": "Ticket reaches Review → tell me",
      "IsActive": false,
      "Nodes": [
        {
          "Id": "r1",
          "TypeId": "youtrack.status-changed",
          "Name": "Ticket status changed",
          "X": 80,
          "Y": 160
        },
        {
          "Id": "r2",
          "TypeId": "cockpit.if",
          "Name": "Did it reach Review?",
          "X": 360,
          "Y": 160,
          "Parameters": {
            "Condition": "state === 'Review'"
          }
        },
        {
          "Id": "r3",
          "TypeId": "cockpit.discord",
          "Name": "Say so on Discord",
          "X": 640,
          "Y": 100,
          "Parameters": {
            "Message": "{ticket} is ready for review: {summary}"
          }
        }
      ],
      "Connections": [
        { "FromNodeId": "r1", "FromOutput": 0, "ToNodeId": "r2" },
        { "FromNodeId": "r2", "FromOutput": 0, "ToNodeId": "r3" }
      ]
    }
    """;
}
