using Cockpit.Core.Updates;

namespace Cockpit.Core.Abstractions.Updates;

/// <summary>
/// Answers whether this copy of the cockpit can replace itself (AC-385). Separate from <see cref="IUpdateService"/>
/// because it is a different question with a different answer source: that one asks GitHub what exists, this one
/// asks the running process what it is.
/// </summary>
public interface IUpdateSupportProbe
{
    /// <summary>
    /// What this copy can do about its own updates. Never throws — a probe that fails to establish anything reports
    /// <see cref="UpdateSupport.NotPackaged"/>, since a property throwing inside a binding fails silently and leaves
    /// the control on its default visibility (the AC-379 shape: an offer behind an invisible banner is not an offer).
    /// </summary>
    UpdateSupport Detect();
}
