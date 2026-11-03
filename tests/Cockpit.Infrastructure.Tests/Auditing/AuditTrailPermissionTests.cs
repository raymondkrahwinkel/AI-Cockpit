using System.Runtime.Versioning;
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

    [PosixFact("Windows has no mode bits; there the boundary is the per-user profile directory instead.")]
    [UnsupportedOSPlatform("windows")]
    public async Task RecordAsync_CreatingTheTrail_LeavesItOwnerOnly()
    {
        var path = Path.Combine(_directory, AuditTrailFiles.Consent);
        var log = new ConsentAuditLog(path, NullLogger<ConsentAuditLog>.Instance);

        await log.RecordAsync(Entry());

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
    }

    [PosixFact("Windows has no mode bits; there the boundary is the per-user profile directory instead.")]
    [UnsupportedOSPlatform("windows")]
    public void RestrictAuditTrails_ClosesTheTrailsAnEarlierVersionLeftOpen()
    {
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


    [PosixFact("Windows has no mode bits; there the boundary is the per-user profile directory instead.")]
    [UnsupportedOSPlatform("windows")]
    public void RestrictAuditTrails_LeavesASymlinkedTrailAlone()
    {
        // Changing the mode follows the link, so a link wearing a trail's name would aim this pass at a file the
        // cockpit never wrote. That file's permissions are the operator's, whichever way they point.
        var target = Path.Combine(_directory, "somewhere-else.txt");
        File.WriteAllText(target, "theirs");
        File.SetUnixFileMode(target, WorldReadable);
        File.CreateSymbolicLink(Path.Combine(_directory, AuditTrailFiles.Consent), target);

        CredentialFileHousekeeping.RestrictAuditTrails(_directory);

        Assert.Equal(WorldReadable, File.GetUnixFileMode(target));
    }


    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static ConsentAuditEntry Entry() =>
        new(DateTimeOffset.UtcNow, ConsentAuditAction.Approved, "Workflows", "pane-1", "workflows", "scope", "rm -rf /tmp/x", Remembered: false);
}
