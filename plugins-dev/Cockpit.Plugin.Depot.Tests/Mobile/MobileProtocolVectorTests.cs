using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cockpit.Plugin.Depot.Mobile;
using Cockpit.Plugins.Abstractions.Channels;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests.Mobile;

// AC-2222, category A: the Cockpit's side of the phone protocol against the vectors in Mobile/protocol-vectors.json, the
// same file the app's Kotlin side is held to. A handshake or frame check that accepts too much still looks like it works
// from the outside, so every refusal the protocol promises is a row here.
public class MobileProtocolVectorTests
{
    public static TheoryData<string> Cases =>
    [
        "pairing", "wrongPairingSecret", "replacedPhonePub", "offCurveEphPub", "offCurvePhonePub", "zeroPointEphPub",
        "zeroPointPhonePub", "wrongLength", "wrongVersion", "unknownFlag", "pairingWithoutPending", "expiredPending",
        "secondPairingSamePending", "tamperedTag", "replay", "counterGap", "shortFrame", "mirroredFrame", "replayedHandshake",
        "assistantPairedPhone", "assistantForgottenPhone", "assistantWrongKey",
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CockpitSide_MatchesTheVectors(string @case)
    {
        if (@case.StartsWith("assistant", StringComparison.Ordinal))
        {
            await _AssistantDoor(@case);
            return;
        }

        var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mobile", "protocol-vectors.json"))).RootElement;
        string Text(string name) => vectors.GetProperty(name).GetString()!;
        byte[] Hex(JsonElement element) => Convert.FromHexString(element.GetString()!);
        ECDiffieHellman Key(string name)
        {
            var pair = vectors.GetProperty("keys").GetProperty(name);
            var pub = Hex(pair.GetProperty("pub"));
            return ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = Hex(pair.GetProperty("d")),
                Q = new ECPoint { X = pub[1..33], Y = pub[33..] },
            });
        }
        using var cockpitKey = Key("cockpit");
        using var cockpitEphemeral = Key("cockpitEphemeral");
        var cockpitPub = Hex(vectors.GetProperty("keys").GetProperty("cockpit").GetProperty("pub"));
        PairingPayload Pending(TimeSpan expiresIn) =>
            new(Text("depot"), Text("pairingId"), cockpitPub, Convert.FromHexString(Text("pairingSecret")), DateTimeOffset.UtcNow + expiresIn);
        var pending = Pending(PairingPayload.Lifetime);
        var handshakes = vectors.GetProperty("handshakes");
        var refusedHandshakes = vectors.GetProperty("refusedHandshakes");
        var frames = vectors.GetProperty("frames").EnumerateArray().Select(frame => Hex(frame.GetProperty("frame"))).ToArray();
        byte[] Handshake(string name) => Hex(handshakes.TryGetProperty(name, out var found) ? found : refusedHandshakes.GetProperty(name));

        bool Accept(string handshake, out MobileHandshakeResult? result, PairingPayload? code, bool paired = false) =>
            MobileHandshake.TryAccept(Handshake(handshake), cockpitKey, cockpitEphemeral, Text("streamId"), code, _ => paired, out result);

        if (@case is "wrongPairingSecret" or "replacedPhonePub" || refusedHandshakes.TryGetProperty(@case, out _))
        {
            Assert.False(Accept(@case, out var refused, pending));
            Assert.Null(refused);
            // A refused handshake does not use the code up: the real phone can still pair with it.
            Assert.True(pending.IsOpen());
            return;
        }

        switch (@case)
        {
            case "pairingWithoutPending":
                Assert.False(Accept("pairing", out _, code: null));
                return;
            case "expiredPending":
                Assert.False(Accept("pairing", out _, Pending(TimeSpan.FromSeconds(-1))));
                return;
            case "replayedHandshake":
                // Depot plays a recorded handshake again on the same stream id: the Cockpit answers with a new ephemeral
                // key, so the keys differ from the recorded stream's and none of its recorded frames opens.
                Assert.True(Accept("knownPhone", out var recorded, code: null, paired: true));
                Assert.True(MobileHandshake.TryAccept(Handshake("knownPhone"), cockpitKey, Text("streamId"), null, _ => true, out var replayed));
                using (var recordedCipher = recorded!.Cipher)
                using (var replayedCipher = replayed!.Cipher)
                {
                    Assert.NotEqual(recorded.Reply, replayed.Reply);
                    Assert.NotEqual(recordedCipher.Seal("probe"u8), replayedCipher.Seal("probe"u8));
                }
                return;
        }

