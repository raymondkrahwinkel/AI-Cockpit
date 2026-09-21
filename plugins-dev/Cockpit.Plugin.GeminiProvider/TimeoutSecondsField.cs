namespace Cockpit.Plugin.GeminiProvider;

// AC-1345: kept out of OpenAiCompatProviderConfigView so a test can call it without loading Avalonia at
// runtime — that class's instance fields (TextBox, AutoCompleteBox) pull in Avalonia.Controls just from
// being touched, even for a call that never reaches them.
internal static class TimeoutSecondsField
{
    // Blank keeps the SDK default (null), a positive number is accepted, zero/negative are rejected.
    internal static bool TryParse(string text, out int? timeoutSeconds)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            timeoutSeconds = null;
            return true;
        }

        if (int.TryParse(trimmed, out var seconds) && seconds > 0)
        {
            timeoutSeconds = seconds;
            return true;
        }

        timeoutSeconds = null;
        return false;
    }
}
