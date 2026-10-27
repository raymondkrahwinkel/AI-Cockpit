namespace Cockpit.App.ViewModels;

// AC-1601: one line of the detail pane's "Running now" — the session itself rides along so "Open" can hand it to the cockpit.
public sealed record ProjectRunningSession(SessionPanelViewModel Session, string Title, string Detail)
{
    public static ProjectRunningSession From(SessionPanelViewModel session, DateTimeOffset now)
    {
        var elapsed = now - session.StartedAt;
        var age = elapsed.TotalMinutes < 60 ? $"{Math.Max(1, (int)elapsed.TotalMinutes)} min" : $"{(int)elapsed.TotalHours} h";
        var detail = string.Join(" · ", new[] { session.ActiveProfileLabel, age, session.SessionStatusLabel }.Where(part => !string.IsNullOrEmpty(part)));
        return new(session, session.Title, detail);
    }
}
