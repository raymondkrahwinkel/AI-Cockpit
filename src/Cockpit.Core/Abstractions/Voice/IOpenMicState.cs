namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// Whether continuous open-mic dictation is actively listening right now. Push-to-talk reads this to stand
/// down while open-mic is on: both capture and transcribe the same speech, so a push-to-talk hold on top of
/// an open mic lands the dictation twice. Open-mic wins — the hold is suppressed.
/// </summary>
public interface IOpenMicState
{
    bool IsListening { get; }
}
