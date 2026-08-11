namespace Cockpit.App.ViewModels;

// One line the session bar renders (AC-683): its key (what a per-line Dismiss takes down), the sentence, and
// which of the bar's action buttons — if any — belong on it. Built fresh by `_RebuildWarnings`, the same
// "clear and re-add" shape `SessionRateWindow`/`UsagePillItem` already use for their own collections.
public sealed record SessionWarningItem(string Key, string Text, bool ShowResumeOffer, bool ShowChangeResumeMoment, bool ShowKill, bool ShowSignInAgain);
