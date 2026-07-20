namespace Cockpit.Core.Verify;

/// <summary>
/// How a verify runner captures the UI it reports back (AC-86). Only <see cref="Avalonia"/> exists in v1 — a
/// headless render of the app's own visual tree to a text snapshot plus an optional screenshot — but it is part
/// of the persisted runner shape so a later web/DOM capture type can be added without migrating existing entries,
/// and so the tool can refuse a runner whose capture kind this build does not know how to read back.
/// </summary>
public enum VerifyCaptureType
{
    /// <summary>A headless Avalonia render: a <c>VisualTreeSnapshot</c> text file and an optional PNG screenshot.</summary>
    Avalonia,
}