        Assert.Equal(Text("qr"), pending.Encode());
        Assert.True(Accept("pairing", out var accepted, pending));
        using var cipher = accepted!.Cipher;
        switch (@case)
        {
            case "pairing":
                Assert.True(accepted.NewlyPaired);
                Assert.Equal(Hex(vectors.GetProperty("handshakeReply")), accepted.Reply);
                Assert.Equal(Hex(vectors.GetProperty("keys").GetProperty("phone").GetProperty("pub")), accepted.PhonePub);
                foreach (var (frame, expected) in frames.Zip(vectors.GetProperty("frames").EnumerateArray()).Take(2))
                {
                    Assert.True(cipher.TryOpen(frame, out var plaintext));
                    Assert.Equal(expected.GetProperty("plaintext").GetString(), Encoding.UTF8.GetString(plaintext));
                }
                var reply = vectors.GetProperty("frames")[2];
                Assert.Equal(frames[2], cipher.Seal(Encoding.UTF8.GetBytes(reply.GetProperty("plaintext").GetString()!)));
                // Without a MAC only a phone paired earlier gets in.
                Assert.False(Accept("knownPhone", out _, code: null));
                Assert.True(Accept("knownPhone", out var known, code: null, paired: true));
                known!.Cipher.Dispose();
                break;
            case "secondPairingSamePending":
                // The code paired one phone and is spent: the same handshake again gets nothing.
                Assert.False(pending.IsOpen());
                Assert.False(Accept("pairing", out _, pending));
                break;
            case "tamperedTag":
                Assert.False(cipher.TryOpen(Hex(vectors.GetProperty("tamperedFrame")), out var tampered));
                Assert.Empty(tampered);
                // A refused frame does not move the counter: the genuine frame with the same counter still opens.
                Assert.True(cipher.TryOpen(frames[0], out _));
                break;
            case "replay":
                Assert.True(cipher.TryOpen(frames[0], out _));
                Assert.True(cipher.TryOpen(frames[1], out _));
                Assert.False(cipher.TryOpen(frames[1], out _));
                Assert.False(cipher.TryOpen(frames[0], out _));
                break;
            case "counterGap":
                // Depot leaves frame 0 out: frame 1 is refused, and the stream still takes frame 0 then 1 in order.
                Assert.False(cipher.TryOpen(frames[1], out _));
                Assert.True(cipher.TryOpen(frames[0], out _));
                Assert.True(cipher.TryOpen(frames[1], out _));
                break;
            case "shortFrame":
                Assert.False(cipher.TryOpen(frames[0][..24], out _));
                Assert.False(cipher.TryOpen([], out _));
                break;
            case "mirroredFrame":
                // The Cockpit's own frame sent back to it: the other direction's key, so it does not open.
                Assert.False(cipher.TryOpen(cipher.Seal("echo"u8), out _));
                break;
        }
    }

    // AC-2227: a phone that speaks the protocol, through MobilePairing to the assistant's door. Only a phone that is
    // paired now gets a message to the assistant; a forgotten one or an unpaired key gets nothing in, on any stream.
    private static async Task _AssistantDoor(string @case)
    {
        const string connection = "connection";
        const string device = "device";
        var storage = new FakePluginStorage();
        var keys = new MobileKeyStore(storage);
        Assert.True(keys.TryGetCockpitKey(connection, out var cockpitKey));
        var cockpitPub = MobileKeyStore.PublicKey(cockpitKey!);
        cockpitKey!.Dispose();
        using var pairedKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        keys.AddPhone(connection, MobileKeyStore.PublicKey(pairedKey));
        storage.Set($"mobilePairedPhones-{connection}", new List<MobilePairedPhone>
        {
            new(device, "Phone", System.Buffers.Text.Base64Url.EncodeToString(MobileKeyStore.PublicKey(pairedKey)), DateTimeOffset.UtcNow),
        });
        using var strangerKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var phoneKey = @case == "assistantWrongKey" ? strangerKey : pairedKey;

        var gateway = Substitute.For<IAssistantChannelGateway>();
        gateway.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>())
            .Returns(AssistantChannelSendResult.Sent());
        var toPhone = new List<byte[]>();
        await using var pairing = new MobilePairing(() => [], _ => null!, storage, null!, _ => gateway,
            send: (_, _, frame) =>
            {
                if (frame() is { } sealedFrame)
                {
                    toPhone.Add(sealedFrame);
                }

                return Task.CompletedTask;
            });

        MobileFrameCipher? Connect(string streamId)
        {
            toPhone.Clear();
            using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            byte[] handshake = [MobileHandshake.Version, MobileHandshake.FlagKnownPhone, .. MobileKeyStore.PublicKey(phoneKey), .. MobileKeyStore.PublicKey(ephemeral)];
            pairing.Opened(connection, streamId, device);
            pairing.Frame(connection, streamId, device, handshake);
            if (toPhone.Count == 0)
            {
                return null;
            }

            using var cockpit = MobileKeyStore.ImportPublicKey(cockpitPub);
            using var cockpitEphemeral = MobileKeyStore.ImportPublicKey(toPhone[0][1..]);
            byte[] ikm =
            [
                .. ephemeral.DeriveRawSecretAgreement(cockpit.PublicKey),
                .. phoneKey.DeriveRawSecretAgreement(cockpit.PublicKey),
                .. ephemeral.DeriveRawSecretAgreement(cockpitEphemeral.PublicKey),
            ];
            byte[] info = [.. handshake[..2], .. cockpitPub, .. toPhone[0][1..], .. handshake[2..]];
            byte[] Key(string label) => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, Encoding.UTF8.GetBytes(streamId), [.. Encoding.ASCII.GetBytes(label), .. info]);
            return new MobileFrameCipher(Key("cockpit-mobile v1 phone-to-cockpit"), Key("cockpit-mobile v1 cockpit-to-phone"));
        }

        void Say(MobileFrameCipher? phone, string streamId, string text) =>
            pairing.Frame(connection, streamId, device, phone?.Seal(JsonSerializer.SerializeToUtf8Bytes(new { type = "assistant.send", id = "p1", body = new { text } }))
                ?? [MobileFrameCipher.Version, .. new byte[24]]);

        using var first = Connect("stream-1");
        Say(first, "stream-1", "hallo");
        if (@case == "assistantForgottenPhone")
        {
            pairing.Forget(connection, device);
            Say(first, "stream-1", "still here");
            using var again = Connect("stream-2");
            Assert.Null(again);
            Say(again, "stream-2", "let me in");
        }

        var expected = @case == "assistantWrongKey" ? 0 : 1;
        await gateway.Received(expected).SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>());
        if (expected == 1)
        {
            await gateway.Received(1).SendAsync(device, "hallo", Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>());
        }
    }
}
