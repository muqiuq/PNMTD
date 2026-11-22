namespace PNMTD.Knowledge;

/// <summary>
/// The <c>KnowledgeBase</c> section (docs/locations-knowledge-plan.md §8.4): <c>KnowledgeBase__*</c> in .env. Read through
/// <c>IOptionsMonitor</c>, registered by <c>FilesSetup.AddFiles</c>. The host's side of the files folder is
/// <c>PNMTD_FILES_DIR</c> in deploy/.env (§8.5).
/// </summary>
public sealed class KnowledgeBaseOptions
{
    public const string Section = "KnowledgeBase";

    /// <summary>The files folder inside the container: a bind mount of a folder on the Docker host (§5.5, §8.5).</summary>
    public string FilesPath { get; set; } = "/var/lib/pnmtd/files";

    /// <summary>Per image (R6), shown as 2 MB. A larger one is recompressed in the browser first.</summary>
    public long MaxImageBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>Per attachment, shown as 25 MB. The upload endpoint's body limit is this plus 4 KB (§5.5).</summary>
    public long MaxAttachmentBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>Refused as attachments, double extensions included (<c>report.pdf.exe</c>): separated by spaces or commas.</summary>
    public string BlockedExtensions { get; set; } = "exe msi dll scr com bat cmd ps1 vbs js jar hta lnk";

    /// <summary>A page's Markdown, in UTF-8 bytes; files don't count toward it.</summary>
    public int MaxPageBytes { get; set; } = 1024 * 1024;

    /// <summary>A location field's Markdown, in UTF-8 bytes.</summary>
    public int MaxFieldBytes { get; set; } = 256 * 1024;

    /// <summary>Uploads per user and hour.</summary>
    public int UploadsPerHour { get; set; } = 200;

    /// <summary>An upload no page or location uses is marked deleted after this long (a paste that was never saved).</summary>
    public int UnusedFileHours { get; set; } = 24;

    /// <summary>A file marked deleted leaves the disk this long after, so a backup's dump never names a file already gone.</summary>
    public int DeletedFileDays { get; set; } = 7;

    /// <summary>Below this much free space on the folder's disk, the <i>File storage</i> sensor writes 428.</summary>
    public long MinFreeMb { get; set; } = 1024;

    /// <summary>AI tags (§7); has an effect only while AI is usable (<c>Ai__Enabled</c>, a key).</summary>
    public bool AiTags { get; set; } = true;

    /// <summary>Tags added per scan at most (R12).</summary>
    public int MaxAiTags { get; set; } = 10;

    /// <summary>How often, in percent, the AI question follows a saved edit (R12, §7.2).</summary>
    public int AiQuestionOnEditPercent { get; set; } = 10;

    /// <summary>The recent changes on top of the knowledge base go back this many days (docs/knowledge-base-extensions-plan.md §6).</summary>
    public int RecentChangesDays { get; set; } = 14;

    /// <summary>At most this many pages in the recent changes, each with its newest change (§6).</summary>
    public int RecentChangesMax { get; set; } = 10;

    /// <summary>The same person records another use of a page after this many hours (§9).</summary>
    public int UseRepeatHours { get; set; } = 12;

    /// <summary><see cref="BlockedExtensions"/> as a set: lower case, without dots.</summary>
    public IReadOnlySet<string> BlockedExtensionSet =>
        BlockedExtensions.Split([' ', ',', ';', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0).ToHashSet(StringComparer.Ordinal);
}
