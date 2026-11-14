using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Knowledge;

/// <summary>The tag task's answer (§7.3): at most 10 tags.</summary>
public sealed record KbTagSuggestion(
    [property: Description("At most 10 tags, the best first: lower case, 2 to 40 letters, digits and dashes, from the vocabulary where one fits")]
    IReadOnlyList<string> Tags);

/// <summary>What the tag task reads: the page as untrusted text, its tags, the vocabulary, and how many tags it may add.</summary>
public sealed record KbTagInput(
    string UntrustedTitle,
    string? UntrustedSummary,
    string UntrustedText,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Vocabulary,
    int MaxTags);

/// <summary><c>kb-tags</c> (§7.3): tags for a knowledge base page.</summary>
public sealed class KbTagTask : AiTask<KbTagInput, KbTagSuggestion>
{
    public override string Purpose => KbTagsHandler.Name;

    public override string PromptVersion => "kb-tags-1";

    public override int MaxTokens => 400;

    protected override string Instructions =>
        "The data is a page of PNMT's knowledge base, written by the IT team: its title, summary and text, the tags it has, and the tags " +
        "used most across the knowledge base (the vocabulary). Suggest at most maxTags tags that would help someone find the page later: " +
        "its topics, systems, products, places and kinds of procedure. Prefer tags from the vocabulary, and propose a new tag only when none " +
        "fits. Do not repeat the page's tags. A tag is lower case, 2 to 40 characters of letters, digits and dashes, such as backup, ups or " +
        "printer-setup. Never use names of people, addresses, numbers, placeholders such as <ip:1>, or anything that looks like a secret. " +
        "Placeholders stand for values that were removed.";

    public override string? Validate(KbTagSuggestion output, KbTagInput input) =>
        output.Tags.Count > 30 || output.Tags.Any(t => t.Length > 100) ? "Too many or too long tags." : null;
}

/// <summary>
/// Scans running for a page (§7.1): marking the page sensitive, archiving, redacting or deleting it cancels every scan of
/// the page at once, even while the request to the model is under way. A page can have more than one (two work items
/// from before the enqueue was atomic, <c>Ai:Parallelism</c> above 1), so each keeps its own token.
/// </summary>
public sealed class KbAiScans
{
    private sealed record Scan(CancellationTokenSource Cts, TaskCompletionSource Ended);

    private readonly object gate = new();
    private readonly Dictionary<Guid, List<Scan>> running = [];

    public CancellationTokenSource Register(Guid page, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (gate)
        {
            if (!running.TryGetValue(page, out var scans))
            {
                running[page] = scans = [];
            }
            scans.Add(new Scan(cts, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)));
        }
        return cts;
    }

    public void Done(Guid page, CancellationTokenSource cts)
    {
        lock (gate)
        {
            if (running.TryGetValue(page, out var scans) && scans.FirstOrDefault(s => s.Cts == cts) is { } scan)
            {
                scans.Remove(scan);
                scan.Ended.TrySetResult();
                if (scans.Count == 0)
                {
                    running.Remove(page);
                }
            }
        }
        cts.Dispose();
    }

    /// <summary>How many scans of the page are running.</summary>
    public int Running(Guid page)
    {
        lock (gate)
        {
            return running.TryGetValue(page, out var scans) ? scans.Count : 0;
        }
    }

    /// <summary>Cancels every scan of the page; the tasks that end when they have stopped.</summary>
    public IReadOnlyList<Task> Cancel(Guid page)
    {
        List<Scan> scans;
        lock (gate)
        {
            scans = running.TryGetValue(page, out var list) ? [.. list] : [];
        }
        foreach (var scan in scans)
        {
            try
            {
                scan.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It ended meanwhile.
            }
        }
        return scans.Select(s => s.Ended.Task).ToList();
    }

    /// <summary>
    /// Cancels every scan of the page and waits, at most <paramref name="wait"/>, until they have stopped: a redaction
    /// cleans the request log after them, so a request that was being written isn't written after the cleaning.
    /// </summary>
    public async Task CancelAsync(Guid page, TimeSpan wait)
    {
        var ended = Cancel(page);
        if (ended.Count > 0)
        {
            await Task.WhenAny(Task.WhenAll(ended), Task.Delay(wait));
        }
    }
}

/// <summary>A scan's work item: the declaration it was started with, then what it did (<c>ai_work.Result</c>).</summary>
public sealed record KbAiTagsResult(KbStoredDeclaration Declaration, IReadOnlyList<string>? Added = null, int? Revision = null, string? BlockedBy = null,
    string? Message = null);

