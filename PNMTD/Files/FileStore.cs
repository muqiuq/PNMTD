using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Knowledge;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Files;

/// <summary>The codes of the files' ProblemDetails (docs/locations-knowledge-plan.md §9).</summary>
public static class FileErrors
{
    /// <summary>413: over the kind's limit, or an image over 10,000 pixels a side or 40 megapixels.</summary>
    public const string TooLarge = "too_large";

    /// <summary>415: not a PNG, JPEG, GIF or WebP by its bytes, or markup in it.</summary>
    public const string NotAnImage = "not_an_image";

    /// <summary>422: an attachment with a blocked extension, double ones included.</summary>
    public const string BlockedType = "blocked_type";

    /// <summary>429: KnowledgeBase:UploadsPerHour used up.</summary>
    public const string TooManyUploads = "too_many_uploads";

    /// <summary>503: the folder is missing or not writable.</summary>
    public const string StorageUnavailable = "file_storage_unavailable";
}

/// <summary>Who stores or deletes a file, for its row and the auth log.</summary>
public sealed record FileActor(Guid? UserId, string? Username, string? DisplayName, AuditContext Audit);

/// <summary>
/// The files folder (docs/locations-knowledge-plan.md §5.5, §8.5): a folder on the Docker host, bind-mounted at
/// <c>KnowledgeBase:FilesPath</c>; PostgreSQL holds only what each file is (<c>files</c>).
/// <list type="bullet">
/// <item><b>Writing</b> streams the body into <c>.tmp/</c>, hashing (SHA-256) and counting on the way, cut off at the kind's
///   limit. An image is then checked and stripped (<see cref="ImageCheck"/>) into a second temporary file.</item>
/// <item><b>Once per content:</b> the same bytes return the file stored already, a deleted one still on disk revived.</item>
/// <item><b>Otherwise the file moves into place</b>, an atomic rename within the folder after a flush to disk, as
///   <c>yyyy/mm/{id}-{slug}.{ext}</c> (0640, folders 0750); then its row. If the row fails, the file goes again.</item>
/// <item><b>Paths</b> come from the id and the slug only. Reading checks that a path lies inside the folder and that nothing
///   from the folder down is a symbolic link.</item>
/// <item>Files are never changed after writing.</item>
/// </list>
/// </summary>
public sealed partial class FileStore(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<KnowledgeBaseOptions> options, ILogger<FileStore> logger)
{
    public const string TempFolder = ".tmp";

    /// <summary>The image's user <c>app</c>, who must own the folder on the Docker host (§8.5).</summary>
    public const int AppUid = 1654;

    private const UnixFileMode FolderPermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    private const UnixFileMode FilePermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>The folder, as an absolute path.</summary>
    public string Root => Path.GetFullPath(options.CurrentValue.FilesPath);

    public long LimitFor(FileKind kind) => kind == FileKind.Image ? options.CurrentValue.MaxImageBytes : options.CurrentValue.MaxAttachmentBytes;

