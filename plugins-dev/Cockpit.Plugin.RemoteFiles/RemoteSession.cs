using System.Text;
using Cockpit.Plugin.RemoteFiles.Contracts;
using Cockpit.Plugins.Abstractions.Files;
using FluentFTP;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Cockpit.Plugin.RemoteFiles;

internal sealed record RemoteItem(string Name, bool Folder, long Size, DateTimeOffset Modified, string? ETag = null)
{
    public string Version => ETag ?? $"{Modified.UtcTicks}:{Size}";
}

// One open, signed-in connection; paths are absolute on the server.
internal interface IRemoteSession : IAsyncDisposable
{
    string Home { get; }
    Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken);
    Task<RemoteItem?> StatAsync(string path, CancellationToken cancellationToken);
    Task<byte[]> ReadAsync(string path, long offset, int count, CancellationToken cancellationToken);
    Task UploadNewAsync(string path, byte[] content, CancellationToken cancellationToken);
    // Never overwrites: false when the target already exists.
    Task<bool> MoveAsync(string from, string to, CancellationToken cancellationToken);
    Task DeleteAsync(string path, CancellationToken cancellationToken);
    Task CreateFolderAsync(string path, CancellationToken cancellationToken);
}

internal static class RemoteSession
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // A close that fails after the work is done must not turn a finished call into a failure.
    public static async Task CloseQuietlyAsync(IRemoteSession session)
    {
        try
        {

            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    // The trust check runs inside the handshake, so a refused key or certificate ends it before the login is sent.
    public static async Task<IRemoteSession> ConnectAsync(RemoteConnection connection, RemoteSecrets secrets, RemoteTrust trust,
        CancellationToken cancellationToken)
    {
        PrivateKeyFile? key = null;
        IDisposable? client = null;
        try
        {
            if (connection.Kind == RemoteFilesChannel.WebDav)
            {
                return new WebDavSession(connection, secrets, trust);
            }
            if (connection.Kind == RemoteFilesChannel.Sftp)
            {
                key = connection.UseKey ? _Key(secrets) : null;
                AuthenticationMethod auth = key is not null
                    ? new PrivateKeyAuthenticationMethod(connection.User, key)
                    : new PasswordAuthenticationMethod(connection.User, secrets.Password ?? "");
                var sftp = new SftpClient(new ConnectionInfo(connection.Host, connection.Port, connection.User, auth) { Timeout = Timeout });
                client = sftp;
                sftp.HostKeyReceived += trust.OnHostKey;
                await sftp.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return new Sftp(sftp, key);
            }
            var ftp = new AsyncFtpClient(connection.Host, connection.User, secrets.Password ?? "", connection.Port, new FtpConfig
            {
                EncryptionMode = connection.Kind == RemoteFilesChannel.Ftps ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None,
                DataConnectionEncryption = true,
                ValidateAnyCertificate = false,
                ConnectTimeout = (int)Timeout.TotalMilliseconds,
                ReadTimeout = (int)Timeout.TotalMilliseconds,
                DataConnectionConnectTimeout = (int)Timeout.TotalMilliseconds,
                DataConnectionReadTimeout = (int)Timeout.TotalMilliseconds,
            });
            client = ftp;
            ftp.ValidateCertificate += trust.OnCertificate;
            await ftp.Connect(cancellationToken).ConfigureAwait(false);
            return new Ftp(ftp, await ftp.GetWorkingDirectory(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception error)
        {
            client?.Dispose();
            key?.Dispose();
            if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            throw trust.Refused ? trust.Refusal() : RemoteTrust.Translate(error);
        }
    }

    private static PrivateKeyFile _Key(RemoteSecrets secrets)
    {
        if (string.IsNullOrWhiteSpace(secrets.Key))
        {
            throw new FileSourceException(FileSourceErrorKind.SignIn, "No private key is stored for this connection.");
        }
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(secrets.Key));
            return string.IsNullOrEmpty(secrets.Passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, secrets.Passphrase);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new FileSourceException(FileSourceErrorKind.SignIn, "The private key could not be read. Check the key and its passphrase.");
        }
    }

    private sealed class Sftp(SftpClient client, PrivateKeyFile? key) : IRemoteSession
    {
        public string Home => client.WorkingDirectory;

        public async Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken)
        {
            var items = new List<RemoteItem>();
            await foreach (var file in client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (file.Name is "." or "..")
                {
                    continue;
                }
                // A link is shown as what it points to; a dangling one is left out instead of failing the folder.
                var attributes = file.IsSymbolicLink ? await _TryStat(file.FullName, cancellationToken).ConfigureAwait(false) : file.Attributes;
                if (attributes is null)
                {
                    continue;
                }
                items.Add(new RemoteItem(file.Name, attributes.IsDirectory, attributes.Size, attributes.LastWriteTimeUtc));
            }
            return items;
        }

        public async Task<RemoteItem?> StatAsync(string path, CancellationToken cancellationToken) =>
            await _TryStat(path, cancellationToken).ConfigureAwait(false) is { } attributes
                ? new RemoteItem(path[(path.LastIndexOf('/') + 1)..], attributes.IsDirectory, attributes.Size, attributes.LastWriteTimeUtc)
                : null;

        private async Task<Renci.SshNet.Sftp.SftpFileAttributes?> _TryStat(string path, CancellationToken cancellationToken)
        {
            try
            {
                return await client.GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (SftpPathNotFoundException)
            {
                return null;
            }
        }

        public async Task<byte[]> ReadAsync(string path, long offset, int count, CancellationToken cancellationToken)
        {
            await using var stream = await client.OpenAsync(path, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[count];
            var read = 0;
            while (read < count && await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false) is var n and > 0)
            {
                read += n;
            }
            return buffer[..read];
        }

        public async Task UploadNewAsync(string path, byte[] content, CancellationToken cancellationToken)
        {
            await using var stream = await client.OpenAsync(path, FileMode.CreateNew, FileAccess.Write, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        }

        // A plain SFTP rename refuses an existing target, which makes it the "must not exist" check.
        public async Task<bool> MoveAsync(string from, string to, CancellationToken cancellationToken)
        {
            try
            {
                await client.RenameFileAsync(from, to, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (SshException)
            {
                if (await _TryStat(to, cancellationToken).ConfigureAwait(false) is not null)
                {
                    return false;
                }
                throw;
            }
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken) => client.DeleteFileAsync(path, cancellationToken);

        public Task CreateFolderAsync(string path, CancellationToken cancellationToken) => client.CreateDirectoryAsync(path, cancellationToken);

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            key?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Ftp(AsyncFtpClient client, string home) : IRemoteSession
    {
        public string Home => home;

        public async Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken) =>
            [.. (await client.GetListing(path, cancellationToken).ConfigureAwait(false))
                .Where(item => item.Name is not ("." or ".."))
                .Select(item => new RemoteItem(item.Name, item.Type == FtpObjectType.Directory, Math.Max(item.Size, 0), _Utc(item.Modified)))];

        public async Task<RemoteItem?> StatAsync(string path, CancellationToken cancellationToken)
        {
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (await client.FileExists(path, cancellationToken).ConfigureAwait(false))
            {
                return new RemoteItem(name, false, await client.GetFileSize(path, -1, cancellationToken).ConfigureAwait(false),
                    _Utc(await client.GetModifiedTime(path, cancellationToken).ConfigureAwait(false)));
            }
            return await client.DirectoryExists(path, cancellationToken).ConfigureAwait(false) ? new RemoteItem(name, true, 0, DateTimeOffset.MinValue) : null;
        }

        public Task<byte[]> ReadAsync(string path, long offset, int count, CancellationToken cancellationToken) =>
            client.DownloadBytes(path, offset, null, cancellationToken, offset + count);

        public async Task UploadNewAsync(string path, byte[] content, CancellationToken cancellationToken)
        {
            if (await client.UploadBytes(content, path, FtpRemoteExists.Skip, false, null, cancellationToken).ConfigureAwait(false) != FtpStatus.Success)
            {
                throw new IOException("Upload failed.");
            }
        }

        public Task<bool> MoveAsync(string from, string to, CancellationToken cancellationToken) =>
            client.MoveFile(from, to, FtpRemoteExists.Skip, cancellationToken);

        public Task DeleteAsync(string path, CancellationToken cancellationToken) => client.DeleteFile(path, cancellationToken);

        public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
        {
            if (!await client.CreateDirectory(path, false, cancellationToken).ConfigureAwait(false))
            {
                throw new IOException("Folder not created.");
            }
        }

        private static DateTimeOffset _Utc(DateTime time) =>
            time == DateTime.MinValue ? DateTimeOffset.MinValue : new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc));

        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
