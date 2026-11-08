using System.Text.Json;

namespace Cockpit.Infrastructure.Tests.Plugins;

public sealed class PluginSignatureContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(27)]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(35)]
    public void SshSig_VerifiesTheContractSignatureCases(int row)
    {
        using var cases = _Fixture("cases.json");
        var test = cases.RootElement.GetProperty("cases")[row];
        var result = SshSig.Verify(Convert.FromBase64String(test.GetProperty("message").GetString() ?? ""),
            test.GetProperty("signature").GetString() ?? "");
        var pinMatches = !test.TryGetProperty("pin", out var pin) || pin.GetString() == result.Fingerprint;

        Assert.Equal(test.GetProperty("expected").GetBoolean(), result.Ok && pinMatches);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(27)]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(35)]
    [InlineData(36)]
    [InlineData(37)]
    [InlineData(38)]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(41)]
    [InlineData(42)]
    [InlineData(43)]
    [InlineData(44)]
    public void SshSig_EnforcesEveryContractInputRule(int row)
    {
        using var cases = _Fixture("cases.json");
        var test = cases.RootElement.GetProperty("inputs")[row];
        var fields = test.GetProperty("utf16");

        var exception = Record.Exception(() => SshSig.Statement(_Text(fields, "org"), _Text(fields, "id"), _Text(fields, "version"),
            _Text(fields, "platform"), _Text(fields, "sha256"), _Text(fields, "repository"), _Text(fields, "tag")));

        Assert.Equal(test.GetProperty("valid").GetBoolean(), exception is null);
    }

    [Fact]
    public void SshSig_SignsThePublicContractVectorByteForByte()
    {
        using var vector = _Fixture("vector.json");
        var data = vector.RootElement;
        var seed = Convert.FromHexString(data.GetProperty("seedHex").GetString() ?? "");
        var key = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(seed, 0).GeneratePublicKey().GetEncoded();
        var message = Convert.FromBase64String(data.GetProperty("statementBase64").GetString() ?? "");

        Assert.Equal(data.GetProperty("signature").GetString(), SshSig.Sign(seed, key, message));
    }

    private static JsonDocument _Fixture(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Plugins", "Signatures", name)));

    private static string _Text(JsonElement fields, string key) =>
        new(fields.GetProperty(key).EnumerateArray().Select(value => (char)value.GetInt32()).ToArray());
}