/// <summary>
/// <c>POST /kb/pages/{id}/ai-tags</c> and its status (§7.3). Before anything leaves PNMT, in order: AI is usable, the
/// page isn't sensitive or archived, the revision is the current one, both statements are there; then the credentials
/// scan in code, which refuses with the line numbers only; then a work item of the AI queue with one attempt.
/// </summary>
public sealed class KbAiTags(PnmtdDbContext db, KbRevisions revisions, IOptionsMonitor<AiOptions> ai, IOptionsMonitor<KnowledgeBaseOptions> kb,
    AiBudget budget, AiWorker worker)
{
    public const string SubjectType = "kb-page";

    public async Task<KbAiTagsStatus> StartAsync(Guid pageId, KbAiTagsRequest request, CancellationToken ct)
    {
        if (!ai.CurrentValue.Usable || !kb.CurrentValue.AiTags)
        {
            throw ApiException.Conflict(KbErrors.AiUnavailable, "AI tags are off: AI isn't usable, or KnowledgeBase:AiTags is false.");
        }
        // Under the page's row lock until the work item is written: two clicks make one item, and marking the page
        // sensitive or redacting it waits for the item, whose scan it then cancels or turns stale.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var page = await revisions.LockAsync(pageId, ct);
        if (page.Sensitive)
        {
            throw ApiException.Conflict(KbErrors.Sensitive, $"KB-{page.Number} is sensitive: it is never sent to an AI.");
        }
        if (page.ArchivedAt is not null)
        {
            throw ApiException.Conflict(KbErrors.Archived, $"KB-{page.Number} is archived.");
        }
        if (request.Revision != page.Revision)
        {
            throw KbPageService.Conflict(page);
        }
        if (!KbStatementNames.Complete(request.Statements))
        {
            throw ApiException.Invalid("statements", "Confirm both statements first.");
        }
        var text = string.Join("\n", page.Content, page.Title, page.Summary ?? "");
        var findings = SecretScanner.Scan(text);
        if (findings.Count > 0)
        {
            var lines = page.Content.Replace("\r\n", "\n").Split('\n').Length;
            throw KbPageService.LooksLikeCredential(findings.Select(f => f.Line > lines ? f with { Line = 0 } : f).ToList(),
                "Some lines look like credentials: nothing was sent. Edit the page, or mark it sensitive.");
        }
        try
        {
            await budget.EnsureAvailableAsync(ct);
        }
        catch (AiBudgetExceededException ex)
        {
            throw ApiException.Conflict(KbErrors.AiBudgetSpent, ex.Message);
        }
        var actor = revisions.Actor;
        if (actor.Id is { } user)
        {
            await KbPageService.NoteQuestionAsync(db, page.Id, user, null, ct);
        }
        var open = await db.AiWork.AsNoTracking()
            .Where(w => w.Purpose == KbTagsHandler.Name && w.SubjectType == SubjectType && w.SubjectId == page.Id && AiWorkQueue.Open.Contains(w.State))
            .FirstOrDefaultAsync(ct);
        if (open is not null)
        {
            await tx.CommitAsync(ct);
            return Status(open);
        }
        var declaration = new KbStoredDeclaration(actor.Id, actor.Name, Clock.UtcNow, KbStatementNames.Both, page.Revision);
        var work = new AiWorkEntity
        {
            Id = Guid.NewGuid(), Purpose = KbTagsHandler.Name, SubjectType = SubjectType, SubjectId = page.Id, InputHash = $"r{page.Revision}",
            State = AiWorkState.Queued, CreatedAt = Clock.UtcNow, NextAttemptAt = Clock.UtcNow,
            // The declaration goes with the item, so the scan writes it into the revision it makes (§7.2).
            Result = KbRevisions.Serialize(new KbAiTagsResult(declaration)),
        };
        db.AiWork.Add(work);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        worker.Wake();
        return Status(work);
    }

    public async Task<KbAiTagsStatus> StatusAsync(Guid pageId, Guid workId, CancellationToken ct)
    {
        var work = await db.AiWork.AsNoTracking()
            .SingleOrDefaultAsync(w => w.Id == workId && w.Purpose == KbTagsHandler.Name && w.SubjectId == pageId, ct) ?? throw ApiException.NotFound("Scan");
        return Status(work);
    }

    private static KbAiTagsStatus Status(AiWorkEntity work)
    {
        var result = KbTagsHandler.Read(work.Result);
        var message = result?.Message ?? (work.State is AiWorkState.Failed or AiWorkState.Cancelled ? work.LastError : null);
        return new KbAiTagsStatus(work.Id, work.State, result?.Added ?? [], result?.Revision, result?.BlockedBy, message, Clock.AsUtc(work.CreatedAt),
            work.FinishedAt is { } f ? Clock.AsUtc(f) : null);
    }
}

