using System.Security.Cryptography;
using System.Text;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Keys;

/// <summary>The file changed between the agent's read (its backup) and the rewrite: nothing was written.</summary>
internal sealed class KeyFileChangedException(string message) : IOException(message);

/// <summary>
/// Rewriting a key file (docs/linux-sensor-plan.md §6.5 step 6, §6.6): through the folder's descriptor (opened without following a symlink),
/// so a folder swapped between the checks and the write can't send it elsewhere; the file opened without following a symlink; a temporary
/// file next to it (<c>O_EXCL</c>), written, synced, with the original's mode (and, as root, its owner), renamed over the original, the folder
/// synced. Run by the agent itself for root's files, and by <c>pnmt-agent internal rewrite-keys</c> as the file's owner (started with
/// <c>setpriv</c>) for a user's: the kernel then checks every step as for that user.
/// </summary>
internal static class KeyRewrite
{
    /// <summary>
    /// Removes every line with the key (whatever its options): the lines removed and the file's new SHA-256. 0 lines: nothing written. With
    /// <paramref name="expectedSha256"/>, refuses (<see cref="KeyFileChangedException"/>) when the file isn't what was backed up.
    /// </summary>
    public static (int Removed, string? Sha256) RemoveKey(string path, string fingerprint, string? expectedSha256, bool keepOwner)
    {
        var result = (Removed: 0, Sha256: (string?)null);
        WithFolder(path, (folder, name) =>
        {
            var (content, facts) = ReadAt(folder, name) ?? throw new FileNotFoundException($"{path} doesn't exist.");
            if (expectedSha256 is not null && Sha(content) != expectedSha256)
            {
                throw new KeyFileChangedException($"{path} changed since it was read: nothing was removed.");
            }
            var (kept, removed) = Without(content, fingerprint);
            if (removed == 0)
            {
                result = (0, Sha(content));
                return;
            }
            Replace(folder, name, kept, (uint)facts.Bits & 0xFFF, keepOwner ? facts : null);
            result = (removed, Sha(kept));
        });
        return result;
    }

    /// <summary>
    /// Writes a file's whole content (<c>pnmt-agent keys restore</c>): over the existing file (its mode and, as root, its owner kept), or a new
    /// one with <paramref name="mode"/> and <paramref name="owner"/>. Answers the new SHA-256.
    /// </summary>
    public static string WriteAll(string path, byte[] content, uint mode, (uint Uid, uint Gid)? owner)
    {
        WithFolder(path, (folder, name) =>
        {
            var existing = ReadAt(folder, name, missingOk: true);
            var facts = existing?.Facts;
            Replace(folder, name, content, facts is null ? mode : (uint)facts.Bits & 0xFFF,
                facts is not null ? owner is null ? null : facts : owner is { } o ? new FileFacts(o.Uid, o.Gid, 0, false, false, 0) : null);
        });
        return Sha(content);
    }

    /// <summary>The file without the key's lines, its bytes otherwise as they were (<see cref="SshKeys.Without"/> for UTF-8, byte by byte for the rest).</summary>
    public static (byte[] Kept, int Removed) Without(byte[] content, string fingerprint)
    {
        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
            var (kept, removed) = SshKeys.Without(text, fingerprint);
            var bom = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF;
            var bytes = Encoding.UTF8.GetBytes(kept);
            return (bom && !(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes, removed);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8 (a comment in Latin-1): each line is judged by its text, kept by its bytes.
            var output = new MemoryStream();
            var removed = 0;
            var start = 0;
            while (start <= content.Length)
            {
                var end = Array.IndexOf(content, (byte)'\n', start);
                var last = end < 0;
                var line = content.AsSpan(start, (last ? content.Length : end) - start);
                var (key, _) = SshKeys.ParseLine(Encoding.UTF8.GetString(line).TrimEnd('\r'));
                if (key is not null && key.Fingerprint == fingerprint)
                {
                    removed++;
                }
                else
                {
                    output.Write(line);
                    if (!last)
                    {
                        output.WriteByte((byte)'\n');
                    }
                }
                if (last)
                {
                    break;
                }
                start = end + 1;
            }
            return (output.ToArray(), removed);
        }
    }

    public static string Sha(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static void WithFolder(string path, Action<int, string> work)
    {
        var folder = Path.GetDirectoryName(path) ?? throw new ArgumentException($"{path} has no folder.");
        var name = Path.GetFileName(path);
        var fd = Native.Open(folder, Native.ReadOnly | Native.DirectoryOnly | Native.NoFollow);
        if (fd < 0)
        {
            var errno = Native.LastError;
            throw new IOException(errno == Native.ELOOP || errno == 20 ? $"{folder} is a symlink or not a folder: not written." : $"{folder}: {Native.Describe(errno)}");
        }
        try
        {
            work(fd, name);
        }
        finally
        {
            Native.Close(fd);
        }
    }

    private static (byte[] Content, FileFacts Facts)? ReadAt(int folder, string name, bool missingOk = false)
    {
        var fd = Native.OpenAt(folder, name, Native.ReadOnly | Native.NoFollow | Native.NonBlocking | Native.NoControllingTerminal);
        if (fd < 0)
        {
            var errno = Native.LastError;
            if (errno == Native.ENOENT && missingOk)
            {
                return null;
            }
            throw errno == Native.ENOENT ? new FileNotFoundException($"{name} doesn't exist.")
                : new IOException(errno == Native.ELOOP ? $"{name} is a symlink: not written." : $"{name}: {Native.Describe(errno)}");
        }
        try
        {
            var facts = Native.StatFd(fd) ?? throw new IOException($"{name}: {Native.Describe(Native.LastError)}");
            if (!facts.Regular)
            {
                throw new IOException($"{name} is not a regular file: not written.");
            }
            var content = Native.ReadAll(fd, SshKeys.MaxFileBytes + 1) ?? throw new IOException($"{name}: {Native.Describe(Native.LastError)}");
            if (content.Length > SshKeys.MaxFileBytes)
            {
                throw new IOException($"{name} is larger than 1 MiB: not written.");
            }
            return (content, facts);
        }
        finally
        {
            Native.Close(fd);
        }
    }

    private static void Replace(int folder, string name, byte[] content, uint mode, FileFacts? owner)
    {
        var temporary = $".{name}.pnmt-{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}";
        var fd = Native.OpenAt(folder, temporary, Native.WriteOnly | Native.Create | Native.Exclusive | Native.NoFollow, mode & 0x1FF);
        if (fd < 0)
        {
            throw new IOException($"a temporary file next to {name}: {Native.Describe(Native.LastError)}");
        }
        var renamed = false;
        try
        {
            try
            {
                Native.WriteAll(fd, content);
                if (owner is not null)
                {
                    Native.Fchown(fd, owner.Uid, owner.Gid);
                }
                // After fchown, which clears setuid bits: the original's mode exactly, past the umask.
                Native.Fchmod(fd, mode & 0xFFF);
                Native.Fsync(fd);
            }
            finally
            {
                Native.Close(fd);
            }
            Native.RenameAt(folder, temporary, name);
            renamed = true;
            try
            {
                Native.Fsync(folder);
            }
            catch (IOException)
            {
                // Some file systems (pmxcfs) don't sync folders; the rename happened.
            }
        }
        finally
        {
            if (!renamed)
            {
                Native.UnlinkAt(folder, temporary);
            }
        }
    }
}
