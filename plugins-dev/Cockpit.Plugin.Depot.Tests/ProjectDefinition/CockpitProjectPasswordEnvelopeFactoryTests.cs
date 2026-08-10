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


    [Fact]
    public void Create_ThenUnwrapWithPassword_RecoversTheSameDataKey()
    {
        var (envelope, dataKey, _) = CockpitProjectPasswordEnvelopeFactory.Create("correct horse battery staple");

        var recovered = CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(envelope, "correct horse battery staple");

        Assert.Equal(dataKey, recovered);
    }

    [Fact]
    public void Create_ThenUnwrapWithRecoveryCode_RecoversTheSameDataKey()
    {
        var (envelope, dataKey, recoveryCode) = CockpitProjectPasswordEnvelopeFactory.Create("hunter2");

        var recovered = CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithRecoveryCode(envelope, recoveryCode);

        Assert.Equal(dataKey, recovered);
    }

    [Fact]
    public void TryUnwrapWithPassword_WrongPassword_ReturnsNull()
    {
        var (envelope, _, _) = CockpitProjectPasswordEnvelopeFactory.Create("right-password");

        Assert.Null(CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(envelope, "wrong-password"));
    }

    [Fact]
    public void TryUnwrapWithRecoveryCode_WrongCode_ReturnsNull()
    {
        var (envelope, _, _) = CockpitProjectPasswordEnvelopeFactory.Create("right-password");

        Assert.Null(CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithRecoveryCode(envelope, "WRONGWRONGWRONGWRONGWRON"));
    }

    [Fact]
    public void TryUnwrapWithPassword_UnknownKdf_ReturnsNullRatherThanThrowing()
    {
        var (envelope, _, _) = CockpitProjectPasswordEnvelopeFactory.Create("right-password");
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

    [Fact]
    public void Create_RecoveryCodeUsesOnlyTheUnambiguousAlphabetAndRequestedLength()
    {
        var (_, _, recoveryCode) = CockpitProjectPasswordEnvelopeFactory.Create("whatever");

        Assert.Equal(24, recoveryCode.Length);
        Assert.DoesNotContain(recoveryCode, character => "0O1IL".Contains(character));
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

    // AC-607 review finding 1: a shared Depot project's project.json can arrive corrupt (buggy build, hand-edit,
    // truncated write) the same way a wrong password can — both must collapse to null, never throw.
    [Fact]
    public void TryUnwrapWithPassword_WrapperSaltIsNull_ReturnsNullRatherThanThrowing()
    {
        var envelope = _DeserializeEnvelope("""
            {"kdf":"pbkdf2-sha512","iterations":210000,
             "password":{"salt":null,"wrappedDataKey":"enc:v1:AAAA"},
             "recovery":{"salt":"AAAA","wrappedDataKey":"enc:v1:AAAA"}}
            """);

        Assert.Null(CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(envelope, "whatever"));
    }

    [Fact]
    public void TryUnwrapWithPassword_WrapperWrappedDataKeyIsNull_ReturnsNullRatherThanThrowing()
    {
        var envelope = _DeserializeEnvelope("""
            {"kdf":"pbkdf2-sha512","iterations":210000,
             "password":{"salt":"AAAA","wrappedDataKey":null},
             "recovery":{"salt":"AAAA","wrappedDataKey":"enc:v1:AAAA"}}
            """);

        Assert.Null(CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(envelope, "whatever"));
    }

    [Fact]
    public void TryUnwrapWithPassword_WrapperItselfIsNull_ReturnsNullRatherThanThrowing()
    {
        var envelope = _DeserializeEnvelope("""
            {"kdf":"pbkdf2-sha512","iterations":210000,
             "password":null,
             "recovery":{"salt":"AAAA","wrappedDataKey":"enc:v1:AAAA"}}
            """);

        Assert.Null(CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(envelope, "whatever"));
    }

    [Fact]
    public void TryUnwrapWithPassword_IterationsIsZeroOrNegative_ReturnsNullRatherThanThrowing()
    {
        var envelope = _DeserializeEnvelope("""
            {"kdf":"pbkdf2-sha512","iterations":0,
             "password":{"salt":"AAAA","wrappedDataKey":"enc:v1:AAAA"},
             "recovery":{"salt":"AAAA","wrappedDataKey":"enc:v1:AAAA"}}
            """);

        Assert.Null(CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(envelope, "whatever"));
    }

    // AC-607 review finding 9: a hostile envelope naming an absurdly large iteration count must not cost a real
    // PBKDF2 call (DoS via CPU) — rejected the same null-shaped way as every other corrupt-input case.
    [Fact]
    public void TryUnwrapWithPassword_IterationsIsAbsurdlyLarge_ReturnsNullRatherThanRunningPbkdf2()
    {
        var envelope = _DeserializeEnvelope("""
            {"kdf":"pbkdf2-sha512","iterations":2000000000,
             "password":{"salt":"AAAA","wrappedDataKey":"enc:v1:AAAA"},
             "recovery":{"salt":"AAAA","wrappedDataKey":"enc:v1:AAAA"}}
            """);

        Assert.Null(CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(envelope, "whatever"));
    }

    // Second review pass, PR #473: WrappedDataKey without the enc:v1: prefix is valid base64 but was never
    // produced by _Wrap — ProjectSecretProtector.Unprotect's idempotency rule (return an unprefixed value as-is)
    // would otherwise hand back those raw bytes as "the data key", independent of whatever password was supplied.
    [Fact]
    public void TryUnwrapWithPassword_WrappedDataKeyMissingTheEncV1Prefix_ReturnsNullRatherThanThePlainBytes()
    {
        var envelope = _DeserializeEnvelope("""
            {"kdf":"pbkdf2-sha512","iterations":210000,
             "password":{"salt":"AAAA","wrappedDataKey":"AAAA"},
             "recovery":{"salt":"AAAA","wrappedDataKey":"enc:v1:AAAA"}}
            """);

        Assert.Null(CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(envelope, "whatever"));
    }
}
