using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Boards;
using PNMTD.Dev;
using PNMTD.Zabbix;

namespace PNMTD.Tests.Boards;

/// <summary>
/// A board's Redmine, Zabbix and general elements (docs/boards-plan.md §7.4–§7.6), against the fake Redmine and Zabbix: a private ticket
/// without its subject, people by initials, the problems by severity as the Zabbix page counts them, and the bar's rules on both.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BoardIntegrationsTests
{
    private static readonly FakeRedmine redmine = new();
    private static readonly FakeZabbix zabbix = new();
    private static TestApp app = null!;
    private static HttpClient admin = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        var settings = FakeRedmine.Settings.Concat(FakeZabbix.Settings).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value);
        app = new TestApp(settings: settings, services: s =>
        {
            redmine.Install(s);
            zabbix.Install(s);
        });
        admin = app.Client(Roles.Admin);
        zabbix.Load("70");
        await app.Services.GetRequiredService<ZabbixGroups>().ResolveAsync(true, CancellationToken.None);
        // #10 private: its subject and assignee never reach a board.
        redmine.Issues[10]["is_private"] = true;
        redmine.Issues[10]["assigned_to"] = new JsonObject { ["id"] = 7, ["name"] = "Anna Muster" };
        redmine.Issues[9]["assigned_to"] = new JsonObject { ["id"] = 8, ["name"] = "Ben Keller" };
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestMethod]
    public async Task RedmineZabbixAndTheBarAsABoardShowsThem()
    {
        var created = await (await admin.PostJsonAsync("/api/v1/boards", new { name = Api.V1.V1.Unique("integrations"), columns = 1, from = "empty" }))
            .ReadAsync<BoardDetail>();
        BoardElement E(BoardElementType type, BoardElementSettings? s = null) =>
            BoardLayouts.NewElement(type) with { Settings = BoardLayouts.Normalize(type, s ?? new BoardElementSettings()) };
        var overview = E(BoardElementType.RedmineOverview);
        var newest = E(BoardElementType.RedmineNewest, new BoardElementSettings(Rows: 20));
        var received = E(BoardElementType.RedmineReceived, new BoardElementSettings(Yesterday: true, Yellow: 0));
        var count = E(BoardElementType.ZabbixCount, new BoardElementSettings(Severity: ZabbixSeverity.Information));
        var history = E(BoardElementType.ZabbixHistory, new BoardElementSettings(Severity: ZabbixSeverity.Information));
        var duty = E(BoardElementType.OnDuty);
        var certificates = E(BoardElementType.Certificates);
        var lounge = E(BoardElementType.Lounge);
        var layout = created.Layout with
        {
            Look = created.Layout.Look with { People = BoardPeople.Initials },
            Columns = [new BoardColumn(1, [overview, newest, received, count, history, duty, certificates, lounge])],
            Bar = created.Layout.Bar with
            {
                Rules =
                [
                    new BoardRule(BoardMetric.ZabbixProblems, BoardColour.Red, 1, "Zabbix problems Disaster", new BoardRuleFilter(Severity: ZabbixSeverity.Disaster)),
                    new BoardRule(BoardMetric.RedmineUrgent, BoardColour.Yellow, 1, "urgent tickets", new BoardRuleFilter()),
                ],
            },
        };
        var saved = await admin.PutJsonAsync($"/api/v1/boards/{created.Id}", new { name = created.Name, enabled = true, layout, revision = created.Revision });
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
        var url = await (await admin.GetAsync($"/api/v1/boards/{created.Id}/url")).ReadAsync<BoardUrl>();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/pnmt-board/api/view");
        request.Headers.TryAddWithoutValidation("Authorization", $"Board {url.Url[(url.Url.LastIndexOf('/') + 1)..]}");
        var response = await app.Client().SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var view = await response.ReadAsync<BoardView>();
        BoardElementData Of(BoardElement e) => view.Elements.Single(x => x.Id == e.Id);

        Assert.AreEqual(BoardDataState.Ok, Of(overview).State, Of(overview).Message);
        CollectionAssert.AreEqual(new[] { "Open", "Urgent", "New, no one assigned", "Overdue", "Received today", "Solved today" },
            Of(overview).Redmine!.Tiles.Select(t => t.Label).ToArray());
        var tickets = Of(newest).Tickets!;
        var hidden = tickets.Single(t => t.Id == 10);
        Assert.IsTrue(hidden.Private);
        Assert.IsNull(hidden.Subject, "a private ticket shows without its subject");
        Assert.IsNull(hidden.AssignedTo);
        Assert.AreEqual("BK", tickets.Single(t => t.Id == 9).AssignedTo, "people by initials, as the board says");
        Assert.AreEqual(0, Of(received).Number!.Value, "the fixtures are from another day");
        Assert.AreEqual(0, Of(received).Number!.Yesterday);
        Assert.AreEqual(BoardColour.Yellow, Of(received).Colour, "yellow from 0");

        var shown = app.Services.GetRequiredService<ZabbixSnapshot>().Current.Problems;
        Assert.AreEqual(BoardDataState.Ok, Of(count).State, Of(count).Message);
        Assert.AreEqual(shown.Count(p => !p.Suppressed), Of(count).Zabbix!.Total, "as the Zabbix page counts them: suppressed ones out");
        Assert.AreEqual(shown.Max(p => p.Severity), Of(count).Zabbix!.Worst);
        Assert.IsTrue(Of(history).Problems!.Count > 0, "the poll that answered the count recorded them");
        Assert.IsTrue(Of(history).Problems!.All(p => p.Resolved is null || p.Resolved >= DateTime.UtcNow.AddHours(-25)));

        Assert.AreEqual(BoardDataState.Ok, Of(duty).State, Of(duty).Message);
        Assert.AreEqual(BoardDataState.Ok, Of(certificates).State, Of(certificates).Message);
        Assert.AreEqual(BoardDataState.Ok, Of(lounge).State, Of(lounge).Message);
        Assert.IsTrue(Of(lounge).Lounge!.Lines.All(l => l.From != "person" || l.Author is null || l.Author.Length <= 3), "people by initials");

        var disaster = shown.Count(p => p.Severity == ZabbixSeverity.Disaster && !p.Suppressed);
        var urgent = Of(overview).Redmine!.Tiles.Single(t => t.Tile == BoardRedmineTile.Urgent).Value;
        Assert.AreEqual(disaster > 0 ? BoardColour.Red : urgent > 0 ? BoardColour.Yellow : BoardColour.Green, view.Bar.Colour, view.Bar.Text);
        if (urgent > 0)
        {
            StringAssert.Contains(view.Bar.Text, $"{urgent} urgent tickets");
        }
    }
}
