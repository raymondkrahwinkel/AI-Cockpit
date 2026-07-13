namespace Cockpit.Plugin.Workflows.Model;

/// <summary>
/// The node types the cockpit ships (#69). Deliberately cockpit-shaped rather than a general automation kit: the
/// value here is in what only this app can do — start sessions, delegate work, watch what an agent says, put a
/// ticket in progress. Anything that talks to a hundred SaaS products already exists, and Raymond runs it.
/// <para>
/// The built-in half of the list. The other half comes from plugins (<c>ICockpitHost.AddWorkflowStep</c>) and is
/// handed to <see cref="Contribute"/> once, at startup: YouTrack knows how to move a ticket, and this plugin should
/// never have to.
/// </para>
/// </summary>
public static class NodeCatalog
{
    private static IReadOnlyList<NodeTypeDescriptor> _contributed = [];

    /// <summary>Every step the picker offers: the cockpit's own, then whatever plugins added.</summary>
    public static IReadOnlyList<NodeTypeDescriptor> All => [.. BuiltIn, .. _contributed];

    /// <summary>The steps plugins contributed, in the order they registered. Called once, when the plugin starts.</summary>
    public static void Contribute(IReadOnlyList<NodeTypeDescriptor> types) => _contributed = types;

    public static IReadOnlyList<NodeTypeDescriptor> BuiltIn { get; } =
    [
        new(
            "cockpit.text-match",
            "Text appears",
            "A session's output contains something you are watching for. Best effort: a model's wording is not a contract.",
            "👁",
            NodeCategory.Trigger,
            WorkflowNodeKind.Trigger,
            [""],
            ["Pattern"],
            new Dictionary<string, string> { ["match"] = "All tests passed", ["session"] = "Eveworkbench" }),
        new(
            "cockpit.schedule",
            "Schedule",
            "Every day at a time you pick, or on an interval.",
            "🕐",
            NodeCategory.Trigger,
            WorkflowNodeKind.Trigger,
            [""],
            ["When"],
            new Dictionary<string, string> { ["at"] = "2026-07-13T09:00:00+02:00" }),
        new(
            "cockpit.manual",
            "Run manually",
            "You start it, from here or from a shortcut.",
            "▶",
            NodeCategory.Trigger,
            WorkflowNodeKind.Trigger,
            [""],
            [],
            new Dictionary<string, string> { ["startedBy"] = "you" }),

        new(
            "cockpit.notify",
            "Notify",
            "A toast in the cockpit.",
            "🔔",
            NodeCategory.Notify,
            WorkflowNodeKind.Action,
            [""],
            ["Message"]),

        new(
            "cockpit.slack",
            "Send to Slack",
            "Post a message to a Slack channel through its incoming webhook. Notify tells you; this tells everyone else.",
            "💬",
            NodeCategory.Notify,
            WorkflowNodeKind.Action,
            [""],
            ["Message", "Webhook URL"],
            new Dictionary<string, string> { ["message"] = "Deployed EVE-14 to staging" }),
        new(
            "cockpit.discord",
            "Send to Discord",
            "Post a message to a Discord channel through its webhook. Anything past 2000 characters is cut, because Discord refuses the rest outright.",
            "🎮",
            NodeCategory.Notify,
            WorkflowNodeKind.Action,
            [""],
            ["Message", "Webhook URL"],
            new Dictionary<string, string> { ["message"] = "Deployed EVE-14 to staging" }),

        new(
            "cockpit.inject",
            "Send to session",
            "Put text into a session's prompt — the agent picks it up as if you typed it.",
            "⌨",
            NodeCategory.Sessions,
            WorkflowNodeKind.Action,
            [""],
            ["Text"]),
        new(
            "cockpit.start-session",
            "Start session",
            "Open a session on a profile and hand it a prompt.",
            "🚀",
            NodeCategory.Sessions,
            WorkflowNodeKind.Action,
            [""],
            ["Profile", "Prompt", "Working directory"],
            new Dictionary<string, string> { ["session"] = "Eveworkbench" }),

        new(
            "cockpit.delegate",
            "Delegate",
            "Hand the work to another profile as a background task, and wait for what it produces. It runs where you can see it, in the delegated tasks view.",
            "🤝",
            NodeCategory.Sessions,
            WorkflowNodeKind.Action,
            [""],
            ["Profile", "Prompt", "Working directory"],
            new Dictionary<string, string> { ["result"] = "Done — 3 files changed", ["profile"] = "reviewer" }),

        new(
            "cockpit.command",
            "Run a command",
            "A shell command in a working directory. What it prints becomes the data the next step gets.",
            "⌘",
            NodeCategory.External,
            WorkflowNodeKind.Action,
            [""],
            ["Command", "Working directory"],
            new Dictionary<string, string> { ["output"] = "M src/Program.cs", ["exitCode"] = "0" }),
        new(
            "cockpit.http",
            "HTTP request",
            "Call something and carry the answer on.",
            "🌐",
            NodeCategory.External,
            WorkflowNodeKind.Action,
            [""],
            ["Method", "URL", "Body"],
            new Dictionary<string, string> { ["status"] = "200", ["body"] = "{\"id\": 42}" }),

        new(
            "cockpit.if",
            "If",
            "Two ways on: one when the condition holds, one when it does not.",
            "⑂",
            NodeCategory.Flow,
            WorkflowNodeKind.Decision,
            ["true", "false"],
            ["Condition"]),
        new(
            "cockpit.approve",
            "Ask me first",
            "Stops and waits for you. For the steps that are not free to undo.",
            "✋",
            NodeCategory.Flow,
            WorkflowNodeKind.Action,
            [""],
            ["Question"]),
    ];

    public static NodeTypeDescriptor? Find(string typeId) =>
        All.FirstOrDefault(type => string.Equals(type.Id, typeId, StringComparison.Ordinal));

    public static IEnumerable<IGrouping<NodeCategory, NodeTypeDescriptor>> ByCategory() =>
        All.GroupBy(type => type.Category);

    /// <summary>What the picker shows for a search term — matched on what the operator would type: the name, or what it does.</summary>
    public static IReadOnlyList<NodeTypeDescriptor> Search(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return All;
        }

        return All
            .Where(type =>
                type.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || type.Description.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
