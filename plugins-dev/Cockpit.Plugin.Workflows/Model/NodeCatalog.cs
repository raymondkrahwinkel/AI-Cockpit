using Material.Icons;

namespace Cockpit.Plugin.Workflows.Model;

// The node types the cockpit ships (#69). Deliberately cockpit-shaped rather than a general automation kit: the
// value here is in what only this app can do — start sessions, delegate work, watch what an agent says, put a
// ticket in progress. Anything that talks to a hundred SaaS products already exists, and the operator runs it elsewhere.
//
// The built-in half of the list. The other half comes from plugins (`ICockpitHost.AddWorkflowStep`) and is
// handed to `Contribute` once, at startup: YouTrack knows how to move a ticket, and this plugin should
// never have to.
public static class NodeCatalog
{
    private static IReadOnlyList<NodeTypeDescriptor> _contributed = [];

    // Every step the picker offers: the cockpit's own, then whatever plugins added.
    public static IReadOnlyList<NodeTypeDescriptor> All => [.. BuiltIn, .. _contributed];

    // The steps plugins contributed, in the order they registered. Called once, when the plugin starts.
    public static void Contribute(IReadOnlyList<NodeTypeDescriptor> types) => _contributed = types;

    public static IReadOnlyList<NodeTypeDescriptor> BuiltIn { get; } =
    [
        new(
            "cockpit.text-match",
            "Text appears",
            "A session's output contains something you are watching for. Best effort: a model's wording is not a contract.",
            "",
            NodeCategory.Trigger,
            WorkflowNodeKind.Trigger,
            [""],
            ["Pattern"],
            new Dictionary<string, string> { ["match"] = "All tests passed", ["session"] = "webshop" })
        {
            IconKind = MaterialIconKind.Eye,
        },
        new(
            "cockpit.schedule",
            "Schedule",
            "Every day at a time you pick, on a weekday, once, or on an interval. \"mon 09:00\", \"mon,fri 09:00\", "
                + "\"once 2026-10-01 09:00\" or \"every 15m\". Time zone is an IANA id (\"Europe/Amsterdam\") — leave "
                + "it blank to use this cockpit's own.",
            "",
            NodeCategory.Trigger,
            WorkflowNodeKind.Trigger,
            [""],
            ["When", "Time zone"],
            new Dictionary<string, string> { ["at"] = "2026-07-13T09:00:00+02:00" })
        {
            IconKind = MaterialIconKind.ClockOutline,
        },
        new(
            "cockpit.manual",
            "Run manually",
            "You start it, from here or from a shortcut.",
            "",
            NodeCategory.Trigger,
            WorkflowNodeKind.Trigger,
            [""],
            [],
            new Dictionary<string, string> { ["startedBy"] = "you" })
        {
            IconKind = MaterialIconKind.Play,
        },

        new(
            "cockpit.notify",
            "Notify",
            "A toast in the cockpit.",
            "",
            NodeCategory.Notify,
            WorkflowNodeKind.Action,
            [""],
            ["Message"])
        {
            IconKind = MaterialIconKind.Bell,
        },

        new(
            "cockpit.slack",
            "Send to Slack",
            "Post a message to a Slack channel through its incoming webhook. Notify tells you; this tells everyone else.",
            "",
            NodeCategory.Notify,
            WorkflowNodeKind.Action,
            [""],
            ["Message", "Webhook URL"],
            new Dictionary<string, string> { ["message"] = "Deployed WEB-14 to staging" })
        {
            IconKind = MaterialIconKind.ChatOutline,
        },
        new(
            "cockpit.discord",
            "Send to Discord",
            "Post a message to a Discord channel through its webhook. Anything past 2000 characters is cut, because Discord refuses the rest outright.",
            "",
            NodeCategory.Notify,
            WorkflowNodeKind.Action,
            [""],
            ["Message", "Webhook URL"],
            new Dictionary<string, string> { ["message"] = "Deployed WEB-14 to staging" })
        {
            IconKind = MaterialIconKind.Gamepad,
        },

        new(
            "cockpit.inject",
            "Send to session",
            "Put text into the prompt of the session you name — its name, its pane id, or {Start session.session} after a step that started one. It is placed, not sent: the session's input holds it as if you typed it.",
            "",
            NodeCategory.Sessions,
            WorkflowNodeKind.Action,
            [""],
            ["Session", "Text"])
        {
            IconKind = MaterialIconKind.Keyboard,
        },
        new(
            "cockpit.start-session",
            "Start session",
            "Open a session on a profile and hand it a prompt. Name it here and it opens under that name — a flow starting a session on a ticket need not open \"Claude — 14:22\" and rename it a step later; leave the name empty and the profile and the clock decide.",
            "",
            NodeCategory.Sessions,
            WorkflowNodeKind.Action,
            [""],
            ["Profile", "Session name", "Prompt", "Working directory"],
            new Dictionary<string, string> { ["session"] = "webshop" })
        {
            IconKind = MaterialIconKind.Rocket,
        },

        new(
            "cockpit.set-status",
            "Set session status",
            "Set the statusline under the name of the session you name — what it is working on, like a ticket number — and optionally rename it. Pair it with Start session and name {Start session.session} to label a session after the ticket it just picked up; an empty status clears the line.",
            "",
            NodeCategory.Sessions,
            WorkflowNodeKind.Action,
            [""],
            ["Session", "Status", "Name"])
        {
            IconKind = MaterialIconKind.Tag,
        },

        new(
            "cockpit.delegate",
            "Delegate",
            "Hand the work to another profile as a background task, and wait for what it produces. It runs where you can see it, in the delegated tasks view. It may only read unless you set Permission — 'acceptEdits' to let it change files, 'bypassPermissions' to also let it run commands.",
            "",
            NodeCategory.Sessions,
            WorkflowNodeKind.Action,
            [""],
            ["Profile", "Prompt", "Working directory", "Permission"],
            new Dictionary<string, string> { ["result"] = "Done — 3 files changed", ["profile"] = "reviewer" })
        {
            IconKind = MaterialIconKind.HandshakeOutline,
        },

        new(
            "cockpit.command",
            "Run a command",
            "A shell command in a working directory. What it prints becomes the data the next step gets.",
            "",
            NodeCategory.External,
            WorkflowNodeKind.Action,
            [""],
            ["Command", "Working directory"],
            new Dictionary<string, string> { ["output"] = "M src/Program.cs", ["exitCode"] = "0" })
        {
            IconKind = MaterialIconKind.AppleKeyboardCommand,
        },
        new(
            "cockpit.http",
            "HTTP request",
            "Call something and carry the answer on.",
            "",
            NodeCategory.External,
            WorkflowNodeKind.Action,
            [""],
            ["Method", "URL", "Body"],
            new Dictionary<string, string> { ["status"] = "200", ["body"] = "{\"id\": 42}" })
        {
            IconKind = MaterialIconKind.Web,
        },

        new(
            "cockpit.if",
            "If",
            "Two ways on: one when the condition holds, one when it does not.",
            "",
            NodeCategory.Flow,
            WorkflowNodeKind.Decision,
            ["true", "false"],
            ["Condition"])
        {
            IconKind = MaterialIconKind.SourceBranch,
        },
        // Its ways out are written into it, not fixed here — see WorkflowNode.Outputs. What stands here is what a
        // switch has before anyone has named a case: the way out for a value it does not recognise.
        new(
            SwitchCases.TypeId,
            "Switch",
            "One value, a way out per case. Anything it does not recognise leaves by \"otherwise\".",
            "",
            NodeCategory.Flow,
            WorkflowNodeKind.Decision,
            [SwitchCases.Otherwise],
            [SwitchCases.ValueParameter, SwitchCases.CasesParameter])
        {
            IconKind = MaterialIconKind.SourceFork,
        },
        new(
            "cockpit.approve",
            "Ask me first",
            "Stops and waits for you — on the banner, or as buttons in a chat channel. No answer in time counts as no. For the steps that are not free to undo.",
            "",
            NodeCategory.Flow,
            WorkflowNodeKind.Action,
            [""],
            ["Question", "Wait (minutes)"])
        {
            IconKind = MaterialIconKind.HandBackLeftOutline,
        },
    ];

    public static NodeTypeDescriptor? Find(string typeId) =>
        All.FirstOrDefault(type => string.Equals(type.Id, typeId, StringComparison.Ordinal));

    public static IEnumerable<IGrouping<NodeCategory, NodeTypeDescriptor>> ByCategory() =>
        All.GroupBy(type => type.Category);

    // What the picker shows for a search term — matched on what the operator would type: the name, or what it does.
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
