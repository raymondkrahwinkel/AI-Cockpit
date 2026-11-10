using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.BackendApi;

// AC-2285: a project's files, or one of its worktrees' (`?worktree=<branch>`), by relative path only. Every opened
// file is checked by its handle, so a link swapped in after the path check is caught on the file actually used.
internal static class FilesEndpoint
{
    private const int MaxBytes = 1024 * 1024;
    private const int MaxEntries = 1000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan WriteWait = TimeSpan.FromSeconds(10);

    // ponytail: one lock for every API write on this server; per-path locks if concurrent saves ever queue noticeably.
    private static readonly SemaphoreSlim Writes = new(1, 1);

    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        api.MapGet("/projects/{id}/file", (Func<HttpContext, Task<IResult>>)(context => ReadAsync(context, services))).RequireOperate();
        api.MapGet("/projects/{id}/files", (Func<HttpContext, Task<IResult>>)(context => _ListAsync(context, services))).RequireOperate();
        api.MapPut("/projects/{id}/file", (Func<HttpContext, Task<IResult>>)(context => _WriteAsync(context, services)))
            .RequireOperate().Audited("write_project_file", services);
        api.MapPost("/projects/{id}/file/move", (Func<HttpContext, Task<IResult>>)(context => _MoveAsync(context, services)))
            .RequireOperate().Audited("move_project_file", services);
        api.MapDelete("/projects/{id}/file", (Func<HttpContext, Task<IResult>>)(context => _DeleteAsync(context, services)))
            .RequireOperate().Audited("delete_project_file", services);
    }

    // Null when the project is unknown or outside the calling key's scope, so both read as not found.
    internal static async Task<Project?> FindProjectAsync(HttpContext context, IServiceProvider services)
    {
        var caller = McpRequestContext.CurrentNodeCaller ?? throw new InvalidOperationException("A file route ran without a connect-key caller.");
        var id = context.Request.RouteValues["id"]?.ToString() ?? string.Empty;
        var project = await services.GetRequiredService<IProjectEditor>().FindProjectAsync(id).ConfigureAwait(false);
        return project is not null && caller.AllowsProject(id, services.GetRequiredService<INodePairingBroker>()) ? project : null;
    }

    // The key as it stands now, not as the door saw it: a revoke or a narrowed scope while a change waited stops it.
    internal static async Task<IResult?> RefuseIfNoLongerAllowedAsync(HttpContext context, IServiceProvider services)
    {
        var caller = McpRequestContext.CurrentNodeCaller;
        var id = context.Request.RouteValues["id"]?.ToString() ?? string.Empty;
        var keys = services.GetService<ConnectKeyVerifier>() is { } verifier
            ? await verifier.ListAsync(context.RequestAborted).ConfigureAwait(false)
            : [];
        var now = DateTimeOffset.UtcNow;
        return caller is { KeyPrefix: { } prefix } && keys.Any(entry => entry.Key.Prefix == prefix && entry.Key.IsUsableAt(now) &&
                entry.Key.EffectiveScope().AllowsProject(id))
            ? null
            : BackendApiRoutes.Error(StatusCodes.Status403Forbidden, "revoked", "The key was revoked or no longer reaches this project.");
    }

    internal static async Task<IResult> ReadAsync(HttpContext context, IServiceProvider services)
    {
        if (await _RootAsync(context, services).ConfigureAwait(false) is not { } root)
        {
            return Results.NotFound();
        }

        var relativePath = context.Request.Query["path"].ToString();
        if (_Resolve(root, relativePath, out var fullPath) is { } refused)
        {
            return refused;
        }

        if (Directory.Exists(fullPath))
        {
            return _InvalidPath("The path names a directory.");
        }

        if (!File.Exists(fullPath))
        {
            return Results.NotFound();
        }

        try
        {
            if (_OpenVerified(fullPath, root, relativePath) is not { } file)
            {
                return _NotARegularFile();
            }

            await using (file)
            {
                if (await _ReadAllAsync(file, context.RequestAborted).ConfigureAwait(false) is not { } bytes)
                {
                    return _TooLarge();
                }

                context.Response.Headers.ETag = $"\"{_Revision(bytes)}\"";
                try
                {
                    var text = StrictUtf8.GetString(bytes);
                    if (!text.Contains('\0'))
                    {
                        return Results.Bytes(bytes, "text/plain; charset=utf-8");
                    }
                }
                catch (DecoderFallbackException)
                {
                }

                return Results.Bytes(bytes, "application/octet-stream");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> _ListAsync(HttpContext context, IServiceProvider services)
    {
        if (await _RootAsync(context, services).ConfigureAwait(false) is not { } root)
        {
            return Results.NotFound();
        }

        var relativePath = context.Request.Query["path"].ToString() is { Length: > 0 } given ? given : ".";
        if (_Resolve(root, relativePath, out var fullPath) is { } refused)
        {
            return refused;
        }

        try
        {
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
            var entries = new DirectoryInfo(fullPath).EnumerateFileSystemInfos("*", options)
                .Where(entry => !_IsGit(entry.Name))
                .Take(MaxEntries + 1)
                .Select(entry => new
                {
                    name = entry.Name,
                    kind = entry is DirectoryInfo ? "directory" : "file",
                    size = entry is FileInfo file ? file.Length : (long?)null,
                    isLink = entry.LinkTarget is not null,
                })
                .ToList();

            // ponytail: a listing has no handle, so this re-check narrows the swap window instead of closing it; names
            // are all it can leak. An openat/fdopendir listing is the upgrade.
            if (!ProjectRootPath.TryResolve(root, relativePath, out var again, out _) || !again.Equals(fullPath, Comparison))
            {
                return _Moved();
            }

            return Results.Json(new { entries = entries.Take(MaxEntries), truncated = entries.Count > MaxEntries });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Results.NotFound();
        }
    }

    // Exactly one of `revision` (the version this save was based on) or `mustNotExist` (a new file) is required.
    private static async Task<IResult> _WriteAsync(HttpContext context, IServiceProvider services)
    {
        if (await _RootAsync(context, services).ConfigureAwait(false) is not { } root)
        {
            return Results.NotFound();
        }

        if (await _BodyAsync<FileWriteRequest>(context).ConfigureAwait(false) is not { Content: { } content } request ||
            (request.Revision is null) == (request.MustNotExist != true))
        {
            return _BadRequest("Send content with either revision or mustNotExist.");
        }

        if (content.Length > (MaxBytes + 2) / 3 * 4)
        {
            return _TooLarge();
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(content);
        }
        catch (FormatException)
        {
            return _BadRequest("content must be base64.");
        }

        if (bytes.Length > MaxBytes)
        {
            return _TooLarge();
        }

        var relativePath = context.Request.Query["path"].ToString();
        if (_Resolve(root, relativePath, out var fullPath) is { } refused)
        {
            return refused;
        }

        var temporaryPath = _Sibling(relativePath);
        if (_Resolve(root, temporaryPath, out var temporaryFull) is { } refusedFolder)
        {
            return refusedFolder;
        }

        return await _UnderWriteLockAsync(context, services, async () =>
        {
            if (Directory.Exists(fullPath))
            {
                return _InvalidPath("The path names a directory.");
            }

            try
            {
                string? revision = null;
                if (File.Exists(fullPath))
                {
                    if (_OpenVerified(fullPath, root, relativePath) is not { } current)
                    {
                        return _NotARegularFile();
                    }

                    // Closed again before the move: Windows will not replace a file this process still holds open.
                    await using (current)
                    {
                        if (await _ReadAllAsync(current, context.RequestAborted).ConfigureAwait(false) is not { } existing)
                        {
                            return _TooLarge();
                        }

                        revision = _Revision(existing);
                    }
                }

                if (revision != request.Revision)
                {
                    return _Conflict(revision);
                }

                // Held open across the move with FileShare.Delete: on Windows neither folder can be renamed meanwhile.
                await using var temporary = new FileStream(temporaryFull, FileMode.CreateNew, FileAccess.Write, FileShare.Delete);
                if (!_IsOpenedAt(temporary, root, temporaryPath))
                {
                    return _Moved();
                }

                await temporary.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
                temporary.Flush(flushToDisk: true);
                File.Move(temporaryFull, fullPath, overwrite: request.MustNotExist != true);
                return Results.Json(new { revision = _Revision(bytes) });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return request.MustNotExist == true && File.Exists(fullPath)
                    ? _Conflict(null)
                    : BackendApiRoutes.Error(StatusCodes.Status409Conflict, "write_failed", "The file could not be written.");
            }
            finally
            {
                _DeleteQuietly(temporaryFull);
            }
        }).ConfigureAwait(false);
    }

    // A rename of one file to a name that is free; the file must still be at `revision`.
    private static async Task<IResult> _MoveAsync(HttpContext context, IServiceProvider services)
    {
        if (await _RootAsync(context, services).ConfigureAwait(false) is not { } root)
        {
            return Results.NotFound();
        }

        if (await _BodyAsync<FileMoveRequest>(context).ConfigureAwait(false) is not { From: { } from, To: { } to, Revision: { } revision })
        {
            return _BadRequest("Send from, to and revision.");
        }

        if (_Resolve(root, from, out var fromFull) is { } refusedFrom)
        {
            return refusedFrom;
        }

        if (_Resolve(root, to, out var toFull) is { } refusedTo)
        {
            return refusedTo;
        }

        var markerPath = _Sibling(to);
        if (_Resolve(root, markerPath, out var markerFull) is { } refusedMarker)
        {
            return refusedMarker;
        }

        return await _UnderWriteLockAsync(context, services, async () =>
        {
            try
            {
                var (file, refused) = await _OpenAtRevisionAsync(fromFull, root, from, revision, context.RequestAborted).ConfigureAwait(false);
                if (file is null)
                {
                    return refused ?? Results.NotFound();
                }

                await using (file)
                {
                    if (File.Exists(toFull) || Directory.Exists(toFull))
                    {
                        return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "exists", "The new name is already taken.");
                    }

                    // The target folder pinned and checked through a handle of our own in it, as the source is by its own.
                    await using var marker = new FileStream(markerFull, FileMode.CreateNew, FileAccess.Write, FileShare.Delete, 1, FileOptions.DeleteOnClose);
                    if (!_IsOpenedAt(marker, root, markerPath))
                    {
                        return _Moved();
                    }

                    // ponytail: Windows keeps both folders pinned by these handles; Linux has no handle-relative rename in
                    // .NET, so there the server account must not outrank the project folder's writers (in Cockpit they are one).
                    File.Move(fromFull, toFull, overwrite: false);
                    return Results.Json(new { revision });
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "write_failed", "The file could not be renamed.");
            }
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> _DeleteAsync(HttpContext context, IServiceProvider services)
    {
        if (await _RootAsync(context, services).ConfigureAwait(false) is not { } root)
        {
            return Results.NotFound();
        }

        var relativePath = context.Request.Query["path"].ToString();
        if (context.Request.Query["revision"].ToString() is not { Length: > 0 } revision)
        {
            return _BadRequest("revision is required.");
        }

        if (_Resolve(root, relativePath, out var fullPath) is { } refusedPath)
        {
            return refusedPath;
        }

        return await _UnderWriteLockAsync(context, services, async () =>
        {
            try
            {
                var (file, refused) = await _OpenAtRevisionAsync(fullPath, root, relativePath, revision, context.RequestAborted).ConfigureAwait(false);
                if (file is null)
                {
                    return refused ?? Results.NotFound();
                }

                await using (file)
                {
                    // ponytail: as a move — the verified handle pins the folder on Windows; Linux relies on the same account.
                    File.Delete(fullPath);
                    return Results.Json(new { deleted = true });
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "write_failed", "The file could not be deleted.");
            }
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> _UnderWriteLockAsync(HttpContext context, IServiceProvider services, Func<Task<IResult>> change)
    {
        if (!await Writes.WaitAsync(WriteWait, context.RequestAborted).ConfigureAwait(false))
        {
            return BackendApiRoutes.Error(StatusCodes.Status503ServiceUnavailable, "busy", "Another change is still being written; try again.");
        }

        try
        {
            return await RefuseIfNoLongerAllowedAsync(context, services).ConfigureAwait(false) ?? await change().ConfigureAwait(false);
        }
        finally
        {
            Writes.Release();
        }
    }

    private static async Task<string?> _RootAsync(HttpContext context, IServiceProvider services)
    {
        if (await FindProjectAsync(context, services).ConfigureAwait(false) is not { SourceDirectory: { } source } project)
        {
            return null;
        }

        return context.Request.Query["worktree"].ToString() is { Length: > 0 } branch
            ? (await WorktreesEndpoints.FindAsync(project, branch, services, context.RequestAborted).ConfigureAwait(false))?.Path
            : source;
    }

    // Also keeps every route out of `.git`, whose config names programs the server's next git command would run.
    private static IResult? _Resolve(string root, string relativePath, out string fullPath)
    {
        if (!ProjectRootPath.TryResolve(root, relativePath, out fullPath, out var refusal))
        {
            return _InvalidPath(refusal ?? "Invalid path.");
        }

        return _HasGitSegment(root, fullPath) ? _InvalidPath("Paths inside .git are not served.") : null;
    }

    private static bool _HasGitSegment(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Any(_IsGit);

    private static bool _IsGit(string name) => name.Equals(".git", StringComparison.OrdinalIgnoreCase);

    private static string _Sibling(string relativePath) =>
        Path.Combine(Path.GetDirectoryName(relativePath) ?? string.Empty, $".cockpit-{Guid.NewGuid():N}.tmp");

    // The file opened and checked by its handle, or null when it is not a regular file or not where the path says.
    private static FileStream? _OpenVerified(string fullPath, string root, string relativePath)
    {
        // Checked before opening: a FIFO would hold the open until some writer showed up.
        if (!_IsRegularFile(fullPath))
        {
            return null;
        }

        var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (_IsOpenedAt(file, root, relativePath))
        {
            return file;
        }

        file.Dispose();
        return null;
    }

    // The source opened, verified and still at `revision`; the caller keeps it open while it changes the file.
    private static async Task<(FileStream? File, IResult? Refusal)> _OpenAtRevisionAsync(
        string fullPath, string root, string relativePath, string revision, CancellationToken cancellationToken)
    {
        if (!File.Exists(fullPath))
        {
            return (null, Results.NotFound());
        }

        if (_OpenVerified(fullPath, root, relativePath) is not { } file)
        {
            return (null, _NotARegularFile());
        }

        var bytes = await _ReadAllAsync(file, cancellationToken).ConfigureAwait(false);
        var current = bytes is null ? null : _Revision(bytes);
        if (current == revision)
        {
            return (file, null);
        }

        await file.DisposeAsync().ConfigureAwait(false);
        return (null, bytes is null ? _TooLarge() : _Conflict(current));
    }

    // Whether the handle's own final path is where the relative path resolves to now, inside the root and outside .git.
    private static bool _IsOpenedAt(FileStream file, string root, string relativePath)
    {
        try
        {
            var opened = ReplyAttachmentPath.OpenedPath(file);
            return ProjectRootPath.TryResolve(root, relativePath, out var expected, out _) &&
                opened.Equals(expected, Comparison) && !_HasGitSegment(root, opened);
        }
        catch (Exception error) when (error is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool _IsRegularFile(string fullPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return (File.GetAttributes(fullPath) & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) == 0;
        }

        // statx's layout is the same on every Linux architecture; stx_mode sits at byte 28.
        const int TypeMask = 0xF000, RegularType = 0x8000;
        var status = new byte[256];
        if (OperatingSystem.IsLinux())
        {
            return statx(-100, fullPath, 0x100, 1, status) == 0 && (BitConverter.ToUInt16(status, 28) & TypeMask) == RegularType;
        }

        return OperatingSystem.IsMacOS() && lstat(fullPath, status) == 0 && (BitConverter.ToUInt16(status, 4) & TypeMask) == RegularType;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int directory, string path, int flags, uint mask, byte[] status);

    [DllImport("libc", SetLastError = true)]
    private static extern int lstat(string path, byte[] status);

    private static async Task<byte[]?> _ReadAllAsync(FileStream file, CancellationToken cancellationToken)
    {
        if (file.Length > MaxBytes)
        {
            return null;
        }

        var buffer = new byte[MaxBytes + 1];
        var count = await file.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        return count > MaxBytes ? null : buffer[..count];
    }

    private static string _Revision(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<T?> _BodyAsync<T>(HttpContext context)
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<T>(context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return default;
        }
    }

    private static void _DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static IResult _InvalidPath(string description) =>
        BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_path", description);

    private static IResult _Moved() => _InvalidPath("The file changed outside the project root while it was opened.");

    private static IResult _NotARegularFile() => _InvalidPath("Only regular files inside the project root are served.");

    private static IResult _BadRequest(string description) =>
        BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", description);

    private static IResult _TooLarge() =>
        BackendApiRoutes.Error(StatusCodes.Status413PayloadTooLarge, "file_too_large", "The file exceeds 1 MiB.");

    private static IResult _Conflict(string? revision) =>
        Results.Json(new { error = "conflict", error_description = "The file changed since it was read; nothing was written.", revision },
            statusCode: StatusCodes.Status409Conflict);

    private sealed record FileWriteRequest(string? Content, string? Revision, bool? MustNotExist);

    private sealed record FileMoveRequest(string? From, string? To, string? Revision);
}
