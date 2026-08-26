using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

// What a Codex session can run out of (#1105): the context window, and the account's one rolling rate-limit
// window — seven days, no credits as a fallback. WindowLabel is the one source both the driver and this
// declaration call, so the two can never spell a window differently (the shape ClaudeUsageSignals uses too).
public static class CodexUsageSignals
{
    public const string ContextKey = "context";

    // The account-wide weekly allowance.
    public const string WeeklyKey = "weekly";

    private const string ResumePrompt = "continue";

    // The weekly window's span in minutes (7 days) — verified against a live account/rateLimits/updated
    // notification (#1105 research). Declared here, not just derived on arrival, so WeeklyKey's Label below is
    // computed the same way the driver computes it for an actually-reported window.
    public const int WeeklyWindowMinutes = 10080;

    // Context worth mentioning at half full (Claude's default); the weekly allowance at 75%, lower than
    // Claude's 90% — a seven-day window with no credit fallback has to warn early enough to spread the
    // remaining budget, not just to finish the current task (#1105 decision 2, Raymond).
    public static IReadOnlyList<PluginUsageSignal> Declarations { get; } =
    [
        new(ContextKey, "ctx", PluginUsageSignalKind.Fill, DefaultThresholdPercent: 50)
        {
            Description = "Context window",
        },
        new(WeeklyKey, WindowLabel(WeeklyWindowMinutes), PluginUsageSignalKind.Allowance, DefaultThresholdPercent: 75)
        {
            Description = "Week (7 days)",
            SupportsResume = true,
            DefaultResumePrompt = ResumePrompt,
        },
    ];

    // The provider owns the header label (#45 D7), derived from the window's span: "5h" for five hours, "7d"
    // for weekly. Called from both this declaration and CodexAppServerSessionDriver's own parsing, so the two
    // can never drift apart (#1105 criterion 6) the way a hardcoded label in each place could.
    public static string WindowLabel(int? windowMinutes) => windowMinutes switch
    {
        null => "rate",
        < 60 => $"{windowMinutes}m",
        < 1440 => $"{windowMinutes / 60}h",
        _ => $"{windowMinutes / 1440}d",
    };
}
