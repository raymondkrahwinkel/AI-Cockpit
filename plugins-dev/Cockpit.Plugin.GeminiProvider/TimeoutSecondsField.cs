namespace Cockpit.Plugin.GeminiProvider;

// AC-1345: kept out of OpenAiCompatProviderConfigView so a test can call it without loading Avalonia at
// runtime — that class's instance fields (TextBox, AutoCompleteBox) pull in Avalonia.Controls just from
// being touched, even for a call that never reaches them.
internal static class TimeoutSecondsField
{
    // Blank returns null, which BuildClientOptions then maps to OpenAiCompatConfig.DefaultTimeoutSeconds
    // (600s) — not the OpenAI SDK's own 100s default. A positive number is accepted; zero/negative rejected.
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
