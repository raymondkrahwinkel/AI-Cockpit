namespace Cockpit.Core.Verify;

// Verify runner capture kind (AC-86); v1's `Avalonia` renders the visual tree to text and optional screenshot.
// Persist it now so future web/DOM kinds avoid migration and unknown kinds can be refused safely.
public enum VerifyCaptureType
{
    // A headless Avalonia render: a `VisualTreeSnapshot` text file and an optional PNG screenshot.
    Avalonia,
}
