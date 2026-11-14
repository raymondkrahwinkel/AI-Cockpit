using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Vault;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Vault;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Security;

// Category A (AC-2336, AC-2346): the vault's hard boundaries. If one breaks silently, a secret lands on disk or at the model provider.
public sealed class VaultHubTests : IDisposable
{
    private const string Canary = "vault-canary-7f3c9e";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-vault-{Guid.NewGuid():N}");

    private string ConfigPath => Path.Combine(_directory, "cockpit.json");

    public VaultHubTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // Also category B (AC-2364): an unavailable vault refuses use but never deletes the stored connection.
    [Fact]
    public async Task WithEncryptionOff_TheHubRefusesToStore_AndNeverDeletesAStoredConnection()
    {
        var holder = new SecretKeyHolder();
        await File.WriteAllTextAsync(ConfigPath, "{ \"Profiles\": [] }");
        var protection = new SecretProtectionService(ConfigPath, holder);
        var hub = new VaultHub(protection, ConfigPath, holder, NullLogger<VaultHub>.Instance);
        var context = hub.Register("plugin-a", new Provider());

        Assert.NotNull(context);
        Assert.Equal(VaultAvailability.EncryptionOff, context.Availability);
        Assert.False(context.TryStoreCredential(Canary));
        Assert.Null(context.ReadCredential());

        var written = await File.ReadAllTextAsync(ConfigPath);
        Assert.DoesNotContain("Vault", written, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, written, StringComparison.Ordinal);

        await protection.EnableAsync("vault-test-password");
        Assert.True(context.TryStoreCredential(Canary));
        var stored = await File.ReadAllTextAsync(ConfigPath);

        // The 2026-11-14 incident: a status read that lands mid-swap reports encryption off while it is on.
        var misread = Substitute.For<ISecretProtectionService>();
        misread.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(new SecretProtectionStatus(Enabled: false, Unlocked: false, ShouldWarnUnprotected: false));
        var blind = new VaultHub(misread, ConfigPath, holder, NullLogger<VaultHub>.Instance).Register("plugin-a", new Provider())!;
        Assert.Equal(VaultAvailability.EncryptionOff, blind.Availability);
        Assert.Null(blind.ReadCredential());
        Assert.False(blind.TryStoreCredential("other"));

        Assert.Equal(stored, await File.ReadAllTextAsync(ConfigPath));

        // The race itself: mid-swap, under the write gate, the file is briefly gone; a reader waits instead of reading "off".
        var aside = ConfigPath + ".swapping";
        var gate = await CockpitConfigWriteGate.AcquireAsync(ConfigPath, CancellationToken.None);
        File.Move(ConfigPath, aside);
        var read = Task.Run(context.ReadCredential);
        var answeredMidSwap = await Task.WhenAny(read, Task.Delay(500)) == read;
        File.Move(aside, ConfigPath);
        await gate.DisposeAsync();
        Assert.False(answeredMidSwap);
        Assert.Equal(Canary, await read);

        Assert.Equal(stored, await File.ReadAllTextAsync(ConfigPath));
        await Assert.ThrowsAsync<SecretProtectionException>(() => protection.DisableAsync());
        Assert.Equal(stored, await File.ReadAllTextAsync(ConfigPath));
    }

