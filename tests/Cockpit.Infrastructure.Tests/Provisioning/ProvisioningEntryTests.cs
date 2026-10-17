using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Provisioning;
using Cockpit.Infrastructure.Provisioning;
using Cockpit.Infrastructure.Tests.ManagedCli;

namespace Cockpit.Infrastructure.Tests.Provisioning;

// AC-1505 (DB-2.5a): the provisioning way in, driven through `ProvisioningEntry.RunAsync` against an in-process
// HTTPS "server". The manifest is the organisation example of manifest-v1 §9.2, the one that carries every section.
public sealed class ProvisioningEntryTests : IDisposable
{
    private const string Token = "s3cr3t-0ne-time-T0KEN";

    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), $"cockpit-provision-{Guid.NewGuid():N}");

    private readonly List<string> _logs = [];

    public ProvisioningEntryTests() => Directory.CreateDirectory(_stateRoot);

    public void Dispose() => Directory.Delete(_stateRoot, recursive: true);

    [Fact]
    public async Task A_manifest_yields_every_section_and_never_takes_over_an_unknown_field()
    {
        var document = JsonNode(OrgManifest);
        document["smuggled"] = new System.Text.Json.Nodes.JsonObject { ["run"] = "anything" };
        document["profiles"]!["items"]![0]!["environment"]![0]!["value"] = @"C:\Users\sanne\acme";

        var outcome = await _RunAsync("https://acme.example/manifest", document.ToJsonString());

        Assert.Null(outcome!.Failure);
        var manifest = outcome.Manifest!;
        Assert.Equal(("acme", 31L), (manifest.Issuer!.Organization!.Id, manifest.Revision!.Value));
        Assert.Equal(new[] { "depot", "youtrack" }, manifest.Plugins!.Required!.Select(plugin => plugin.Id));
        Assert.Equal("token-required", manifest.Stores!.Allowed![1].Auth);
        Assert.Equal(("locked", "org"), (manifest.Settings!["updates.channel"].Mode, manifest.Settings["updates.channel"].By));
        Assert.Equal("ACME_API_KEY", manifest.Profiles!.Items![0].Environment![1].Name);
        Assert.Equal("handboek", manifest.Projects!.Shared![1].Slug);
        Assert.Equal("Assistentregels Acme", manifest.Assistant!.OrgRules!.Title);
        Assert.Equal("forbidden", manifest.Policy!.Permissions!.TerminalAccess);
        Assert.Equal(12, manifest.Backup!.Schedule!.IntervalHours);
        Assert.Equal("exchange-provision-token", manifest.Connections![0].Config!.Value.GetProperty("credential").GetString());
        Assert.Equal("ontwikkelen", manifest.FirstRun!.WorkKind);
        Assert.Equal(2, manifest.Notes.Count);

        // Counterproof: the unknown field is reported and absent from the object; the path is stripped, not carried.
        Assert.Contains(outcome.Skipped, note => note is { Path: "smuggled", Reason: ProvisionManifestReader.SkippedUnknown });
        Assert.Contains(outcome.Skipped, note => note is { Path: "profiles.items[0].environment[0].value", Reason: ProvisionManifestReader.SkippedNotPortable });
        var carried = JsonSerializer.Serialize(manifest);
        Assert.DoesNotContain("smuggled", carried);
        Assert.DoesNotContain(@"sanne\\acme", carried);

        var enforced = JsonNode(OrgManifest);
        enforced["policy"]!["permissions"]!["allowEverything"] = true;
        Assert.Contains("policy.permissions.allowEverything", (await _RunAsync("https://acme.example/manifest", enforced.ToJsonString()))!.Failure);

        var newer = JsonNode(OrgManifest);
        newer["formatVersion"] = 2;
        Assert.Contains("update Cockpit", (await _RunAsync("https://acme.example/manifest", newer.ToJsonString()))!.Failure);

        var fetched = false;
        var plain = await ProvisioningEntry.RunAsync("http://acme.example/manifest", false, _stateRoot, _Logger(), new StubHttpMessageHandler(_ =>
        {
            fetched = true;
            return _Manifest(OrgManifest);
        }));
        Assert.Contains("https://", plain!.Failure);
        Assert.False(fetched);
    }

    [Fact]
    public async Task The_one_time_token_shows_up_in_no_log_message_file_or_argument_trail()
    {
        var url = $"https://depot.acme.example/cockpit/manifest?t={Token}";
        var outcomes = new List<ProvisioningOutcome?>
        {
            await ProvisioningEntry.RunAsync(url, false, _stateRoot, _Logger(), new StubHttpMessageHandler(request =>
                throw new HttpRequestException($"Connection refused ({request.RequestUri})"))),
            await _RunAsync(url, OrgManifest),
            await _RunAsync(url, OrgManifest),
        };

        Assert.Contains("could not be fetched", outcomes[0]!.Failure);
        Assert.NotNull(outcomes[1]!.Manifest);
        Assert.Contains("already been used", outcomes[2]!.Failure);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Token)));
        Assert.Contains(hash, await File.ReadAllTextAsync(Path.Combine(_stateRoot, "provision-tokens-used.txt")));

        var trail = _logs
            .Concat(outcomes.Select(outcome => outcome!.Failure ?? string.Empty))
            .Concat(ProvisioningEntry.RedactArguments(["--provision", url, "--safe-mode"]))
            .Concat(Directory.EnumerateFiles(_stateRoot, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.All(trail, line => Assert.DoesNotContain(Token, line));
        Assert.Contains(_logs, line => line.Contains("https://depot.acme.example/cockpit/manifest", StringComparison.Ordinal));
    }

    private Task<ProvisioningOutcome?> _RunAsync(string url, string json) =>
        ProvisioningEntry.RunAsync(url, false, _stateRoot, _Logger(), new StubHttpMessageHandler(_ => _Manifest(json)));

    private static HttpResponseMessage _Manifest(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/vnd.cockpit.provision+json") };

    private static System.Text.Json.Nodes.JsonNode JsonNode(string json) => System.Text.Json.Nodes.JsonNode.Parse(json)!;

    private ILogger _Logger() => new _CollectingLogger(_logs);

    private sealed class _CollectingLogger(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add($"{formatter(state, exception)} {exception}");
    }

    private const string OrgManifest = """
        {
          "format": "cockpit.provision", "formatVersion": 1, "requires": [],
          "manifestId": "acme:ontwikkelaars:u-456", "revision": 31, "issuedAt": "2026-10-17T10:00:00Z",
          "issuer": { "kind": "depot",
                      "organization": { "id": "acme", "name": "Acme BV" },
                      "installationSet": { "id": "ontwikkelaars", "name": "Ontwikkelaars" },
                      "profile": { "id": "u-456", "name": "Sanne" } },
          "plugins": {
            "required":  [ { "id": "depot", "minVersion": "0.12.0", "store": "cockpit-public" },
                           { "id": "youtrack", "minVersion": "1.2.0", "store": "acme-intern" } ],
            "allowed":   [ "depot", "youtrack", "git-status", "github-pull-requests", "local-ci", "transcript-search" ],
            "forbidden": [ "proxmox", "docker" ],
            "selected":  [ { "id": "git-status" } ] },
          "stores": { "allowed": [ { "id": "cockpit-public", "url": "https://store.cockpit.example/", "auth": "none" },
                                   { "id": "acme-intern", "url": "https://plugins.acme.nl/", "auth": "token-required" } ],
                      "allowUserStores": false },
          "settings": {
            "updates.channel":       { "value": "stable", "mode": "locked",  "by": "org" },
            "screenLock.lockWithOs": { "value": true,     "mode": "locked",  "by": "org" },
            "layout.themeMode":      { "value": "Light",  "mode": "default", "by": "profile" },
            "terminal.fontSize":     { "value": 12,       "mode": "default", "by": "org" }
          },
          "profiles": { "items": [
              { "id": "acme-dev", "label": "Acme ontwikkelen", "mode": "locked", "by": "set",
                "provider": { "id": "claude-provider", "model": "claude-sonnet-5-5" },
                "defaults": { "permissionMode": "acceptEdits", "readingLevel": "Developer", "autoApproveTools": false },
                "environment": [ { "name": "ACME_ENV", "value": "dev" }, { "name": "ACME_API_KEY", "secret": true } ] } ],
            "assistantProfile": { "label": "Acme ontwikkelen", "replacesStandingInstruction": false, "mode": "locked", "by": "org" },
            "defaultProfile": "Acme ontwikkelen" },
          "projects": { "shared": [ { "connection": "acme-depot", "org": "acme", "slug": "webshop", "by": "set" },
                                    { "connection": "acme-depot", "org": "acme", "slug": "handboek", "by": "org" } ] },
          "assistant": { "orgRules": { "by": "org", "title": "Assistentregels Acme",
                                       "revision": "sha256:3f1c9a…",
                                       "content": "# Acme\n- Schrijf klantcommunicatie altijd als concept.\n- Noem nooit klantnamen in commitberichten.\n" } },
          "policy": { "by": "org",
            "providers": { "allowed": [ { "id": "claude-provider", "models": [ "claude-sonnet-5-5", "claude-haiku-5-5" ] } ] },
            "permissions": { "maxPermissionMode": "acceptEdits", "autoApproveTools": "forbidden",
                             "delegationCeiling": "acceptEdits", "terminalAccess": "forbidden", "shellAccess": "allowed",
                             "neverSend": true },
            "backup": { "required": true, "maxIntervalHours": 24, "minKeep": 7, "allowedTargets": [ "depot" ],
                        "includeCredentials": "forbidden" },
            "account": { "require2fa": true } },
          "backup": { "target": { "connection": "acme-depot", "by": "org" },
                      "schedule": { "intervalHours": 12, "keep": 14, "by": "profile" }, "restoreFrom": "latest" },
          "connections": [ { "id": "acme-depot", "plugin": "depot", "required": true,
                             "config": { "url": "https://depot.acme.nl", "credential": "exchange-provision-token" } } ],
          "firstRun": { "workKind": "ontwikkelen", "readingLevel": "Developer", "simpleView": false },
          "notes": [ { "path": "settings.updates.channel", "reason": "locked", "by": "org",
                       "detail": "je profielwaarde 'preview' geldt niet: beheerd door Acme BV" },
                     { "path": "plugins.selected.docker", "reason": "forbidden", "by": "org" } ]
        }
        """;
}
