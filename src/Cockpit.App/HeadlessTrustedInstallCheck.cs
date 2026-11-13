using Cockpit.Infrastructure.Updates;

namespace Cockpit.App;

// `--verify-trusted-install [root] [--signed e]... [--pin sha256]...`: names-only verdict, exit 0 only if trusted (AC-2342).
internal static class HeadlessTrustedInstallCheck
{
    private const string Flag = "--verify-trusted-install";

    public static bool IsRequested(string[] args) => args.Contains(Flag);

    public static int Run(string[] args)
    {
        var after = args.SkipWhile(arg => arg != Flag).Skip(1).ToArray();
        var root = after.FirstOrDefault() is { } first && !first.StartsWith("--", StringComparison.Ordinal)
            ? first
            : AppContext.BaseDirectory;
        var signed = _Values(after, "--signed");
        var pins = _Values(after, "--pin");
        var report = TrustedInstallation.Verify(
            root,
            signed.Count > 0 ? signed : ["Cockpit.App.exe"],
            pins.Count > 0 ? pins : TrustedInstallation.PinnedPublisherSha256);
        Console.WriteLine(report.IsTrusted ? "trusted: yes" : "trusted: no");
        Console.WriteLine($"pins: {(pins.Count > 0 ? "command-line" : "compiled")}");
        foreach (var failure in report.Failures)
        {
            Console.WriteLine(failure);
        }

        return report.IsTrusted ? 0 : 1;
    }

    private static List<string> _Values(string[] args, string option) =>
        args.Zip(args.Skip(1)).Where(pair => pair.First == option).Select(pair => pair.Second).ToList();
}
