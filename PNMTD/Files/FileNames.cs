using System.Globalization;
using System.Text;
using PNMTD.Api.V1.Contracts;
using PNMTD.Models.Db;

namespace PNMTD.Files;

/// <summary>
/// Names of stored files (docs/locations-knowledge-plan.md §5.1, §5.5): the URL Markdown points to,
/// <c>/api/v1/files/{id}/{slug}</c>, and the slug the URL and the path on disk end in. Only the id finds a file; the slug
/// is there to be read, by people and by the host's tools.
/// </summary>
public static class FileNames
{
    public const string UrlPrefix = "/api/v1/files/";

    /// <summary>A slug's length at most, its extension included.</summary>
    public const int MaxSlug = 80;

    public static string Url(Guid id, string name) => $"{UrlPrefix}{id}/{Slug(name)}";

    public static FileItem Item(FileEntity file) =>
        new(file.Id, file.Kind, file.Name, file.ContentType, file.Size, file.Width, file.Height, Url(file.Id, file.Name));

    /// <summary>
    /// <c>Cage B.png</c> gives <c>cage-b.png</c>: lower case ASCII letters and digits, accents dropped, everything else a
    /// single <c>-</c>, and the extension (up to 10 letters and digits) after the one dot. <c>file</c> when nothing is left
    /// of the name. Never a path: no slashes, no leading dot, no <c>..</c>.
    /// </summary>
    public static string Slug(string? name)
    {
        var plain = Ascii(name ?? "");
        var dot = plain.LastIndexOf('.');
        var extension = dot >= 0 && plain.Length - dot - 1 is >= 1 and <= 10 && plain[(dot + 1)..].All(char.IsAsciiLetterOrDigit)
            ? plain[(dot + 1)..] : "";
        var stem = Dashes(extension.Length > 0 ? plain[..dot] : plain);
        var room = MaxSlug - (extension.Length > 0 ? extension.Length + 1 : 0);
        if (stem.Length > room)
        {
            stem = stem[..room].TrimEnd('-');
        }
        if (stem.Length == 0)
        {
            stem = "file";
        }
        return extension.Length > 0 ? $"{stem}.{extension}" : stem;
    }

    /// <summary>Lower case, accents dropped (<c>ü</c> → <c>u</c>, <c>ß</c> → <c>ss</c>); other characters stay for <see cref="Dashes"/>.</summary>
    private static string Ascii(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            if (c == 'ß')
            {
                sb.Append("ss");
                continue;
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Letters and digits kept, every run of anything else one <c>-</c>, none at either end.</summary>
    private static string Dashes(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }
        return sb.ToString().TrimEnd('-');
    }
}
