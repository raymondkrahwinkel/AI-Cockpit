namespace Cockpit.Core.Abstractions.Claude;

/// <summary>
/// Resolves the <c>claude</c> executable to spawn when a <see cref="Profiles.ClaudeProfile"/>
/// does not pin its own <c>ExecutablePath</c>.
/// </summary>
public interface IClaudeExecutableLocator
{
    /// <summary>
    /// Returns the bundled executable path (the newest version folder under
    /// <c>%APPDATA%\Claude\claude-code\</c>), or <see langword="null"/> if none was found —
    /// callers should then fall back to <c>"claude"</c> resolved via PATH.
    /// </summary>
    string? FindBundledExecutable();
}
