using System.Text;

namespace PNMTD.Lib.Helper;

public enum DiffLineKind
{
    Context = 0,
    Added = 1,
    Removed = 2,
}

/// <summary>One line of a diff, with its number in the old and in the new text (1-based; null where it has none).</summary>
public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldNumber, int? NewNumber);

/// <summary>A unified diff's hunk: where it starts in either text, and its lines.</summary>
public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<DiffLine> Lines)
{
    public string Header => $"@@ -{OldStart},{OldCount} +{NewStart},{NewCount} @@";
}

public sealed record LineDiffResult(int Added, int Removed, IReadOnlyList<DiffHunk> Hunks)
{
    public bool Identical => Added == 0 && Removed == 0;

    /// <summary>As <c>diff -u</c> writes it, with the given file names.</summary>
    public string Unified(string oldName, string newName)
    {
        if (Identical)
        {
            return "";
        }
        var sb = new StringBuilder();
        sb.Append("--- ").Append(oldName).Append('\n');
        sb.Append("+++ ").Append(newName).Append('\n');
        foreach (var hunk in Hunks)
        {
            sb.Append(hunk.Header).Append('\n');
            foreach (var line in hunk.Lines)
            {
                sb.Append(line.Kind switch { DiffLineKind.Added => '+', DiffLineKind.Removed => '-', _ => ' ' }).Append(line.Text).Append('\n');
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// A line diff (docs/mikrotik-plan.md §6.4): Myers' O((N+M)D) algorithm, after the common start and
/// end are cut off, with unified hunks. Shared since the knowledge base's histories use it too
/// (docs/locations-knowledge-plan.md §6.3). Exports differ in a few lines between versions; when two texts
/// differ in more than <see cref="MaxEdits"/> lines, the rest is shown as replaced, which is still a
/// correct diff, only not the shortest.
/// </summary>
public static class LineDiff
{
    public const int MaxEdits = 4000;

    public static LineDiffResult Compare(string oldText, string newText, int context = 3)
    {
        var a = Lines(oldText);
        var b = Lines(newText);
        var edits = Edits(a, b);
        var added = edits.Count(e => e.Kind == DiffLineKind.Added);
        var removed = edits.Count(e => e.Kind == DiffLineKind.Removed);
        return new LineDiffResult(added, removed, Hunks(edits, context));
    }

    /// <summary>
    /// Only the counts, for sequences compared by value: the keyed line digests of an export
    /// (docs/mikrotik-backup-plan.md §3.7), counted as the text would be.
    /// </summary>
    public static (int Added, int Removed) Count(string[] oldLines, string[] newLines)
    {
        var edits = Edits(oldLines, newLines);
        return (edits.Count(e => e.Kind == DiffLineKind.Added), edits.Count(e => e.Kind == DiffLineKind.Removed));
    }

    /// <summary>The lines of a text; a final line end does not make an empty last line.</summary>
    public static string[] Lines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }
        var normalised = text.Replace("\r\n", "\n");
        if (normalised.EndsWith('\n'))
        {
            normalised = normalised[..^1];
        }
        return normalised.Split('\n');
    }

    /// <summary>Every line of both texts in order: kept, removed or added.</summary>
    private static List<DiffLine> Edits(string[] a, string[] b)
    {
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)])
        {
            suffix++;
        }
        var result = new List<DiffLine>(a.Length + b.Length);
        for (var i = 0; i < prefix; i++)
        {
            result.Add(new DiffLine(DiffLineKind.Context, a[i], i + 1, i + 1));
        }
        var middle = Myers(a.AsSpan(prefix, a.Length - prefix - suffix).ToArray(), b.AsSpan(prefix, b.Length - prefix - suffix).ToArray());
        foreach (var (kind, ai, bi) in middle)
        {
            result.Add(kind switch
            {
                DiffLineKind.Context => new DiffLine(kind, a[prefix + ai], prefix + ai + 1, prefix + bi + 1),
                DiffLineKind.Removed => new DiffLine(kind, a[prefix + ai], prefix + ai + 1, null),
                _ => new DiffLine(kind, b[prefix + bi], null, prefix + bi + 1),
            });
        }
        for (var i = 0; i < suffix; i++)
        {
            var ai = a.Length - suffix + i;
            var bi = b.Length - suffix + i;
            result.Add(new DiffLine(DiffLineKind.Context, a[ai], ai + 1, bi + 1));
        }
        return result;
    }

    /// <summary>Myers' greedy algorithm with the trace of each step, then the path walked back.</summary>
    private static List<(DiffLineKind Kind, int A, int B)> Myers(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length, max = n + m;
        var path = new List<(DiffLineKind, int, int)>();
        if (max == 0)
        {
            return path;
        }
        var limit = Math.Min(max, MaxEdits);
        var offset = limit + 1;
        var v = new int[2 * limit + 3];
        var trace = new List<int[]>();
        var found = false;
        for (var d = 0; d <= limit && !found; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                var down = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]);
                var x = down ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = true;
                    break;
                }
            }
        }
        if (!found)
        {
            // Too different: everything removed, then everything added.
            for (var i = 0; i < n; i++) path.Add((DiffLineKind.Removed, i, 0));
            for (var j = 0; j < m; j++) path.Add((DiffLineKind.Added, n, j));
            return path;
        }
        // Walk back from (n, m) through the saved states.
        int cx = n, cy = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var vd = trace[d];
            var k = cx - cy;
            int prevK;
            if (d == 0)
            {
                while (cx > 0 && cy > 0)
                {
                    path.Add((DiffLineKind.Context, --cx, --cy));
                }
                break;
            }
            var down = k == -d || (k != d && vd[offset + k - 1] < vd[offset + k + 1]);
            prevK = down ? k + 1 : k - 1;
            var prevX = vd[offset + prevK];
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                path.Add((DiffLineKind.Context, --cx, --cy));
            }
            if (down)
            {
                path.Add((DiffLineKind.Added, cx, --cy));
            }
            else
            {
                path.Add((DiffLineKind.Removed, --cx, cy));
            }
        }
        path.Reverse();
        return path;
    }

    private static List<DiffHunk> Hunks(List<DiffLine> edits, int context)
    {
        var hunks = new List<DiffHunk>();
        var i = 0;
        while (i < edits.Count)
        {
            if (edits[i].Kind == DiffLineKind.Context)
            {
                i++;
                continue;
            }
            // A change: take up to `context` lines before it, then extend while the next change is near.
            var start = Math.Max(0, i - context);
            var end = i;
            while (true)
            {
                while (end < edits.Count && edits[end].Kind != DiffLineKind.Context)
                {
                    end++;
                }
                var next = end;
                while (next < edits.Count && edits[next].Kind == DiffLineKind.Context && next - end < 2 * context)
                {
                    next++;
                }
                if (next < edits.Count && edits[next].Kind != DiffLineKind.Context)
                {
                    end = next;
                    continue;
                }
                end = Math.Min(edits.Count, end + context);
                break;
            }
            var lines = edits.GetRange(start, end - start);
            var oldLines = lines.Where(l => l.OldNumber is not null).ToList();
            var newLines = lines.Where(l => l.NewNumber is not null).ToList();
            var oldStart = oldLines.FirstOrDefault()?.OldNumber ?? (PreviousOld(edits, start) ?? 0);
            var newStart = newLines.FirstOrDefault()?.NewNumber ?? (PreviousNew(edits, start) ?? 0);
            hunks.Add(new DiffHunk(oldStart, oldLines.Count, newStart, newLines.Count, lines));
            i = end;
        }
        return hunks;
    }

    /// <summary>For a hunk without lines on one side, unified diff names the line before it.</summary>
    private static int? PreviousOld(List<DiffLine> edits, int index)
    {
        for (var j = index - 1; j >= 0; j--)
        {
            if (edits[j].OldNumber is { } n) return n;
        }
        return null;
    }

    private static int? PreviousNew(List<DiffLine> edits, int index)
    {
        for (var j = index - 1; j >= 0; j--)
        {
            if (edits[j].NewNumber is { } n) return n;
        }
        return null;
    }
}
