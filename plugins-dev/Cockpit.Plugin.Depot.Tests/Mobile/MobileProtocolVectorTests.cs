using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Events;
using Cockpit.Plugin.Depot.Mobile;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Sessions;
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
        "assistantPairedPhone", "assistantForgottenPhone", "assistantWrongKey", "assistantOpenQuestion",
        "replacedCockpitPub", "readyTimeout", "assistantBeforeReady", "assistantImages",
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CockpitSide_MatchesTheVectors(string @case)
    {
        if (@case is "replacedCockpitPub" or "readyTimeout")
        {
            await _KeyConfirmation(@case);
            return;
        }

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
                var ready = vectors.GetProperty("ready");
                using (var confirmation = new MobileFrameCipher(Hex(vectors.GetProperty("sessionKeys").GetProperty("cockpitToPhone")), Hex(vectors.GetProperty("sessionKeys").GetProperty("phoneToCockpit"))))
                {
                    Assert.Equal(Hex(ready.GetProperty("cockpitToPhone")), confirmation.Seal(Encoding.UTF8.GetBytes(ready.GetProperty("plaintext").GetString() ?? throw new InvalidDataException())));
                    Assert.True(confirmation.TryOpen(Hex(ready.GetProperty("phoneToCockpit")), out var opened));
                    Assert.Equal(ready.GetProperty("plaintext").GetString(), Encoding.UTF8.GetString(opened));
                }
                await _KeyConfirmation("pairing");
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
    // paired now gets a message to the assistant; a forgotten one or an unpaired key gets nothing in, on any stream, and
    // no text from the phone answers a question the assistant waits on.
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
        gateway.AcceptsImages.Returns(true);
        var host = Substitute.For<ICockpitHost>();
        host.Sessions.OpenSessions.Returns([new OpenCockpitSession("pane", "Session")]);
        var operatorChannel = Substitute.For<IOperatorActionChannel>();
        operatorChannel.Snapshot.Returns([]);
        var operatorTransport = Substitute.For<IOperatorActionTransport>();
        operatorTransport.OpenChannel().Returns(operatorChannel);
        operatorTransport.OpenChannel(device).Returns(operatorChannel);
        host.OperatorTransport.Returns(operatorTransport);
        host.SendToSessionAsync("pane", Arg.Any<string>(), Arg.Any<IReadOnlyList<byte[]>>())
            .Returns(new SessionSendResult(true, "this session cannot see images"));
        var toPhone = new List<byte[]>();
        await using var pairing = new MobilePairing(() => [], _ => null!, _ => null!, storage, null!, _ => gateway,
            send: (_, _, frame) =>
            {
                if (frame() is { } sealedFrame)
                {
                    toPhone.Add(sealedFrame);
                }

                return Task.CompletedTask;
            }, host: host);

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

            var phone = _PhoneCipher(phoneKey, ephemeral, cockpitPub, toPhone[0], MobileHandshake.FlagKnownPhone, streamId);
            Assert.Equal(2, toPhone.Count);
            Assert.True(phone.TryOpen(toPhone[1], out var ready));
            using var readyMessage = JsonDocument.Parse(ready);
            Assert.Equal("ready", readyMessage.RootElement.GetProperty("type").GetString());
            if (@case != "assistantBeforeReady")
            {
                pairing.Frame(connection, streamId, device, phone.Seal("{\"type\":\"ready\",\"id\":\"p0\",\"body\":{}}"u8));
            }
            return phone;
        }

        void Say(MobileFrameCipher? phone, string streamId, string text) =>
            pairing.Frame(connection, streamId, device, phone?.Seal(JsonSerializer.SerializeToUtf8Bytes(new { type = "assistant.send", id = "p1", body = new { text } }))
                ?? [MobileFrameCipher.Version, .. new byte[24]]);

        using var first = Connect("stream-1");
        Say(first, "stream-1", "hallo");
        if (@case == "assistantImages")
        {
            await _Images(first!, json => pairing.Frame(connection, "stream-1", device, first!.Seal(Encoding.UTF8.GetBytes(json))), toPhone, gateway, host, device);
        }
        if (@case == "assistantForgottenPhone")
        {
            pairing.Forget(connection, device);
            Say(first, "stream-1", "still here");
            using var again = Connect("stream-2");
            Assert.Null(again);
            Say(again, "stream-2", "let me in");
        }

        if (@case == "assistantOpenQuestion")
        {
            var question = new AssistantChannelRow
            {
                Id = Guid.NewGuid(),
                Kind = AssistantChannelRowKind.Question,
                Text = "Which one?",
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = "question",
                Questions = [new AssistantChannelQuestion { Question = "Which one?", Options = [] }],
            };
            gateway.PendingQuestions.Returns([question]);
            operatorChannel.Snapshot.Returns([new OperatorPendingAction("question", "revision", "question", "cockpit-assistant", question.Text, [], "test-capability")
            {
                Questions = question.Questions,
            }]);
            pairing.Frame(connection, "stream-1", device, first?.Seal("{\"type\":\"operator.pending\",\"id\":\"pending\",\"body\":{}}"u8) ?? []);

            Assert.NotNull(first);
            var read = 2;
            JsonElement LatestPending()
            {
                JsonElement latest = default;
                for (; read < toPhone.Count; read++)
                {
                    Assert.True(first.TryOpen(toPhone[read], out var plaintext));
                    using var document = JsonDocument.Parse(plaintext);
                    if (document.RootElement.GetProperty("type").GetString() == "operator.pending")
                    {
                        latest = document.RootElement.GetProperty("body").Clone();
                    }
                }
                return latest.GetProperty("items");
            }
            Assert.Equal("question", Assert.Single(LatestPending().EnumerateArray()).GetProperty("id").GetString());
            gateway.PendingQuestions.Returns([]);
            operatorChannel.Snapshot.Returns([]);
            gateway.RowChanged += Raise.Event<EventHandler<AssistantChannelRow>>(gateway,
                question with { IsUpdate = true, Questions = [] });
            Assert.Empty(LatestPending().EnumerateArray());
            gateway.PendingQuestions.Returns([question]);
            gateway.RowChanged += Raise.Event<EventHandler<AssistantChannelRow>>(gateway, question);
            Say(first, "stream-1", "1");
        }

        var expected = @case switch
        {
            "assistantWrongKey" or "assistantBeforeReady" => 0,
            "assistantImages" => 4,
            _ => 1,
        };
        await gateway.Received(expected).SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>());
        if (expected > 0)
        {
            await gateway.Received(1).SendAsync(device, "hallo", Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>());
        }
    }

    // As a phone writes JSON: base64's + and / as they are, not as \u002B, which would not fit a full chunk in a frame.
    private static readonly JsonSerializerOptions _Unescaped = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // AC-2265 (PROTOCOL.md § 12): the vectors' chunks and a 3 MB photo reach the assistant byte for byte, a session without
    // image input still gets the text and the reason, and every broken attachment is refused once and dropped.
    private static async Task _Images(MobileFrameCipher phone, Action<string> send, List<byte[]> toPhone, IAssistantChannelGateway gateway, ICockpitHost host, string device)
    {
        // Frames 0 and 1 are the handshake reply and the ready the phone already opened.
        var read = 2;
        List<(string Type, JsonElement Body)> Replies()
        {
            List<(string Type, JsonElement Body)> replies = [];
            for (; read < toPhone.Count; read++)
            {
                Assert.True(phone.TryOpen(toPhone[read], out var plaintext));
                using var message = JsonDocument.Parse(plaintext);
                replies.Add((message.RootElement.GetProperty("type").GetString()!, message.RootElement.GetProperty("body").Clone()));
            }

            return replies;
        }

        string Chunk(string attachmentId, int seq, int total, string mime, byte[] data) =>
            JsonSerializer.Serialize(new { type = "attachment.chunk", id = "p", body = new { attachmentId, seq, total, mime, data = Convert.ToBase64String(data) } }, _Unescaped);
        string Message(string type, object body) => JsonSerializer.Serialize(new { type, id = "p", body });
        Replies();

        var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mobile", "protocol-vectors.json"))).RootElement.GetProperty("attachment");
        foreach (var chunk in vectors.GetProperty("chunks").EnumerateArray())
        {
            send(chunk.GetString()!);
        }

        send(vectors.GetProperty("send").GetString()!);
        var image = Convert.FromHexString(vectors.GetProperty("image").GetString()!);
        await gateway.Received(1).SendAsync(device, "kijk", Arg.Is<IReadOnlyList<byte[]>>(images => images.Count == 1 && images[0].SequenceEqual(image)), Arg.Any<CancellationToken>());

        var photo = new byte[3_000_000];
        Random.Shared.NextBytes(photo);
        photo[0] = 0xFF;
        photo[1] = 0xD8;
        photo[2] = 0xFF;
        var pieces = photo.Chunk(MobileAttachments.MaxChunkBytes).ToArray();
        Assert.Equal(63, pieces.Length);
        for (var seq = 0; seq < pieces.Length; seq++)
        {
            send(Chunk("photo", seq, pieces.Length, "image/jpeg", pieces[seq]));
        }

        send(Message("assistant.send", new { text = "", attachmentIds = new[] { "photo" } }));
        await gateway.Received(1).SendAsync(device, "", Arg.Is<IReadOnlyList<byte[]>>(images => images.Count == 1 && images[0].SequenceEqual(photo)), Arg.Any<CancellationToken>());

        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0];
        send(Chunk("shot", 0, 1, "image/jpeg", jpeg));
        send(Message("session.send", new { paneId = "pane", text = "en dit", attachmentIds = new[] { "shot" } }));
        await host.Received(1).SendToSessionAsync("pane", "en dit", Arg.Is<IReadOnlyList<byte[]>>(images => images.Count == 1));
        var replies = Replies();
        Assert.All(replies.Where(reply => reply.Type == "assistant.sent"), reply => Assert.True(reply.Body.GetProperty("ok").GetBoolean()));
        var sessionSent = Assert.Single(replies, reply => reply.Type == "session.sent").Body;
        Assert.True(sessionSent.GetProperty("ok").GetBoolean());
        Assert.Equal("this session cannot see images", sessionSent.GetProperty("imagesRefused").GetString());

        send(Chunk("late", 1, 2, "image/jpeg", jpeg));
        send(Chunk("png", 0, 1, "image/png", jpeg));
        send(Chunk("gif", 0, 1, "image/gif", jpeg));
        send(Chunk("huge", 0, 176, "image/jpeg", jpeg));
        send(Chunk("swap", 0, 2, "image/jpeg", jpeg));
        send(Chunk("swap", 1, 2, "image/png", jpeg));
        for (var open = 0; open < MobileAttachments.MaxOpen; open++)
        {
            send(Chunk($"open{open}", 0, 2, "image/jpeg", jpeg));
        }

        send(Chunk("eleventh", 0, 2, "image/jpeg", jpeg));
        send(Chunk("late", 0, 2, "image/jpeg", jpeg));
        send(Message("assistant.send", new { text = "zonder", attachmentIds = new[] { "png" } }));
        await gateway.Received(1).SendAsync(device, "zonder", Arg.Is<IReadOnlyList<byte[]>>(images => images.Count == 0), Arg.Any<CancellationToken>());
        replies = Replies();
        Assert.Equal(["late", "png", "gif", "huge", "swap", "eleventh"],
            replies.Where(reply => reply.Type == "attachment.refused").Select(reply => reply.Body.GetProperty("attachmentId").GetString()));
        var sent = Assert.Single(replies, reply => reply.Type == "assistant.sent").Body;
        Assert.True(sent.GetProperty("ok").GetBoolean());
        Assert.Equal("an attachment was missing, incomplete or refused", sent.GetProperty("imagesRefused").GetString());

        await _ImageCapabilityChangesAsync();
    }

    // Category D: a connected phone must hear image capability changes without another request or turn.
    private static async Task _ImageCapabilityChangesAsync()
    {
        SessionCapabilities capabilities = SessionCapabilities.ClaudeCli with { SupportsVision = false };
        var runtime = Substitute.For<ISessionRuntime>();
        runtime.Capabilities.Returns(_ => capabilities);
        var manager = Substitute.For<ISessionManager>();
        manager.Create(Arg.Any<SessionProfile?>()).Returns(runtime);
        var sessionHost = new SessionHost(() => "assistant", manager, TimeProvider.System);
        sessionHost.Attach(null);
        await using var session = new SessionHostHandle("assistant", "Assistant", false, "desk", null, null, sessionHost);
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(session);
        var contribution = new AssistantChannelContribution
        {
            Id = "phone", Name = "Phone", Access = AssistantChannelAccess.ForSingleUser("device").Access
                ?? throw new InvalidOperationException(),
        };
        var gateway = new AssistantChannelGateway(contribution, host, Substitute.For<IConsentBroker>(),
            NullLogger<AssistantChannelGateway>.Instance);
        var logger = Substitute.For<ILogger>();
        List<byte[]> sent = [];
        using var channel = new MobileAssistantChannel("device", gateway, bytes => { sent.Add(bytes); return true; }, "Laptop", "1", logger);
        channel.StreamStarted("stream");
        channel.Receive("{\"type\":\"cockpit.status\",\"id\":\"first\",\"body\":{}}"u8.ToArray(), "stream");
        sent.Clear();

        capabilities = capabilities with { SupportsVision = true };
        runtime.EventAppended += Raise.Event<Action<SessionEvent>>(new SessionCapabilitiesChanged { SessionId = "session", Capabilities = capabilities });
        using (JsonDocument status = JsonDocument.Parse(Assert.Single(sent)))
        {
            Assert.Equal("cockpit.status", status.RootElement.GetProperty("type").GetString());
            Assert.True(status.RootElement.GetProperty("body").GetProperty("images").GetBoolean());
        }

        sent.Clear();
        runtime.EventAppended += Raise.Event<Action<SessionEvent>>(new SessionCapabilitiesChanged { SessionId = "session", Capabilities = capabilities });
        Assert.Empty(sent);

        capabilities = capabilities with { SupportsVision = false };
        runtime.EventAppended += Raise.Event<Action<SessionEvent>>(new SessionCapabilitiesChanged { SessionId = "session", Capabilities = capabilities });
        using (JsonDocument status = JsonDocument.Parse(Assert.Single(sent)))
        {
            Assert.False(status.RootElement.GetProperty("body").GetProperty("images").GetBoolean());
        }

        sent.Clear();
        capabilities = capabilities with { SupportsVision = true };
        runtime.EventAppended += Raise.Event<Action<SessionEvent>>(new SessionCapabilitiesChanged { SessionId = "session", Capabilities = capabilities });
        sent.Clear();
        host.Session.Returns((IAssistantSession?)null);
        host.PropertyChanged += Raise.Event<System.ComponentModel.PropertyChangedEventHandler>(host,
            new System.ComponentModel.PropertyChangedEventArgs(nameof(IAssistantSessionHost.Session)));
        using (JsonDocument status = JsonDocument.Parse(Assert.Single(sent)))
        {
            Assert.False(status.RootElement.GetProperty("body").GetProperty("images").GetBoolean());
        }

        sent.Clear();
        host.Session.Returns(session);
        host.PropertyChanged += Raise.Event<System.ComponentModel.PropertyChangedEventHandler>(host,
            new System.ComponentModel.PropertyChangedEventArgs(nameof(IAssistantSessionHost.Session)));
        using (JsonDocument status = JsonDocument.Parse(Assert.Single(sent)))
        {
            Assert.True(status.RootElement.GetProperty("body").GetProperty("images").GetBoolean());
        }

        channel.StreamEnded();
        sent.Clear();
        capabilities = capabilities with { SupportsVision = false };
        runtime.EventAppended += Raise.Event<Action<SessionEvent>>(new SessionCapabilitiesChanged { SessionId = "session", Capabilities = capabilities });
        Assert.Empty(sent);

        channel.StreamStarted("reconnected");
        channel.Receive("{\"type\":\"cockpit.status\",\"id\":\"again\",\"body\":{}}"u8.ToArray(), "reconnected");
        var statusLogs = logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log) && call.GetArguments()[0] is LogLevel.Information)
            .Select(call => call.GetArguments()[2]).OfType<IReadOnlyList<KeyValuePair<string, object?>>>()
            .Where(entry => entry.Any(pair => pair.Key == "Images"))
            .Select(entry => ((bool?)entry.Single(pair => pair.Key == "Images").Value,
                (string?)entry.Single(pair => pair.Key == "Reason").Value));
        Assert.Equal(new (bool?, string?)[]
        {
            (false, "first status"), (true, "capability change"), (false, "capability change"),
            (true, "capability change"), (false, "capability change"), (true, "capability change"),
            (false, "reconnect"),
        }, statusLogs);
    }

    private static async Task _KeyConfirmation(string @case)
    {
        const string connection = "connection";
        const string device = "device";
        const string streamId = "stream";
        var storage = new FakePluginStorage();
        var keys = new MobileKeyStore(storage);
        var registration = new DepotConnectionRegistration(connection, "Depot", "https://depot.example.org");
        await using var events = new DepotEvents(() => [], _ => throw new InvalidOperationException(), (_, _, _, _, _) => throw new InvalidOperationException());
        var time = Substitute.For<TimeProvider>();
        long timestamp = 0;
        time.TimestampFrequency.Returns(TimeSpan.TicksPerSecond);
        time.GetTimestamp().Returns(_ => timestamp);
        TimerCallback? expire = null;
        time.CreateTimer(Arg.Do<TimerCallback>(callback => expire = callback), Arg.Is<object?>(state => state == null), TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan)
            .Returns(Substitute.For<ITimer>());
        var toPhone = new List<byte[]>();
        var openedChannels = new List<AssistantChannelContribution>();
        await using var pairing = new MobilePairing(() => [registration],
            _ => new DepotBrowserModelTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"pairingId\":\"pairing\",\"depotUrl\":\"https://depot.example.org\",\"expiresIn\":300}"),
            }), _ => new DepotBrowserModelTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)), storage, events, channel =>
            {
                openedChannels.Add(channel);
                return Substitute.For<IAssistantChannelGateway>();
            }, send: (_, _, frame) =>
            {
                if (frame() is { } sent)
                {
                    toPhone.Add(sent);
                }
                return Task.CompletedTask;
            }, time: time);
        var status = await pairing.StartAsync(connection, CancellationToken.None);
        Assert.Equal(DepotPairingState.Open, status.State);
        using var qr = JsonDocument.Parse(Base64Url.DecodeFromChars(status.Qr ?? throw new InvalidDataException()));
        byte[] QrBytes(string name) => Base64Url.DecodeFromChars(qr.RootElement.GetProperty(name).GetString() ?? throw new InvalidDataException());
        using var phoneKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var impostor = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var phonePub = MobileKeyStore.PublicKey(phoneKey);
        var cockpitPub = @case == "replacedCockpitPub" ? MobileKeyStore.PublicKey(impostor) : QrBytes("cockpitPub");
        var mac = MobileHandshake.PairingMac(QrBytes("pairingSecret"), "pairing", phonePub);
        byte[] handshake = [MobileHandshake.Version, MobileHandshake.FlagPairing, .. phonePub, .. MobileKeyStore.PublicKey(ephemeral), .. mac];
        pairing.Opened(connection, streamId, device);
        pairing.Frame(connection, streamId, device, handshake);
        Assert.Equal(2, toPhone.Count);
        Assert.False(keys.IsPaired(connection, phonePub));
        Assert.Empty((await pairing.PhonesAsync(connection, false, CancellationToken.None)).Phones);
        Assert.Empty(openedChannels);
        Assert.Equal(DepotPairingState.Waiting, pairing.Status(connection).State);
        using var phone = _PhoneCipher(phoneKey, ephemeral, cockpitPub, toPhone[0], MobileHandshake.FlagPairing, streamId);
        Assert.Equal(@case != "replacedCockpitPub", phone.TryOpen(toPhone[1], out _));
        if (@case == "readyTimeout")
        {
            timestamp = TimeSpan.FromSeconds(15).Ticks;
            Assert.NotNull(expire);
            expire(null);
            Assert.Equal(DepotPairingState.None, pairing.Status(connection).State);
        }
        else
        {
            timestamp = TimeSpan.FromMilliseconds(14999).Ticks;
        }

        pairing.Frame(connection, streamId, device, phone.Seal("{\"type\":\"ready\",\"id\":\"p0\",\"body\":{}}"u8));
        Assert.Equal(@case == "pairing", keys.IsPaired(connection, phonePub));
        Assert.Equal(@case == "pairing" ? 1 : 0, (await pairing.PhonesAsync(connection, false, CancellationToken.None)).Phones.Count);
        Assert.Equal(@case == "pairing" ? 1 : 0, openedChannels.Count);
        toPhone.Clear();
        pairing.Opened(connection, "reconnect", device);
        pairing.Frame(connection, "reconnect", device, [MobileHandshake.Version, MobileHandshake.FlagKnownPhone, .. phonePub, .. MobileKeyStore.PublicKey(ephemeral)]);
        Assert.Equal(@case == "pairing" ? 2 : 0, toPhone.Count);
    }

    private static MobileFrameCipher _PhoneCipher(ECDiffieHellman phone, ECDiffieHellman ephemeral, byte[] cockpitPub, byte[] reply, byte flag, string streamId)
    {
        using var cockpit = MobileKeyStore.ImportPublicKey(cockpitPub);
        using var cockpitEphemeral = MobileKeyStore.ImportPublicKey(reply[1..]);
        byte[] ikm = [.. ephemeral.DeriveRawSecretAgreement(cockpit.PublicKey), .. phone.DeriveRawSecretAgreement(cockpit.PublicKey), .. ephemeral.DeriveRawSecretAgreement(cockpitEphemeral.PublicKey)];
        byte[] info = [MobileHandshake.Version, flag, .. cockpitPub, .. reply[1..], .. MobileKeyStore.PublicKey(phone), .. MobileKeyStore.PublicKey(ephemeral)];
        byte[] Key(string label) => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, Encoding.UTF8.GetBytes(streamId), [.. Encoding.ASCII.GetBytes(label), .. info]);
        return new MobileFrameCipher(Key("cockpit-mobile v1 phone-to-cockpit"), Key("cockpit-mobile v1 cockpit-to-phone"));
    }
}
