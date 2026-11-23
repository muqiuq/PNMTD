using System.Collections.Concurrent;
using PNMTD.Api.V1.Contracts;
using PNMTD.Connectors;
using PNMTD.Lib.Helper;

namespace PNMTD.Boards;

/// <summary>The <c>Boards</c> settings (docs/boards-plan.md §11.4).</summary>
public sealed class BoardOptions
{
    public const string Section = "Boards";

    /// <summary>Off: all of /pnmt-board answers a bare 404, as an unknown path does (§4.1).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The address screens open boards at, when it isn't PublicBaseUrl (§5.1): https://board.example.com.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Requests per address and minute on /pnmt-board/api (§5.2).</summary>
    public int RequestsPerMinute { get; set; } = 120;
}

public static class BoardsSetup
{
    /// <summary>Boards (docs/boards-plan.md §11.4): the admin's service, the board's access check and data, and their memory.</summary>
    public static IServiceCollection AddBoards(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BoardOptions>(configuration.GetSection(BoardOptions.Section));
        services.AddSingleton<BoardTokenProtector>();
        services.AddSingleton<BoardGuard>();
        services.AddSingleton<BoardRuntime>();
        services.AddScoped<BoardService>();
        services.AddScoped<BoardAccess>();
        services.AddScoped<BoardData>();
        return services;
    }
}

/// <summary>
/// What boards keep in memory (§5.4, §8.1, §11.4): the boards found by their tokens (30 s), each board's last view (so ten screens cost
/// one build), the screens that showed one lately, when its last-seen was written, since when its bar has its colour, and when a
/// refusal by network was last audited. A restart forgets it all, at no harm.
/// </summary>
public sealed class BoardRuntime
{
    public static readonly TimeSpan TokenFor = TimeSpan.FromSeconds(30);

    /// <summary>A board as a request needs it: found by the token's hash.</summary>
    public sealed record Row(Guid Id, string Name, bool Enabled, IReadOnlyList<ConnectorNetwork> Networks, string Layout, int Revision);

    private readonly ConcurrentDictionary<string, (Row Row, DateTime At)> tokens = new();
    private readonly ConcurrentDictionary<Guid, (int Revision, BoardView View, DateTime At)> views = new();
    private readonly ConcurrentDictionary<(Guid Board, string Address, string Agent), DateTime> screens = new();
    private readonly ConcurrentDictionary<Guid, DateTime> written = new();
    private readonly ConcurrentDictionary<Guid, (BoardColour Colour, DateTime Since)> colours = new();
    private readonly ConcurrentDictionary<(Guid Board, string Address), DateTime> refusals = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> building = new();

    public Row? Token(string hash) => tokens.TryGetValue(hash, out var e) && Clock.UtcNow - e.At < TokenFor ? e.Row : null;

    public void Remember(string hash, Row row) => tokens[hash] = (row, Clock.UtcNow);

    /// <summary>After a save, a renewed URL, a delete: the next request reads the board again, and its view is built anew.</summary>
    public void Forget(Guid board)
    {
        foreach (var (hash, entry) in tokens)
        {
            if (entry.Row.Id == board)
            {
                tokens.TryRemove(hash, out _);
            }
        }
        views.TryRemove(board, out _);
    }

    /// <summary>The board's last view if it is of this revision and younger than <paramref name="maxAge"/>; else one built now, once.</summary>
    public async Task<BoardView> ViewAsync(Guid board, int revision, TimeSpan maxAge, Func<Task<BoardView>> build)
    {
        if (Fresh(board, revision, maxAge) is { } cached)
        {
            return cached;
        }
        // One build per board at a time: its other screens wait for it; other boards don't.
        var gate = building.GetOrAdd(board, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (Fresh(board, revision, maxAge) is { } built)
            {
                return built;
            }
            var view = await build();
            views[board] = (revision, view, Clock.UtcNow);
            return view;
        }
        finally
        {
            gate.Release();
        }
    }

    private BoardView? Fresh(Guid board, int revision, TimeSpan maxAge) =>
        views.TryGetValue(board, out var e) && e.Revision == revision && Clock.UtcNow - e.At < maxAge ? e.View : null;

    /// <summary>A screen showed the board; true when its last-seen is due to be written (once a minute).</summary>
    public bool Seen(Guid board, string address, string agent)
    {
        var now = Clock.UtcNow;
        screens[(board, address, agent)] = now;
        if (screens.Count > 10_000)
        {
            foreach (var (key, at) in screens)
            {
                if (now - at > TimeSpan.FromHours(1))
                {
                    screens.TryRemove(key, out _);
                }
            }
        }
        if (written.TryGetValue(board, out var last) && now - last < TimeSpan.FromMinutes(1))
        {
            return false;
        }
        written[board] = now;
        return true;
    }

    /// <summary>The screens (address and browser) that showed the board within twice its refresh, at least a minute.</summary>
    public int ScreensNow(Guid board, int refreshSeconds)
    {
        var within = TimeSpan.FromSeconds(Math.Max(60, refreshSeconds * 2));
        var now = Clock.UtcNow;
        return screens.Count(s => s.Key.Board == board && now - s.Value <= within);
    }

    /// <summary>Since when the board's bar has this colour: now, when it just changed.</summary>
    public DateTime Since(Guid board, BoardColour colour)
    {
        var now = Clock.UtcNow;
        var entry = colours.AddOrUpdate(board, (colour, now), (_, e) => e.Colour == colour ? e : (colour, now));
        return entry.Since;
    }

    /// <summary>True when a refusal of this address for this board is due to be audited: at most once an hour.</summary>
    public bool AuditRefusal(Guid board, string address)
    {
        var now = Clock.UtcNow;
        if (refusals.TryGetValue((board, address), out var last) && now - last < TimeSpan.FromHours(1))
        {
            return false;
        }
        refusals[(board, address)] = now;
        return true;
    }

    /// <summary>For tests.</summary>
    public void Reset()
    {
        tokens.Clear();
        views.Clear();
        screens.Clear();
        written.Clear();
        colours.Clear();
        refusals.Clear();
    }
}
