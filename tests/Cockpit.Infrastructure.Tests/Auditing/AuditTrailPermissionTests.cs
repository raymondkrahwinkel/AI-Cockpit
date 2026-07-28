using Cockpit.Core.Abstractions.Consent;
using Cockpit.Infrastructure.Auditing;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Consent;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Tests.Auditing;

/// <summary>
/// An audit trail is the record of what was approved, what a sub-agent was asked to do, and what one agent told
/// another — free text that nothing stops from carrying a token, a path or a customer's name. Its value is in the
/// fact that only its owner can read it, so these tests read the mode bits rather than trusting the comment above
/// the write (AC-435): the trail was created with <c>File.AppendAllText</c>, which leaves a file at the process
/// umask, and on a stock Fedora that is world-readable.
/// <para>
/// Unix-only where the mode is asserted: Windows has no mode bits, and the equivalent boundary there is the
/// per-user profile directory the state root sits in.
/// </para>
/// </summary>
public sealed class AuditTrailPermissionTests : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode WorldReadable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-trail-perm-{Guid.NewGuid():N}");

    public AuditTrailPermissionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task RecordAsync_CreatingTheTrail_LeavesItOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_directory, AuditTrailFiles.Consent);
        var log = new ConsentAuditLog(path, NullLogger<ConsentAuditLog>.Instance);

        await log.RecordAsync(Entry());

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
    }

    [Fact]
    public void RestrictAuditTrails_ClosesTheTrailsAnEarlierVersionLeftOpen()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // What every machine that ran an earlier build looks like today: trails created at the umask. The write path
        // is fixed, but a create mode only applies to a file being created, so these stay open until something
        // reaches back for them.
        foreach (var trail in AuditTrailFiles.In(_directory))
        {
            File.WriteAllText(trail, "{}" + Environment.NewLine);
            File.SetUnixFileMode(trail, WorldReadable);
        }

        CredentialFileHousekeeping.RestrictAuditTrails(_directory);

        foreach (var trail in AuditTrailFiles.In(_directory))
        {
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(trail));
        }
    }

    [Fact]
    public void RestrictAuditTrails_LeavesAFileItDoesNotOwnAlone()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // The state root is shared with whatever else the operator keeps there, and a derived log can be pointed at
        // an arbitrary file. The repair therefore walks the known trails, and only those.
        var unrelated = Path.Combine(_directory, "notes.txt");
        File.WriteAllText(unrelated, "mine");
        File.SetUnixFileMode(unrelated, WorldReadable);

        CredentialFileHousekeeping.RestrictAuditTrails(_directory);

        Assert.Equal(WorldReadable, File.GetUnixFileMode(unrelated));
    }

    [Fact]
    public void RestrictAuditTrails_OnAStateRootWithoutTrails_DoesNothing()
    {
        // A fresh install has no trails at all; the repair runs on every start and must not mind.
        CredentialFileHousekeeping.RestrictAuditTrails(_directory);

        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public void EveryTrailIsNamedInTheListTheRepairWalks()
    {
        // The repair can only close what it knows about. A trail added past AuditTrailFiles would be created
        // owner-only and never repaired on the machines that already have it — a gap that reads as covered.
        var trails = typeof(JsonlAuditLog<>).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsGenericTypeDefinition: false } && IsTrail(type))
            .ToList();

        Assert.Equal(AuditTrailFiles.Names.Count, trails.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static bool IsTrail(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(JsonlAuditLog<>))
            {
                return true;
            }
        }

        return false;
    }

    private static ConsentAuditEntry Entry() =>
        new(DateTimeOffset.UtcNow, ConsentAuditAction.Approved, "Workflows", "pane-1", "workflows", "scope", "rm -rf /tmp/x", Remembered: false);
}
