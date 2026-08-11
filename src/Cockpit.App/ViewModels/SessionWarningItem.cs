namespace Cockpit.App.ViewModels;

// One line the session bar renders (AC-683): its key (what a per-line Dismiss takes down), the sentence, and
// which of the bar's action buttons — if any — belong on this particular line. Built fresh by
// `SessionPanelViewModel._RebuildWarnings` whenever anything that could change the list changes, the same
// "clear and re-add" shape `SessionRateWindow`/`UsagePillItem` already use for their own collections, rather than
// patched in place — the list is short and rebuilding it is simpler than tracking which single field of which
// single row just changed.
public sealed record SessionWarningItem(string Key, string Text, bool ShowResumeOffer, bool ShowChangeResumeMoment, bool ShowKill);
