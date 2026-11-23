using PNMTD.Api.V1.Contracts;
using PNMTD.Boards;
using PNMTD.Connectors;
using PNMTD.Data;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Zabbix;

namespace PNMTD.Cli;

/// <summary>
/// The seed's boards (docs/boards-plan.md §15): <i>NOC</i>, three columns of PNMT's monitoring, Zabbix, Redmine and the lounge, opened from
/// anywhere; and <i>Helpdesk TV</i>, two columns of tickets in the SUSE theme, only from private networks, so that a refusal can be
/// seen. Their URLs are on their Access tab. On an empty table only.
/// </summary>
internal static class DevSeedBoards
{
    public static string Seed(PnmtdDbContext db, IConfiguration configuration, DateTime now)
    {
        if (db.Boards.Any())
        {
            return "Boards exist already: left as they are.";
        }
        var protector = new BoardTokenProtector(DevSeedConnectors.Protection(configuration));
        BoardElement E(BoardElementType type, int? height = null, BoardElementSettings? settings = null) =>
            BoardLayouts.NewElement(type) with { Height = height, Settings = BoardLayouts.Normalize(type, settings ?? new BoardElementSettings()) };

        var noc = BoardLayouts.New(3, false, true) with
        {
            Look = BoardLayouts.DefaultLook with { Theme = "dark" },
            Header = BoardLayouts.DefaultHeader with { Title = "NOC" },
            Columns =
            [
                new BoardColumn(1, [E(BoardElementType.SensorSummary), E(BoardElementType.FailingSensors, 2), E(BoardElementType.Uplink)]),
                new BoardColumn(1, [E(BoardElementType.ZabbixCount), E(BoardElementType.ZabbixHistory, 1)]),
                new BoardColumn(1, [E(BoardElementType.RedmineNewest, 1), E(BoardElementType.RedmineReceived, settings: new BoardElementSettings(Yesterday: true)),
                    E(BoardElementType.RedmineSolved, settings: new BoardElementSettings(Yesterday: true)), E(BoardElementType.Lounge, 1)]),
            ],
        };
        var helpdesk = BoardLayouts.New(2, false, false) with
        {
            Look = BoardLayouts.DefaultLook with { Theme = "suse", People = BoardPeople.Initials },
            Bar = BoardLayouts.New(2, false, false).Bar with
            {
                Rules =
                [
                    new BoardRule(BoardMetric.RedmineUrgent, BoardColour.Red, 1, "urgent tickets", new BoardRuleFilter()),
                    new BoardRule(BoardMetric.RedmineUnassigned, BoardColour.Yellow, 3, "tickets nobody has", new BoardRuleFilter()),
                ],
            },
            Columns =
            [
                new BoardColumn(2, [E(BoardElementType.RedmineOverview, 1)]),
                new BoardColumn(1, [E(BoardElementType.Clock, settings: new BoardElementSettings(Date: true, Zones: [new BoardZone("America/New_York", "New York")])),
                    E(BoardElementType.Note, settings: new BoardElementSettings(Text: "**Hotline** 044 555 12 34\n\nOn call tonight: see the plan.", Large: true)),
                    E(BoardElementType.OnDuty)]),
            ],
        };
        foreach (var (name, description, layout, networks, minutes) in new[]
        {
            ("NOC", "The wall in the operations room", noc, new List<ConnectorNetwork>(), 3),
            ("Helpdesk TV", "The screen beside the helpdesk", helpdesk,
                new List<ConnectorNetwork> { new("10.0.0.0/8", "office"), new("192.168.0.0/16", "VPN"), new("fd00::/8", "office IPv6") }, 60 * 26),
        })
        {
            var token = BoardTokens.New();
            db.Boards.Add(new BoardEntity
            {
                Id = Guid.NewGuid(), Name = name, NormalizedName = BoardService.Normalize(name), Description = description, Networks = ConnectorNetworks.Write(networks),
                Layout = BoardLayouts.Write(layout), TokenHash = BoardTokens.Hash(token), TokenProtected = protector.Protect(token), TokenHint = BoardTokens.Hint(token),
                CreatedAt = now.AddDays(-3), CreatedByName = "PNMT", UpdatedAt = now.AddDays(-1), UpdatedByName = "PNMT", LastSeenAt = now.AddMinutes(-minutes),
                LastSeenAddress = networks.Count == 0 ? "198.51.100.20" : "10.20.0.31", LastSeenAgent = "Mozilla/5.0 (X11; Linux aarch64) Chromium kiosk",
            });
        }
        db.SaveChanges();
        return "Seeded 2 boards: NOC (from anywhere) and Helpdesk TV (private networks only); their URLs are on their Access tab.";
    }
}
