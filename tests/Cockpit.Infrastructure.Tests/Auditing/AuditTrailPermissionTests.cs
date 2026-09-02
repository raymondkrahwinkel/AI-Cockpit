using System.Runtime.Versioning;
using System.Reflection;
using Cockpit.Core.Abstractions.Consent;
using Cockpit.Infrastructure.Auditing;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Consent;
using Microsoft.Extensions.Logging;
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
    public void RestrictAuditTrails_LeavesAFileItDoesNotOwnAlone()
    {
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

    [Fact]
    public void AuditTrailFiles_Names_CoverEveryTrailTheCockpitWrites()
    {
        // The repair can only close what it knows about, and it knows only what AuditTrailFiles names. A trail that
        // names its own file would be created owner-only and then never repaired on the machines that already have
        // it — a gap that reads as covered. Asking each trail where it actually writes is the only way to hold the
        // two sides together; counting them would pass on a list with a name duplicated and one missing.
        var written = _TrailTypes().Select(_DefaultPathOf).ToHashSet();

        Assert.Equal(AuditTrailFiles.In(CockpitConfigPath.Root).ToHashSet(), written);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static IEnumerable<Type> _TrailTypes() =>
        typeof(JsonlAuditLog<>).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsGenericTypeDefinition: false } && _IsTrail(type));

    private static bool _IsTrail(Type type)
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

    /// <summary>
    /// Builds a trail the way the container does — through the constructor that takes only a logger, the one that
    /// decides the default path — and reports where it would write. Constructing writes nothing; the file appears on
    /// the first record. A trail that no longer offers that shape fails here on purpose: where it writes by default
    /// is what this test exists to read.
    /// </summary>
    private static string _DefaultPathOf(Type type)
    {
        var constructor = type.GetConstructor([typeof(ILogger<>).MakeGenericType(type)]);
        Assert.NotNull(constructor);

        var logger = typeof(NullLogger<>).MakeGenericType(type).GetProperty("Instance")?.GetValue(null);
        var trail = constructor.Invoke([logger]);

        var filePath = type.GetProperty("FilePath", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(trail);

        return Assert.IsType<string>(filePath);
    }

    private static ConsentAuditEntry Entry() =>
        new(DateTimeOffset.UtcNow, ConsentAuditAction.Approved, "Workflows", "pane-1", "workflows", "scope", "rm -rf /tmp/x", Remembered: false);
}
