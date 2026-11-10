using Cockpit.Infrastructure.Configuration;
using Cockpit.Core.Profiles;
using Cockpit.Core.Provisioning;
using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Sessions;

internal static class SessionGuardrailEnvironment
{
    public static IReadOnlyDictionary<string, string> Build(SessionProfile? profile, string? workingDirectory, string scratch, string? configDirectory, ProvisionPolicy? policy, Action<string> log)
    {
        var roots = new List<string> { Path.GetFullPath(workingDirectory ?? Directory.GetCurrentDirectory()), Path.GetFullPath(scratch) };
        foreach (var allowedPath in profile?.GuardrailAllowedPaths ?? [])
        {
            try
            {
                roots.Add(GuardrailPaths.Expand(allowedPath));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
            {
                log($"Skipped guardrail allowed path '{allowedPath}': it does not apply on this machine.");
            }
        }
        var plansRoot = configDirectory;
        if (profile?.Claude is { } claude)
        {
            plansRoot = string.IsNullOrWhiteSpace(claude.ConfigDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                : claude.ConfigDir;
        }
        if (string.IsNullOrWhiteSpace(plansRoot))
        {
            plansRoot = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        }
        if (string.IsNullOrWhiteSpace(plansRoot))
        {
            plansRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        }
        roots.Add(Path.Combine(Path.GetFullPath(plansRoot), "plans"));

        return new Dictionary<string, string>
        {
            ["COCKPIT_GUARD_MODE"] = OrgPolicyCeilings.GuardrailMode(policy, profile?.GuardrailMode ?? SessionProfileEntry.ExistingProfileGuardrailMode).ToString(),
            ["COCKPIT_SESSION_JOB"] = SessionScratch.JobNameOf(scratch),
            ["COCKPIT_HOST_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["COCKPIT_GUARD_ROOTS"] = string.Join(Path.PathSeparator, roots.Select(GuardrailPaths.Expand).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)),
        };
    }
}
