using System.Text.Json;

namespace Cockpit.Plugin.RemoteFiles.Contracts;

// Secrets only travel towards the backend; every answer says no more than whether one is set.
public static class RemoteFilesChannel
{
    public const string Connections = "remote-files-connections";
    public const string Save = "remote-files-save";
    public const string Test = "remote-files-test";

    public const string Sftp = "sftp";
    public const string Ftps = "ftps";
    public const string Ftp = "ftp";

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    public static int DefaultPort(string kind) => kind == Sftp ? 22 : 21;
}

// Fingerprint is "SHA256:<base64>" for a host key, colon-hex SHA-256 for a certificate.
public sealed record RemotePin(string Algorithm, string Fingerprint, DateTimeOffset Trusted);

public sealed record RemoteConnection(string Id, string Name, string Kind, string Host, int Port, string User,
    bool UseKey = false, bool ReadOnly = true, RemotePin? Pin = null);

public sealed record RemoteSecretsSet(bool Password, bool Key, bool Passphrase);

public sealed record RemoteConnectionsAnswer(IReadOnlyList<RemoteConnection> Connections,
    IReadOnlyDictionary<string, RemoteSecretsSet> Secrets, string Machine, string? Error = null);

// Null keeps what is stored; an empty string clears it. ToString says only which are set, never the values.
public sealed record RemoteSecrets(string? Password = null, string? Key = null, string? Passphrase = null)
{
    public override string ToString() =>
        $"RemoteSecrets {{ Password = {_Set(Password)}, Key = {_Set(Key)}, Passphrase = {_Set(Passphrase)} }}";

    private static string _Set(string? value) => value is null ? "unset" : "set";
}

public sealed record RemoteSaveRequest(IReadOnlyList<RemoteConnection> Connections, IReadOnlyDictionary<string, RemoteSecrets>? Secrets);

public sealed record RemoteTestRequest(RemoteConnection Connection, RemoteSecrets? Secrets);

public enum RemoteTestStatus { Connected, Untrusted, Changed, Failed }

public sealed record RemotePresented(string Algorithm, string Fingerprint, string? Subject = null, string? Issuer = null,
    DateTimeOffset? NotAfter = null);

public sealed record RemoteTestAnswer(RemoteTestStatus Status, RemotePresented? Presented, string? Message);
