using System.Text.Json;
using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

public class CockpitProjectPasswordEnvelopeFactoryTests
{
    // Same options CockpitProjectDefinitionJson/CockpitProjectDefinitionStore actually deserialize with
    // (JsonSerializerDefaults.Web -> camelCase) — these tests must go through real deserialization, not a
    // hand-built C# object, to prove the actual off-the-wire path.
    private static readonly JsonSerializerOptions _WireOptions = new(JsonSerializerDefaults.Web);

    private static CockpitProjectPasswordEnvelope _DeserializeEnvelope(string json)
    {
        var envelope = JsonSerializer.Deserialize<CockpitProjectPasswordEnvelope>(json, _WireOptions);
        return envelope ?? throw new InvalidOperationException("Test setup produced a null envelope.");
    }

    // One Create: the password and the recovery code both unwrap the same data key, and the code uses only the unambiguous alphabet.
    [Fact]
    public void Create_ThenUnwrapWithPasswordOrRecoveryCode_RecoversTheSameDataKey_AndTheCodeIsUnambiguous()
    {
        var (envelope, dataKey, recoveryCode) = CockpitProjectPasswordEnvelopeFactory.Create("correct horse battery staple");

        Assert.Equal(dataKey, CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(envelope, "correct horse battery staple"));
        Assert.Equal(dataKey, CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithRecoveryCode(envelope, recoveryCode));
        Assert.Equal(24, recoveryCode.Length);
        Assert.DoesNotContain(recoveryCode, character => "0O1IL".Contains(character));
    }

    [Fact]
    public void TryUnwrap_WrongPasswordOrWrongCode_ReturnsNull()
    {
        var (envelope, _, _) = CockpitProjectPasswordEnvelopeFactory.Create("right-password");

        Assert.Null(CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(envelope, "wrong-password"));
        Assert.Null(CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithRecoveryCode(envelope, "WRONGWRONGWRONGWRONGWRON"));

        envelope.Kdf = "argon2id"; // as if written by a future build with a KDF this one does not know
        Assert.Null(CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(envelope, "right-password"));
    }

    [Fact]
    public void ChangePassword_OldPasswordNoLongerUnwraps_NewPasswordDoes_RecoveryUntouched()
    {
        var (envelope, dataKey, recoveryCode) = CockpitProjectPasswordEnvelopeFactory.Create("old-password");

        var changed = CockpitProjectPasswordEnvelopeFactory.ChangePassword(envelope, dataKey, "new-password");

        Assert.Null(CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(changed, "old-password"));
        Assert.Equal(dataKey, CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(changed, "new-password"));
        Assert.Equal(dataKey, CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithRecoveryCode(changed, recoveryCode));
    }

    // Iron Law #8: no password, data key or recovery code character ever reaches an exception message or ToString().
    [Fact]
    public void NullReturns_NeverLeakThePasswordOrDataKeyAnywhereObservable()
    {
        var (envelope, dataKey, recoveryCode) = CockpitProjectPasswordEnvelopeFactory.Create("super-secret-password");

        Assert.Null(CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(envelope, "wrong-one"));

        // Real .ToString()/interpolation on the real objects, not a hand-assembled string from their properties —
        // so a leaky override added to any of these later is actually exercised here, not silently missed.
        var envelopeText = $"{envelope} {envelope.Password} {envelope.Recovery}";
        Assert.DoesNotContain("super-secret-password", envelopeText, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-one", envelopeText, StringComparison.Ordinal);
        Assert.DoesNotContain(recoveryCode, envelopeText, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(dataKey), envelopeText, StringComparison.Ordinal);
    }

    // AC-607 review findings 1 and 9, PR #473: a shared Depot project's project.json can arrive corrupt (buggy build,
    // hand-edit, truncated write) or hostile; every shape collapses to null, never throws, never runs a huge PBKDF2,
    // and never hands back an unprefixed wrappedDataKey as "the data key".
    [Theory]
    [InlineData(210000, """{"salt":null,"wrappedDataKey":"enc:v1:AAAA"}""")]
    [InlineData(210000, """{"salt":"AAAA","wrappedDataKey":null}""")]
    [InlineData(210000, "null")]
    [InlineData(0, """{"salt":"AAAA","wrappedDataKey":"enc:v1:AAAA"}""")]
    [InlineData(2000000000, """{"salt":"AAAA","wrappedDataKey":"enc:v1:AAAA"}""")]
    [InlineData(210000, """{"salt":"AAAA","wrappedDataKey":"AAAA"}""")]
    public void TryUnwrapWithPassword_CorruptOrHostileEnvelope_ReturnsNullRatherThanThrowing(int iterations, string passwordWrapper)
    {
        var envelope = _DeserializeEnvelope(
            $$$"""{"kdf":"pbkdf2-sha512","iterations":{{{iterations}}},"password":{{{passwordWrapper}}},"recovery":{"salt":"AAAA","wrappedDataKey":"enc:v1:AAAA"}}""");

        Assert.Null(CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(envelope, "whatever"));
    }
}