    /// <summary>Stores an upload's body and returns the file: a new one, or the one with the same bytes.</summary>
    public async Task<FileItem> StoreAsync(Stream body, FileKind kind, string? name, string? contentType, FileActor actor, CancellationToken ct)
    {
        var fileName = CleanName(name);
        // Every kind: an image's name is replaced by its detected type's extension below, but a name such as invoice.cmd
        // says what the uploader meant (§8.6).
        if (BlockedExtension(fileName, options.CurrentValue.BlockedExtensionSet) is { } blocked)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, FileErrors.BlockedType,
                $"Files ending in .{blocked} can't be uploaded (KnowledgeBase:BlockedExtensions).");
        }
        var temp = TempDirectory();
        var upload = Path.Combine(temp, $"{Guid.NewGuid():N}.upload");
        var stored = upload;
        try
        {
            var limit = LimitFor(kind);
            var (size, sha) = await WriteLimitedAsync(body, upload, limit, ct);
            ImageInfo? image = null;
            if (kind == FileKind.Image)
            {
                var (stripped, info, refusal) = ImageCheck.Check(await File.ReadAllBytesAsync(upload, ct));
                if (refusal is not null)
                {
                    throw refusal.TooLarge
                        ? new ApiException(StatusCodes.Status413PayloadTooLarge, FileErrors.TooLarge, refusal.Message)
                        : new ApiException(StatusCodes.Status415UnsupportedMediaType, FileErrors.NotAnImage, refusal.Message);
                }
                image = info!;
                fileName = FileNames.ImageName(fileName, image.Extension);
                stored = Path.Combine(temp, $"{Guid.NewGuid():N}.stripped");
                await WriteAllAsync(stored, stripped!, ct);
                File.Delete(upload);
                size = stripped!.Length;
                sha = Convert.ToHexStringLower(SHA256.HashData(stripped));
            }
            if (await ExistingAsync(sha, stored, image, ct) is { } existing)
            {
                return existing;
            }
            var now = Clock.UtcNow;
            var id = Guid.CreateVersion7(now);
            var slug = FileNames.Slug(fileName);
            if (image is not null)
            {
                var dot = slug.LastIndexOf('.');
                slug = $"{(dot > 0 ? slug[..dot] : slug)}.{image.Extension}";
            }
            var relative = $"{now:yyyy}/{now:MM}/{id}-{slug}";
            var target = Path.Combine(Root, relative);
            CreateFolder(Path.GetDirectoryName(target)!);
            File.Move(stored, target, overwrite: false);
            stored = target;
            var file = new FileEntity
            {
                Id = id, Kind = kind, Name = fileName, ContentType = image?.ContentType ?? AttachmentType(contentType, fileName), Size = size,
                Width = image?.Width, Height = image?.Height, Sha256 = sha, Path = relative, CreatedAt = now, CreatedBy = actor.UserId,
                CreatedByName = actor.DisplayName ?? actor.Username,
            };
            try
            {
                await using var db = await dbs.CreateDbContextAsync(ct);
                db.Files.Add(file);
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // The same bytes, stored by someone else a moment ago.
                File.Delete(target);
                if (await ExistingAsync(sha, null, image, ct) is { } raced)
                {
                    return raced;
                }
                throw;
            }
            catch
            {
                File.Delete(target);
                throw;
            }
            stored = "";
            logger.LogInformation("Stored {Kind} {Id} ({Size} bytes) for {User}", kind, id, size, actor.Username);
            return FileNames.Item(file);
        }
        finally
        {
            DeleteQuietly(upload);
            if (stored.Length > 0 && stored != upload && stored.StartsWith(temp, StringComparison.Ordinal))
            {
                DeleteQuietly(stored);
            }
        }
    }

    /// <summary>A file's place on disk, or null: missing, outside the folder, or a symbolic link on the way (§8.6).</summary>
    public string? Locate(string relativePath)
    {
        var root = Root.TrimEnd(Path.DirectorySeparatorChar);
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, relativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }
        var file = new FileInfo(full);
        if (!file.Exists || file.LinkTarget is not null)
        {
            return null;
        }
        for (var folder = file.Directory; folder is not null && folder.FullName.Length > root.Length; folder = folder.Parent)
        {
            if (folder.LinkTarget is not null)
            {
                return null;
            }
        }
        return full;
    }

    /// <summary>
    /// An Admin's delete (§5.5, §10.6): off the disk and out of the database at once, pages showing <i>File removed</i>
    /// from then on, and <c>FileDeleted</c> in the auth log. False when there is no such file.
    /// </summary>
    public async Task<bool> DeleteNowAsync(Guid id, FileActor actor, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var file = await db.Files.SingleOrDefaultAsync(f => f.Id == id, ct);
        if (file is null)
        {
            return false;
        }
        RemoveFromDisk(file);
        db.Files.Remove(file);
        var who = actor.Audit.Origin == "cli" ? AuditWho.Cli
            : new AuditWho(AuditActor.User, actor.UserId, actor.Username ?? "", null, actor.Audit.IpAddress, actor.Audit.UserAgent);
        AuditLog.Record(db, AuditEvents.FileDeleted, $"{file.Name} ({file.Kind}, {file.Size} bytes)", who,
            AuditTarget.Of(AuditTargets.File, file.Id, file.Name));
        await db.SaveChangesAsync(ct);
        logger.LogWarning("File {Id} ({Name}) deleted by {User}", file.Id, file.Name, actor.Username);
        return true;
    }

    /// <summary>Removes a file's bytes; a file already gone is fine. Never anything outside the folder.</summary>
    public void RemoveFromDisk(FileEntity file)
    {
        if (Locate(file.Path) is { } full)
        {
            File.Delete(full);
        }
    }

    /// <summary>Whether the folder exists and a file can be written into it: what start, info.sh and the sensor report.</summary>
    public string? Problem()
    {
        var root = Root;
        if (!Directory.Exists(root))
        {
            return $"The files folder {root} doesn't exist: is it mounted? deploy/docker-compose.yml mounts PNMTD_FILES_DIR (./files) there.";
        }
        try
        {
            var probe = Path.Combine(TempDirectory(), $"{Guid.NewGuid():N}.probe");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What the operator does about it (deploy/README.md, Uploaded files): an existing installation updated by hand may lack
            // the mount, or Docker made the folder as root.
            return $"The files folder {root} isn't writable by PNMTD's user {Environment.UserName}: {ex.Message.TrimEnd('.')}. "
                + $"On the Docker host it must be mounted (deploy/docker-compose.yml) and belong to uid {AppUid}: "
                + $"sudo chown {AppUid}:{AppUid} deploy/files (or PNMTD_FILES_DIR); ./info.sh checks it.";
        }
    }

    /// <summary><c>.tmp/</c> inside the folder, so the move into place is a rename on the same file system.</summary>
    public string TempDirectory()
    {
        var root = Root;
        if (!Directory.Exists(root))
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, FileErrors.StorageUnavailable,
                "The files folder is missing: uploads are off until it is mounted again.");
        }
        var temp = Path.Combine(root, TempFolder);
        try
        {
            CreateFolder(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, FileErrors.StorageUnavailable,
                "The files folder isn't writable: uploads are off until it is fixed.");
        }
        return temp;
    }

    /// <summary>
    /// The file with these bytes: a live one, or one marked deleted but still on disk, which is live again. A live one whose
    /// bytes went missing from the folder gets them back from <paramref name="upload"/>: they are the same. For an image
    /// (<paramref name="image"/>), an attachment with these bytes becomes the image: they passed the check just now, and
    /// were stripped already, as they are the stripped bytes. An image serves an attachment's upload as it is.
    /// </summary>
    private async Task<FileItem?> ExistingAsync(string sha, string? upload, ImageInfo? image, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var candidates = await db.Files.AsNoTracking().Where(f => f.Sha256 == sha)
            .OrderBy(f => f.DeletedAt != null).ThenByDescending(f => f.CreatedAt).ToListAsync(ct);
        foreach (var candidate in candidates)
        {
            var file = candidate;
            if (Locate(file.Path) is null)
            {
                if (file.DeletedAt is null && upload is not null && Restore(file, upload))
                {
                    logger.LogWarning("File {Id} was missing from the folder; restored from an upload of the same bytes", file.Id);
                    return FileNames.Item(await AsImageAsync(db, file, image, ct));
                }
                continue;
            }
            if (file.DeletedAt is not null)
            {
                if (candidates.Any(c => c.DeletedAt is null))
                {
                    continue;
                }
                // In one statement: the cleanup deletes the row first, so either it is still there to revive, or gone.
                var id = file.Id;
                await db.Files.Where(f => f.Id == id && f.DeletedAt != null).ExecuteUpdateAsync(u => u.SetProperty(f => f.DeletedAt, (DateTime?)null), ct);
                if (await db.Files.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct) is not { DeletedAt: null } revived)
                {
                    continue;
                }
                file = revived;
            }
            return FileNames.Item(await AsImageAsync(db, file, image, ct));
        }
        return null;
    }

    /// <summary>An attachment with an image's bytes, asked for as that image (§5.5): the row becomes the image, once.</summary>
    private static async Task<FileEntity> AsImageAsync(PnmtdDbContext db, FileEntity file, ImageInfo? image, CancellationToken ct)
    {
        if (image is null || file.Kind != FileKind.Attachment)
        {
            return file;
        }
        var id = file.Id;
        var name = FileNames.ImageName(file.Name, image.Extension);
        await db.Files.Where(f => f.Id == id && f.Kind == FileKind.Attachment).ExecuteUpdateAsync(u => u
            .SetProperty(f => f.Kind, FileKind.Image).SetProperty(f => f.ContentType, image.ContentType)
            .SetProperty(f => f.Width, image.Width).SetProperty(f => f.Height, image.Height).SetProperty(f => f.Name, name), ct);
        return await db.Files.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct) ?? file;
    }

    /// <summary>Moves the bytes to a live file's own path, when that path is inside the folder and free.</summary>
    private bool Restore(FileEntity file, string upload)
    {
        var root = Root.TrimEnd(Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(root, file.Path));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || File.Exists(target) || Directory.Exists(target))
        {
            return false;
        }
        CreateFolder(Path.GetDirectoryName(target)!);
        File.Move(upload, target, overwrite: false);
        return Locate(file.Path) is not null;
    }

    /// <summary>The body into <paramref name="path"/>, hashed and counted; over <paramref name="limit"/> it is cut off with 413.</summary>
    private static async Task<(long Size, string Sha256)> WriteLimitedAsync(Stream body, string path, long limit, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        await using (var output = new FileStream(path, NewFile()))
        {
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                size += read;
                if (size > limit)
                {
                    throw new ApiException(StatusCodes.Status413PayloadTooLarge, FileErrors.TooLarge,
                        $"The file is larger than {limit / (1024.0 * 1024):0.#} MB.");
                }
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            await output.FlushAsync(ct);
            output.Flush(flushToDisk: true);
        }
        if (size == 0)
        {
            throw ApiException.Invalid("body", "The file is empty.");
        }
        return (size, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static async Task WriteAllAsync(string path, byte[] bytes, CancellationToken ct)
    {
        await using var output = new FileStream(path, NewFile());
        await output.WriteAsync(bytes, ct);
        await output.FlushAsync(ct);
        output.Flush(flushToDisk: true);
    }

    private static FileStreamOptions NewFile()
    {
        var o = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows())
        {
            o.UnixCreateMode = FilePermissions;
        }
        return o;
    }

    private static void CreateFolder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, FolderPermissions);
        }
    }

    private void DeleteQuietly(string path)
    {
        try
        {
            if (path.Length > 0)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not remove the temporary file {Path}: {Error}", path, ex.Message);
        }
    }

    /// <summary>The name as sent, without a path, control characters or trailing dots and spaces; at most 255 characters.</summary>
    internal static string CleanName(string? name)
    {
        var cleaned = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray());
        cleaned = cleaned[(cleaned.LastIndexOfAny(['/', '\\']) + 1)..].Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0)
        {
            throw ApiException.Invalid("name", "A file name is required.");
        }
        if (cleaned.Length > 255)
        {
            var dot = cleaned.LastIndexOf('.');
            var extension = dot > 0 && cleaned.Length - dot <= 16 ? cleaned[dot..] : "";
            cleaned = cleaned[..(255 - extension.Length)] + extension;
        }
        return cleaned;
    }

    /// <summary>The first blocked extension among a name's: <c>report.pdf.exe</c> and <c>setup.exe.pdf</c> both give <c>exe</c>.</summary>
    internal static string? BlockedExtension(string name, IReadOnlySet<string> blocked)
    {
        var parts = name.ToLowerInvariant().Split('.');
        return parts.Skip(1).Select(p => p.Trim().Split(':')[0]).FirstOrDefault(blocked.Contains);
    }

    /// <summary>An attachment's type for its chip: the type sent if it is one, else by the extension, else octet-stream.</summary>
    private static string AttachmentType(string? contentType, string name)
    {
        var sent = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        if (sent is { Length: > 0 and <= 255 } && sent != "application/octet-stream" && MediaType().IsMatch(sent))
        {
            return sent;
        }
        return ContentTypes.TryGetContentType(name, out var byName) ? byName : "application/octet-stream";
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9!#$&^_.+-]{0,126}/[a-z0-9][a-z0-9!#$&^_.+-]{0,126}$", RegexOptions.None, 250)]
    private static partial Regex MediaType();
}

/// <summary>Uploads per user and hour (KnowledgeBase:UploadsPerHour), in memory, as the agents' downloads per address.</summary>
public sealed class UploadGuard
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, (DateTime Start, int Count)> perUser = new();

    /// <summary>False when the user used up their uploads; <paramref name="retryAfter"/> is when their window ends.</summary>
    public bool TryEnter(Guid user, int perHour, out TimeSpan retryAfter)
    {
        var now = Clock.UtcNow;
        var entry = perUser.AddOrUpdate(user, _ => (now, 1), (_, e) => now - e.Start >= Window ? (now, 1) : (e.Start, e.Count + 1));
        retryAfter = entry.Start + Window - now;
        return entry.Count <= Math.Max(1, perHour);
    }

    /// <summary>For tests.</summary>
    public void Reset() => perUser.Clear();
}
