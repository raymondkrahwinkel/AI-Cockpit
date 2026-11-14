namespace Cockpit.Plugin.Matomo;

internal sealed class MatomoException(string code, string message, string fix) : Exception(message)
{
    public string Code { get; } = code;

    public string Fix { get; } = fix;
}