    [Fact]
    public async Task ASessionGrantForOneArgv_DoesNotCoverAnother_AndNeitherAnotherPluginNorTheOutputGetsTheValue()
    {
        var holder = new SecretKeyHolder();
        await File.WriteAllTextAsync(ConfigPath, "{ \"Profiles\": [] }");
        var protection = new SecretProtectionService(ConfigPath, holder);
        await protection.EnableAsync("vault-test-password");
        var hub = new VaultHub(protection, ConfigPath, holder, NullLogger<VaultHub>.Instance);

        // AC-2333 S1: a plugin that takes over a provider id does not inherit the owner's stored credential.
        var owner = new Provider(Canary);
        Assert.True(hub.Register("plugin-a", owner)!.TryStoreCredential(Canary));
        hub.Unregister(owner);
        var connected = new Provider(Canary);
        Assert.Null(hub.Register("plugin-b", connected)!.ReadCredential());
        var consent = Substitute.For<IConsentBroker>();
        consent.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>()).Returns(
            new ConsentDecision(ConsentOutcome.Approved, Remembered: false) { Chosen = new Dictionary<string, IReadOnlyList<string>> { ["scope"] = ["session"] } });
        var session = Substitute.For<ISessionHandle>();
        session.PaneId.Returns("pane-a");
        session.WorkingDirectory.Returns(_directory);
        session.ProjectId.Returns((string?)null);
        var sessions = Substitute.For<ISessionRegistry>();
        sessions.Find("pane-a").Returns(session);
        var tools = new VaultMcpTools(hub, consent, sessions, NullLogger<VaultMcpTools>.Instance);
        string[] printX = OperatingSystem.IsWindows() ? ["cmd", "/c", "echo %X%"] : ["sh", "-c", "echo $X"];
        string[] printXAgain = [.. printX[..^1], printX[^1] + " again"];
        var env = new Dictionary<string, string> { ["X"] = "test://deploy" };

        McpRequestContext.Set("pane-a");
        string refused = await tools.WriteAsync("test", "Deploy token", "command", ["must-not-start"]);
        Assert.Contains("Agent writes are disabled", refused, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, refused, StringComparison.Ordinal);
        Assert.Contains("Agent writes are disabled", await tools.WriteAsync("test", "Deploy token", "generate"), StringComparison.Ordinal);

        string[] results =
        [
            await tools.RunAsync(printX, env, "test"),
            await tools.RunAsync(printX, env, "test"),
            await tools.RunAsync(printXAgain, env, "test"),
        ];

