namespace Cockpit.Core.Abstractions.Claude;

/// <summary>
/// A child process hosted inside a Windows pseudo console (ConPTY) so it believes it is attached
/// to a real interactive terminal (<c>isTTY=true</c>). Used by TTY mode (#9) to run the real
/// <c>claude</c> TUI: the cockpit reads rendered terminal output from <see cref="OutputStream"/>,
/// writes keystrokes to <see cref="InputStream"/>, and forwards panel resizes via <see cref="Resize"/>.
/// </summary>
public interface IConPtyProcess : IDisposable
{
    /// <summary>Stream to write user keystrokes (already VT-encoded) into the pty's stdin.</summary>
    Stream InputStream { get; }

    /// <summary>Stream to read the pty's rendered output (ANSI/VT bytes) from.</summary>
    Stream OutputStream { get; }

    /// <summary>The child process id.</summary>
    int ProcessId { get; }

    /// <summary>Resizes the pseudo console. Called when the hosting panel resizes.</summary>
    void Resize(short columns, short rows);
}
