using System.Reflection;
using Cockpit.Core.Abstractions.Consent;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Infrastructure.Auditing;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Delegation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Tests.Auditing;

/// <summary>
/// Only the usage trail rolls over (AC-399): it is the one trail with a write on every completed turn, so it is
/// the one whose disk footprint the maintainer decided to bound. The consent trail (#AC-47) and the delegation
/// trail (#67) share the exact same append machinery, <see cref="JsonlAuditLog{T}"/>, and are deliberately left
/// append-only forever — the consent trail's whole reason to exist is that a decision, once logged, cannot be
/// erased. Rotation was built one layer above that shared base specifically so nothing added for the usage trail
/// could leak into these two; these tests hold both sides of that claim:
/// <list type="bullet">
/// <item>structurally — <see cref="ConsentAuditLog"/> and <see cref="DelegationAuditLog"/> get their
/// <c>RecordAsync</c>/<c>ReadRecentAsync</c> straight from <see cref="JsonlAuditLog{T}"/>, unlike
/// <see cref="Usage.UsageHistoryLog"/>, which hides both to add the rollover check;</item>
/// <item>functionally — writing well past what would trigger the usage trail's rollover never produces a
/// <c>.1.jsonl</c> file for either trail, and every line written is still there afterwards.</item>
/// </list>
/// </summary>
public sealed class SharedAuditTrailsDoNotRotateTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));

    public SharedAuditTrailsDoNotRotateTests() => Directory.CreateDirectory(_tempDir);

    [Fact]
    public void ConsentAuditLog_InheritsRecordAndRead_FromTheSharedBase_RatherThanDefiningItsOwn()
    {
        _AssertNoRotationOverride(typeof(ConsentAuditLog), typeof(JsonlAuditLog<ConsentAuditEntry>));
    }

    [Fact]
    public void DelegationAuditLog_InheritsRecordAndRead_FromTheSharedBase_RatherThanDefiningItsOwn()
    {
        _AssertNoRotationOverride(typeof(DelegationAuditLog), typeof(JsonlAuditLog<DelegationAuditEntry>));
    }

    // If a future edit ever gave one of these trails its own RecordAsync/ReadRecentAsync (the shape rotation
    // needs — see Usage.UsageHistoryLog, which does exactly this), GetMethod would resolve to that trail's own
    // type instead of the shared base, and this goes red. Mirrors how UsageHistoryLog is proven *to* rotate: by
    // the same structural fact, the other way around.
    private static void _AssertNoRotationOverride(Type trailType, Type sharedBaseType)
    {
        foreach (var methodName in new[] { "RecordAsync", "ReadRecentAsync" })
        {
            var method = trailType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            method.Should().NotBeNull();
            method!.DeclaringType.Should().Be(sharedBaseType,
                $"{trailType.Name}.{methodName} must come from the shared, non-rotating base, not a rotation-adding override of its own");
        }
    }

    [Fact]
    public async Task ConsentTrail_WrittenWellPastAModestTrailsUsualSize_NeverRolls()
    {
        var path = Path.Combine(_tempDir, "consent-audit.jsonl");
        var log = new ConsentAuditLog(path, NullLogger<ConsentAuditLog>.Instance);

        for (var i = 0; i < _WriteCount; i++)
        {
            await log.RecordAsync(_ConsentEntry(i));
        }

        File.Exists(_DotOneOf(path)).Should().BeFalse("the consent trail must stay append-only, unbounded, forever");
        new FileInfo(path).Length.Should().BeGreaterThan(0);
        (await log.ReadRecentAsync(limit: int.MaxValue)).Should().HaveCount(_WriteCount,
            "every line the consent trail was given must still be there — nothing here may ever drop a record");
    }

    [Fact]
    public async Task DelegationTrail_WrittenWellPastAModestTrailsUsualSize_NeverRolls()
    {
        var path = Path.Combine(_tempDir, "delegation-audit.jsonl");
        var log = new DelegationAuditLog(path, NullLogger<DelegationAuditLog>.Instance);

        for (var i = 0; i < _WriteCount; i++)
        {
            await log.RecordAsync(_DelegationEntry(i));
        }

        File.Exists(_DotOneOf(path)).Should().BeFalse("the delegation trail must stay append-only, unbounded, forever");
        new FileInfo(path).Length.Should().BeGreaterThan(0);
        (await log.ReadRecentAsync(limit: int.MaxValue)).Should().HaveCount(_WriteCount,
            "every line the delegation trail was given must still be there — nothing here may ever drop a record");
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