/// <summary>
/// Runs a scan (§7.3): the page through <see cref="MachineAccess"/>, its text cut to <c>Ai:MaxInputChars</c> and scrubbed
/// (addresses, mail addresses, URLs, host names, GUIDs, long tokens, phone numbers); the pipeline with Jev's two extra
/// questions, which stop it like the injection question; the strict schema; the checks in code. At most
/// <c>KnowledgeBase:MaxAiTags</c> tags are added as AI tags, in one revision with the declaration. Files are never sent.
/// One attempt: a scan someone waits for doesn't come back hours later.
/// </summary>
public sealed partial class KbTagsHandler(IDbContextFactory<PnmtdDbContext> dbs, AiPipeline pipeline, IOptionsMonitor<AiOptions> ai,
    IOptionsMonitor<KnowledgeBaseOptions> kb, KbAiScans scans, IHttpContextAccessor accessor) : IAiWorkHandler
{
    public const string Name = "kb-tags";

    /// <summary>How many of the most used tags go along as the vocabulary.</summary>
    public const int VocabularySize = 300;

    /// <summary>An item older than this is no longer waited for.</summary>
    public static readonly TimeSpan WaitedFor = TimeSpan.FromMinutes(15);

    public const string CredentialsQuestion = "Does the text contain credentials such as passwords, keys or tokens?";

    public const string PersonalDataQuestion = "Does the text contain personal data about identifiable people?";

    private static readonly KbTagTask Task = new();

    private static readonly IReadOnlyDictionary<string, DecisionQuestion> GuardQuestions = new Dictionary<string, DecisionQuestion>
    {
        ["credentials"] = DecisionQuestion.Noul(CredentialsQuestion),
        ["personalData"] = DecisionQuestion.Noul(PersonalDataQuestion),
    };

    public string Purpose => Name;

    public int? MaxAttempts => 1;

    public async Task<string?> RunAsync(AiWorkEntity work, CancellationToken ct)
    {
        var started = Read(work.Result) ?? throw new AiWorkStaleException("The scan has no statements.");
        var declaration = started.Declaration;
        if (Clock.UtcNow - Clock.AsUtc(work.CreatedAt) > WaitedFor)
        {
            throw new AiWorkStaleException("Nobody waits for this scan any more.");
        }
        var pageId = work.SubjectId ?? throw new AiWorkStaleException("No page.");
        var cts = scans.Register(pageId, ct);
        try
        {
            return await ScanAsync(work, pageId, declaration, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new AiWorkStaleException("The page was marked sensitive, archived or deleted.");
        }
        finally
        {
            scans.Done(pageId, cts);
        }
    }

    private async Task<string> ScanAsync(AiWorkEntity work, Guid pageId, KbStoredDeclaration declaration, CancellationToken ct)
    {
        KbTagInput input;
        IReadOnlyList<string> vocabulary;
        await using (var db = await dbs.CreateDbContextAsync(ct))
        {
            // Refused a second time, by the filter every machine reader uses (§7.4).
            var page = await MachineAccess.Pages(db).Include(p => p.Tags).ThenInclude(t => t.Tag).SingleOrDefaultAsync(p => p.Id == pageId, ct)
                ?? throw new AiWorkStaleException("The page is sensitive or gone.");
            if (page.ArchivedAt is not null)
            {
                throw new AiWorkStaleException("The page is archived.");
            }
            if (page.Revision != declaration.Revision)
            {
                throw new AiWorkStaleException("The page changed after the statements were made.");
            }
            var existing = page.Tags.Select(t => t.Tag!.Name).Order(StringComparer.Ordinal).ToList();
            var room = Math.Min(kb.CurrentValue.MaxAiTags, KbTags.MaxPerPage - existing.Count);
            if (room <= 0)
            {
                return Result(declaration, [], null, null, $"KB-{page.Number} has {existing.Count} tags already.");
            }
            vocabulary = await KbTags.VocabularyAsync(MachineAccess.Pages(db), db, VocabularySize, ct);
            var max = ai.CurrentValue.MaxInputChars;
            var scrubber = new Scrubber { PhoneNumbers = true };
            var title = scrubber.Scrub(InputSanitizer.Clean(page.Title, 500).Text);
            var summary = page.Summary is null ? null : scrubber.Scrub(InputSanitizer.Clean(page.Summary, 1000).Text);
            var text = scrubber.Scrub(InputSanitizer.Clean(page.PlainText, max).Text);
            input = new KbTagInput(title, summary, text, existing, vocabulary, room);
        }
        var untrusted = new[] { string.Join("\n\n", new[] { input.UntrustedTitle, input.UntrustedSummary, input.UntrustedText }.Where(t => !string.IsNullOrEmpty(t))) };
        var result = await pipeline.RunAsync(Task, input, untrusted, new AiCall(Purpose, work.Id, work.Attempts + 1), ct, GuardQuestions);
        if (result.Blocked)
        {
            var threshold = ai.CurrentValue.Guard.Threshold;
            var by = result.BlockedBy == "scan" ? "scan"
                : result.Injection >= threshold ? "injection"
                : result.Guard.TryGetValue("credentials", out var c) && (c.Probability ?? 0) >= threshold ? "credentials"
                : "personalData";
            return Result(declaration, [], null, by, by switch
            {
                "credentials" => "The guard thinks the page holds credentials. Nothing went to the main model.",
                "personalData" => "The guard thinks the page holds personal data. Nothing went to the main model.",
                _ => "The page looks like it gives instructions to an AI. Nothing went to the main model.",
            });
        }
        var tags = Check(result.Output!.Tags, input.Tags, vocabulary, input.MaxTags);
        if (tags.Count == 0)
        {
            return Result(declaration, [], null, null, "The AI found no new tags.");
        }
        return await ApplyAsync(pageId, declaration, tags, ct);
    }

    /// <summary>The tags as AI tags, in one revision with the declaration; nothing if the page changed or became sensitive.</summary>
    private async Task<string> ApplyAsync(Guid pageId, KbStoredDeclaration declaration, IReadOnlyList<string> tags, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var revisions = new KbRevisions(db, accessor) { As = new KbActor(declaration.ByUserId, declaration.ByName) };
        var page = await revisions.LockAsync(pageId, ct);
        if (page.Sensitive || page.ArchivedAt is not null || page.Revision != declaration.Revision)
        {
            throw new AiWorkStaleException("The page changed, or was marked sensitive or archived, during the scan.");
        }
        var before = KbSnapshot.Of(page);
        var names = before.Tags.Select(t => t.Name).Concat(tags).Distinct().ToList();
        var known = await KbTags.ResolveAsync(db, names, ct);
        foreach (var tag in known.Where(t => tags.Contains(t.NormalizedName)))
        {
            page.Tags.Add(new KbPageTagEntity { PageId = page.Id, TagId = tag.Id, Tag = tag, Source = KbTagSource.Ai, AddedAt = Clock.UtcNow, AddedBy = declaration.ByUserId });
        }
        var revision = await revisions.CommitAsync(page, before, null,
            [new KbChange(KbChangeKinds.AiTags, $"{string.Join(", ", tags)} (statements by {declaration.ByName})")], ct, declaration);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Result(declaration, tags, revision.Number, null, null);
    }

    /// <summary>
    /// The checks in code (§7.3): normalised; without the page's tags and duplicates; without what looks like a
    /// placeholder, a number, an address, a mail address or a URL; 2–40 characters; at most <paramref name="max"/>, those
    /// in the vocabulary first.
    /// </summary>
    public static IReadOnlyList<string> Check(IEnumerable<string> proposed, IReadOnlyCollection<string> existing, IReadOnlyCollection<string> vocabulary, int max)
    {
        var kept = new List<string>();
        foreach (var raw in proposed)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Contains('<') || raw.Contains('>') || raw.Contains('@') || raw.Contains("://", StringComparison.Ordinal)
                || raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || Address().IsMatch(raw.Trim()))
            {
                continue;
            }
            if (KbTags.Normalize(raw) is not { } tag || Number().IsMatch(tag) || existing.Contains(tag) || kept.Contains(tag))
            {
                continue;
            }
            kept.Add(tag);
        }
        return kept.OrderBy(t => vocabulary.Contains(t) ? 0 : 1).Take(Math.Max(0, max)).ToList();
    }

    internal static KbAiTagsResult? Read(string? json) => KbRevisions.Read<KbAiTagsResult>(json);

    private static string Result(KbStoredDeclaration declaration, IReadOnlyList<string> added, int? revision, string? blockedBy, string? message) =>
        KbRevisions.Serialize(new KbAiTagsResult(declaration, added, revision, blockedBy, message));

    /// <summary>Digits with separators only: 2026, 10-20, 1.5.</summary>
    [GeneratedRegex(@"^[0-9]+(?:[-.][0-9]+)*$")]
    private static partial Regex Number();

    /// <summary>An IPv4 or IPv6 address, a MAC address, a network.</summary>
    [GeneratedRegex(@"^(?:\d{1,3}\.){3}\d{1,3}(?:/\d{1,2})?$|^[0-9a-f]*:[0-9a-f:]+$|^(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}$", RegexOptions.IgnoreCase)]
    private static partial Regex Address();
}