        await consent.Received(2).RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>());
        Assert.All(results, result =>
        {
            Assert.Contains("[vault: Deploy token]", result, StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, result, StringComparison.Ordinal);
        });

        string[] readInput = OperatingSystem.IsWindows()
            ? ["powershell", "-NoProfile", "-Command", "[Console]::Out.Write([Console]::In.ReadToEnd()); [Console]::Error.Write($env:X)"]
            : ["sh", "-c", "cat; printf '%s' \"$X\" >&2"];
        string combined = await tools.RunAsync(readInput, new() { ["X"] = "test://second" }, "test", stdinReference: "test://deploy");
        string stdinOnly = await tools.RunAsync(readInput, [], "test", stdinReference: "test://deploy");
        Assert.Contains("[vault: Second token]", combined, StringComparison.Ordinal);
        await consent.Received(3).RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>());
        Assert.All(new[] { combined, stdinOnly }, result =>
        {
            Assert.Contains("\"exitCode\":0", result, StringComparison.Ordinal);
            Assert.Contains("[vault: Deploy token]", result, StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, result, StringComparison.Ordinal);
        });

        string[] readFile = OperatingSystem.IsWindows()
            ? ["powershell", "-NoProfile", "-Command", "$f=[IO.File]::Open($env:VAULT_TEST_FILE,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)); try { $r=[IO.StreamReader]::new($f); [Console]::Write($r.ReadToEnd()) } finally { $f.Dispose() }"]
            : ["sh", "-c", "cat \"$VAULT_TEST_FILE\""];
        var fileMapping = new Dictionary<string, string> { ["VAULT_TEST_FILE"] = "test://deploy" };
        string verified = await tools.RunAsync(readFile, [], "test", files: fileMapping, verifyOnly: true);
        string fileOutput = await tools.RunAsync(readFile, [], "test", files: fileMapping);
        Assert.Equal("{\"ok\":true}", verified);
        Assert.Contains("[vault: Deploy token]", fileOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, fileOutput, StringComparison.Ordinal);
        await consent.Received(5).RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>());
        Assert.False(Directory.Exists(hub.RuntimeDirectory) && Directory.EnumerateDirectories(hub.RuntimeDirectory, ".cockpit-vault-*").Any());

        string[] wait = OperatingSystem.IsWindows() ? ["powershell", "-NoProfile", "-Command", "Start-Sleep -Seconds 10"] : ["sleep", "10"];
        string timedOut = await tools.RunAsync(wait, [], "test", timeoutSeconds: 1, files: fileMapping);
        Assert.Contains("\"timedOut\":true", timedOut, StringComparison.Ordinal);
        Assert.False(Directory.Exists(hub.RuntimeDirectory) && Directory.EnumerateDirectories(hub.RuntimeDirectory, ".cockpit-vault-*").Any());

        string special = Canary + "/+ with spaces";
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(special));
        string url = Uri.EscapeDataString(special);
        var (maskedOutput, _) = VaultMcpTools.Redact(encoded + "|" + url, [(special, "dummy")]);
        Assert.DoesNotContain(encoded, maskedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(url, maskedOutput, StringComparison.Ordinal);

        string[] hexOutput = OperatingSystem.IsWindows()
            ? ["powershell", "-NoProfile", "-Command", "$v=[Environment]::GetEnvironmentVariable('[stdin] [verify only]'); if ($null -eq $v) { $v=[Console]::In.ReadToEnd().TrimEnd([char]10) }; [Console]::Write([BitConverter]::ToString([Text.Encoding]::UTF8.GetBytes($v)).Replace('-','').ToLowerInvariant())"]
            : ["sh", "-c", "v=$(printenv '[stdin] [verify only]') || v=$(cat); printf '%s' \"$v\" | od -An -tx1 | tr -d ' \\n'"];
        Assert.Equal("{\"ok\":true}", await tools.RunAsync(hexOutput, [], "grant-mode proof", stdinReference: "test://deploy", verifyOnly: true));
        consent.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>()).Returns(new ConsentDecision(ConsentOutcome.Denied, Remembered: false));
        string forged = await tools.RunAsync(hexOutput, new() { ["[stdin] [verify only]"] = "test://deploy" }, "grant-mode proof");
        Assert.False(forged.Contains(Convert.ToHexString(Encoding.UTF8.GetBytes(Canary)), StringComparison.OrdinalIgnoreCase), "A forged verify-only channel grant exposed a value (suppressed).");
        Assert.Contains("\"ok\":false", await tools.RunAsync(hexOutput, [], "grant-mode proof", stdinReference: "test://deploy"), StringComparison.Ordinal);

        string taggedScalar = System.Text.Json.JsonSerializer.Serialize(new { @fields = new Dictionary<string, string> { [Canary] = "dummy" } });
        taggedScalar = taggedScalar.Insert(1, "\"$cockpitFields\":1,");
        string nativeJson = System.Text.Json.JsonSerializer.Serialize(new[] { new { id = "dummy", key = "Opaque", value = taggedScalar } });
        var bwsConnection = new Cockpit.Plugin.Bitwarden.BitwardenProvider.Connection("dummy", "project", "Agents");
        var scalarItems = Cockpit.Plugin.Bitwarden.BitwardenProvider.ItemsIn(nativeJson, bwsConnection);
        Assert.Single(scalarItems);
        Assert.True(!System.Text.Json.JsonSerializer.Serialize(scalarItems).Contains(Canary, StringComparison.Ordinal), "Scalar contents became public metadata (value suppressed).");
        nativeJson = System.Text.Json.JsonSerializer.Serialize(new[] { new { id = "dummy", key = "Bundle", value = taggedScalar,
            note = System.Text.Json.JsonSerializer.Serialize(new { cockpitVaultBundleVersion = 1, fieldNames = new[] { "password" } }) } });
        var declaredItems = Cockpit.Plugin.Bitwarden.BitwardenProvider.ItemsIn(nativeJson, bwsConnection);
        Assert.True(!System.Text.Json.JsonSerializer.Serialize(declaredItems).Contains(Canary, StringComparison.Ordinal), "Undeclared bundle keys became public metadata (value suppressed).");

        var fullBundle = Enumerable.Range(1, 64).ToDictionary(index => "field" + index, _ => Canary, StringComparer.Ordinal);
        Cockpit.Plugin.Bitwarden.BitwardenProvider.MergeFields(fullBundle, new Dictionary<string, string> { ["field1"] = Canary });
        Assert.Throws<Cockpit.Plugin.Bitwarden.BitwardenException>(() =>
            Cockpit.Plugin.Bitwarden.BitwardenProvider.MergeFields(fullBundle, new Dictionary<string, string> { ["field65"] = Canary }));
        Assert.Equal(64, fullBundle.Count);
        nativeJson = System.Text.Json.JsonSerializer.Serialize(new[] { new { id = "dummy", key = "FullBundle", value = "private",
            note = System.Text.Json.JsonSerializer.Serialize(new { cockpitVaultBundleVersion = 1, fieldNames = fullBundle.Keys.ToArray() }) } });
        Assert.Equal(64, Cockpit.Plugin.Bitwarden.BitwardenProvider.ItemsIn(nativeJson, bwsConnection).Count);

        if (OperatingSystem.IsWindows())
        {
            using var privateFiles = new VaultSecretFiles(hub.RuntimeDirectory);
            string privatePath = privateFiles.Add(Canary);
            using var identity = WindowsIdentity.GetCurrent();
            var acl = new FileInfo(privatePath).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            Assert.StartsWith(hub.RuntimeDirectory + Path.DirectorySeparatorChar, privatePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(acl.AreAccessRulesProtected);
            Assert.Equal(identity.User, acl.GetOwner(typeof(SecurityIdentifier)));
            Assert.Single(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                Assert.False(rule.IsInherited);
                Assert.Equal(identity.User, rule.IdentityReference);
            }

            string ownedDirectory = Path.Combine(_directory, "owned-with-modify");
            var directoryInfo = Directory.CreateDirectory(ownedDirectory);
            var ownerCanModify = new DirectorySecurity();
            ownerCanModify.SetOwner(identity.User!);
            ownerCanModify.SetAccessRuleProtection(true, false);
            ownerCanModify.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.Modify, AccessControlType.Allow));
            directoryInfo.SetAccessControl(ownerCanModify);
            CockpitConfigPath.EnsurePrivateDirectory(ownedDirectory);
            Assert.True(directoryInfo.GetAccessControl().AreAccessRulesProtected);
            Assert.Equal(FileSystemRights.FullControl, Assert.Single(directoryInfo.GetAccessControl()
                .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()).FileSystemRights);
        }

        consent.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>()).Returns(
            new ConsentDecision(ConsentOutcome.Approved, Remembered: false) { Chosen = new Dictionary<string, IReadOnlyList<string>> { ["scope"] = ["session"] } });
        connected.CanWrite = true;
        var otherTools = new VaultMcpTools(hub, consent, sessions, NullLogger<VaultMcpTools>.Instance);
        string[] apply = OperatingSystem.IsWindows()
            ? ["powershell", "-NoProfile", "-Command", "[IO.File]::WriteAllText('remote-state',[Console]::In.ReadToEnd().TrimEnd([char]10))"]
            : ["sh", "-c", "IFS= read -r value; printf '%s' \"$value\" > remote-state"];
        connected.BlockNextWrite = true;
        Task<string> first = tools.RunAsync(apply, [], "rotate A", stdinReference: "test://stage-a", updateOnSuccess: "test://deploy");
        await connected.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connected.NextVersionRead = observed;
        Task<string> second = otherTools.RunAsync(apply, [], "rotate B", stdinReference: "test://stage-b", updateOnSuccess: "test://deploy");
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        connected.ContinueWrite.TrySetResult();
        string[] rotations = await Task.WhenAll(first, second);
        Assert.Contains("\"updated\":true", rotations[0], StringComparison.Ordinal);
        Assert.Contains("\"ok\":false", rotations[1], StringComparison.Ordinal);
        Assert.True(await File.ReadAllTextAsync(Path.Combine(_directory, "remote-state")) == connected.CurrentValue, "Remote and vault diverged after concurrent rotations (values suppressed).");

        connected.WriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connected.ContinueWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connected.BlockNextWrite = true;
        first = tools.RunAsync(apply, [], "rotate with competing direct write", stdinReference: "test://stage-a", updateOnSuccess: "test://deploy");
        await connected.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connected.NextVersionRead = observed;
        Task<string> direct = otherTools.WriteAsync("test", "Deploy token", "generate", reference: "test://deploy");
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        connected.ContinueWrite.TrySetResult();
        rotations = await Task.WhenAll(first, direct);
        Assert.Contains("\"updated\":true", rotations[0], StringComparison.Ordinal);
        Assert.Contains("\"ok\":false", rotations[1], StringComparison.Ordinal);
        Assert.True(await File.ReadAllTextAsync(Path.Combine(_directory, "remote-state")) == connected.CurrentValue, "A direct write bypassed rotation coordination (values suppressed).");

        connected.ChangeOnRead = connected.VersionReads + 3;
        string beforeStale = connected.CurrentValue;
        string stale = await tools.RunAsync(apply, [], "external revision changed", stdinReference: "test://stage-b", updateOnSuccess: "test://deploy");
        Assert.Contains("\"updated\":false", stale, StringComparison.Ordinal);
        Assert.True(connected.CurrentValue == beforeStale, "A stale snapshot overwrote the vault value (suppressed).");

        string[] flood = OperatingSystem.IsWindows()
            ? ["powershell", "-NoProfile", "-Command", "[Console]::Out.Write('x' * 65537)"]
            : ["sh", "-c", "head -c 65537 /dev/zero | tr '\\000' x"];
        string capped = await tools.WriteAsync("test", "capped", "command", flood);
        Assert.Contains("exceeded 65,536", capped, StringComparison.Ordinal);
        flood[^1] = OperatingSystem.IsWindows() ? "[Console]::Error.Write('x' * 65537)" : flood[^1] + " >&2";
        capped = await tools.WriteAsync("test", "capped", "command", flood);
        Assert.Contains("exceeded 65,536", capped, StringComparison.Ordinal);
        Assert.True(connected.CurrentValue == beforeStale);
    }

    private sealed class Provider(string? value = null) : IVaultProvider
    {
        private readonly Dictionary<string, string> _values = new()
        {
            ["test://deploy"] = value ?? string.Empty, ["test://second"] = value + "second",
            ["test://stage-a"] = value + "stage-a", ["test://stage-b"] = value + "stage-b",
        };
        private int _revision;
        public bool CanWrite { get; set; }
        public string CurrentValue => _values["test://deploy"];
        public int VersionReads { get; private set; }
        public int ChangeOnRead { get; set; }
        public bool BlockNextWrite { get; set; }
        public TaskCompletionSource WriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? NextVersionRead;
        public string Id => "test";

        public string DisplayName => "Test";

        public string ReferenceScheme => "test";

        public Task<VaultProviderStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new VaultProviderStatus(VaultProviderState.Connected));

        public Task<IReadOnlyList<VaultItem>> ListItemsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VaultItem>>([new VaultItem("test://deploy", "Deploy token"), new VaultItem("test://second", "Second token"),
                new VaultItem("test://stage-a", "Staged A"), new VaultItem("test://stage-b", "Staged B")]);

        public Task<string> ResolveAsync(string reference, CancellationToken cancellationToken) =>
            value is null ? throw new InvalidOperationException("The hub must not resolve while encryption is off.") : Task.FromResult(_values[reference]);

        public Task<VaultItemVersion> GetVersionAsync(string reference, CancellationToken cancellationToken)
        {
            if (++VersionReads == ChangeOnRead)
            {
                _revision++;
            }

            var version = new VaultItemVersion(reference, _revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Interlocked.Exchange(ref NextVersionRead, null)?.TrySetResult();
            return Task.FromResult(version);
        }

        public async Task WriteAsync(string name, string reference, string next, VaultItemVersion expected, CancellationToken cancellationToken)
        {
            if (BlockNextWrite)
            {
                BlockNextWrite = false;
                WriteEntered.TrySetResult();
                await ContinueWrite.Task.WaitAsync(cancellationToken);
            }

            if (expected != new VaultItemVersion(reference, _revision.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            {
                throw new VaultWriteConflictException();
            }

            _values[reference] = next;
            _revision++;
        }
    }
}
