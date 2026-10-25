using Cockpit.Core.Abstractions.Consent;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Infrastructure.Auditing;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Delegation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Tests.Auditing;

/// <summary>
/// Only the usage trail rolls over (AC-399): it is the one trail with a write on every completed turn, so it is
/// the one whose disk footprint the maintainer decided to bound. The consent trail (#AC-47) and the delegation
/// trail (#67) share the exact same append machinery, <see cref="JsonlAuditLog{T}"/>, and are deliberately left
/// append-only forever — the consent trail's whole reason to exist is that a decision, once logged, cannot be
/// erased. Rotation was built one layer above that shared base specifically so nothing added for the usage trail
/// could leak into these two: writing well past what would trigger the usage trail's rollover never produces a
/// <c>.1.jsonl</c> file for either trail, and every line written is still there afterwards.
/// </summary>
public sealed class SharedAuditTrailsDoNotRotateTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));

    public SharedAuditTrailsDoNotRotateTests() => Directory.CreateDirectory(_tempDir);

    [Fact]
    public async Task ConsentAndDelegationTrails_WrittenWellPastAModestSize_NeverRoll()
    {
        var consentPath = Path.Combine(_tempDir, "consent-audit.jsonl");
        var consentLog = new ConsentAuditLog(consentPath, NullLogger<ConsentAuditLog>.Instance);

        for (var i = 0; i < _WriteCount; i++)
        {
            await consentLog.RecordAsync(_ConsentEntry(i));
        }

        Assert.False(File.Exists(_DotOneOf(consentPath)), "the consent trail must stay append-only, unbounded, forever");
        Assert.True(new FileInfo(consentPath).Length > 0);
        Assert.Equal(_WriteCount, System.Linq.Enumerable.Count(await consentLog.ReadRecentAsync(limit: int.MaxValue)));

        var delegationPath = Path.Combine(_tempDir, "delegation-audit.jsonl");
        var delegationLog = new DelegationAuditLog(delegationPath, NullLogger<DelegationAuditLog>.Instance);

        for (var i = 0; i < _WriteCount; i++)
        {
            await delegationLog.RecordAsync(_DelegationEntry(i));
        }

        Assert.False(File.Exists(_DotOneOf(delegationPath)), "the delegation trail must stay append-only, unbounded, forever");
        Assert.True(new FileInfo(delegationPath).Length > 0);
        Assert.Equal(_WriteCount, System.Linq.Enumerable.Count(await delegationLog.ReadRecentAsync(limit: int.MaxValue)));
    }

    // Both trails trim their one free-text field to 300 chars before writing (ActionText / Prompt), so this many
    // records is comfortably more than a real install's per-run volume while keeping the test fast.
    private const int _WriteCount = 1_500;

    private static string _DotOneOf(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}.1{Path.GetExtension(path)}");

    private static ConsentAuditEntry _ConsentEntry(int i) =>
        new(DateTimeOffset.UtcNow, ConsentAuditAction.Approved, "Workflows", "pane-1", "workflows", i.ToString(), new string('x', 400), Remembered: false);

    private static DelegationAuditEntry _DelegationEntry(int i) => new(
        DateTimeOffset.UtcNow,
        DelegationAuditAction.Delegated,
        ProfileLabel: "local",
        TaskId: i.ToString(),
        Label: "summarise",
        TaskType: null,
        Prompt: new string('x', 400),
        Reason: null);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
