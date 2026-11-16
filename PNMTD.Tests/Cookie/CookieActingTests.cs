using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Cookie;
using PNMTD.Dev;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Tests.Ai;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Cookie;

/// <summary>
/// Package nine point five with a fake model (docs/cookie-plan.md §21.7): the executor's checks, Seraph's verdicts and the
/// retry, the inbox for <i>Propose</i>, the duties' texts, the <i>AI every time</i> mails, mute and suppress, the big brother,
/// and ratings into lessons. E-mails are simulated.
/// </summary>
[TestClass]
public sealed class CookieActingTests
{
    private static readonly FakeOpenRouter router = new();
    private static readonly FakeRedmine redmine = new();
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Guid bea;
    private static Guid carl;
    private static readonly UserRights Rights = new(false, new Dictionary<Area, Level> { [Area.Monitoring] = Level.Edit, [Area.Redmine] = Level.View, [Area.Mail] = Level.Read });

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        var settings = new Dictionary<string, string?>(FakeOpenRouter.Settings) { ["Cookie:Enabled"] = "true" };
        foreach (var (k, v) in FakeRedmine.WritableSettings)
        {
            settings[k] = v;
        }
        app = new TestApp(settings: settings, services: s =>
        {
            router.Install(s);
            redmine.Install(s);
        });
        admin = app.Client(Roles.Admin);
        await using var db = app.NewDb();
        UserEntity User(string name, string email) => new()
        {
            Id = Guid.NewGuid(), Username = V1.Unique(name), NormalizedUsername = V1.Unique(name.ToLowerInvariant()), DisplayName = name, PasswordHash = "x",
            SecurityStamp = "s", Email = email,
        };
        var b = User("Bea Example", "bea@example.com");
        var c = User("Carl Example", "carl@example.com");
        db.Users.AddRange(b, c);
        await db.SaveChangesAsync();
        (bea, carl) = (b.Id, c.Id);
        // Always working hours: outside them, every message waits for a person (§8.3), and the tests run at any hour.
        await V1.AvailabilityHoursAsync(admin, []);
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await Save("actions", new ActionSettings());
        await Save("lessons", new LessonSettings());
        await V1.AvailabilityHoursAsync(admin, null);
        await using (var db = app.NewDb())
        {
            await db.CookieInstructions.Where(i => i.EndedAt == null).ExecuteUpdateAsync(u => u.SetProperty(i => i.EndedAt, Clock.UtcNow));
            await db.CookieRuntime.ExecuteUpdateAsync(u => u.SetProperty(r => r.Running, false));
        }
        app.Dispose();
    }

    [TestCleanup]
    public void Reset()
    {
        router.Answer = null;
        router.Message = null;
    }

    private static CookieExecutor Executor => app.Services.GetRequiredService<CookieExecutor>();

    private static async Task Save<T>(string section, T value)
    {
        using var response = await admin.PutAsync($"/api/v1/cookie/admin/settings/{section}",
            new StringContent(JsonSerializer.Serialize(value, CookieSettings.Json), System.Text.Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<CookieCaseEntity> ProblemAsync(string name)
    {
        var host = await V1.CreateHostAsync(admin, V1.Unique(name));
        var sensor = await V1.CreateSensorAsync(admin, host.Id, name: "ping");
        V1.AddEvents(app, sensor.Id, (Clock.UtcNow.AddHours(-6), 500, "down"));
        await app.Services.GetRequiredService<CaseBook>().RunAsync(false, CancellationToken.None);
        await using var db = app.NewDb();
        return await db.CookieCases.AsNoTracking().SingleAsync(c => c.Key == $"host:{host.Id}" && c.Open);
    }

    private static ProposedAction Email(Guid to, string subject, double confidence = 0.95) =>
        new(CookieActionKind.Email, to.ToString(), 0, null, subject, $"{subject}: please have a look.", "It is down since this morning.", [], confidence, Urgency.Important);

    private static async Task<CookieDecisionEntity> HandleAsync(ProposedAction a, CookieCaseEntity? c, Func<string, CancellationToken, Task<ProposedAction?>>? retry = null)
    {
        var p = await app.Services.GetRequiredService<PrivacyFilter>().OpenAsync(CancellationToken.None);
        return await Executor.HandleAsync(new ActionRequest(new CookieProposal(a), c, null, Rights, p, ["Monitoring"], retry), CancellationToken.None);
    }

    private static void Seraph(params string[] verdicts)
    {
        var queue = new Queue<string>(verdicts);
        router.Answer = (schema, _) => schema == nameof(SeraphVerdict)
            ? $$"""{"verdict":"{{(queue.Count > 1 ? queue.Dequeue() : queue.Peek())}}","reason":"the evidence says so"}"""
            : """{"answer":"pong"}""";
    }

    [TestMethod]
    public async Task The_executor_checks_level_target_threshold_duplicates_mute_and_Seraph()
    {
        await Save("actions", new ActionSettings { Levels = new ActionLevels { Email = new ActionRule(ActionLevel.Auto) } });
        var c = await ProblemAsync("web");
        Seraph("approve");

        Assert.AreEqual("refused", (await HandleAsync(Email(Guid.NewGuid(), "Nobody"), c)).Outcome, "an id that isn't a user");
        Assert.AreEqual("refused", (await HandleAsync(Email(bea, "web is down") with { Urgency = Urgency.Info }, c)).Outcome, "e-mail isn't for info");
        Assert.AreEqual("refused", (await HandleAsync(Email(bea, "web is down") with { Action = CookieActionKind.Pushover, Urgency = Urgency.Important }, c)).Outcome,
            "Pushover only for what is urgent");
        var done = await HandleAsync(Email(bea, "web is down"), c);
        Assert.AreEqual("done", done.Outcome, done.Detail);
        Assert.AreEqual("approve", done.Verdict);
        await using (var db = app.NewDb())
        {
            var mail = await db.CookieOutbox.SingleAsync(o => o.Id == done.OutboxId);
            Assert.AreEqual(CookieOutboxState.Simulated, mail.State);
            StringAssert.Contains(mail.Body, "PNMT's assistant", "signed");
            Assert.AreEqual("cookie", mail.Source);
        }
        Assert.AreEqual("duplicate", (await HandleAsync(Email(bea, "web is still down"), c)).Outcome, "the same action on the same person today");
        var unsure = await HandleAsync(Email(carl, "web is down", confidence: 0.3), c);
        Assert.AreEqual("proposed", unsure.Outcome, "below the threshold: a person decides");
        Assert.IsNotNull(unsure.InboxDecisionId);

        var other = await ProblemAsync("db");
        using (var mute = await admin.PostAsJsonAsync("/api/v1/cookie/admin/instructions",
            new CookieInstructionRequest("mute", "host", other.HostId.ToString(), null, "Maintenance", Clock.UtcNow.AddDays(1))))
        {
            Assert.AreEqual(HttpStatusCode.OK, mute.StatusCode, await mute.Content.ReadAsStringAsync());
        }
        Assert.AreEqual("muted", (await HandleAsync(Email(carl, "db is down"), other)).Outcome);

        var third = await ProblemAsync("dns");
        Seraph("reject");
        Assert.AreEqual("rejected", (await HandleAsync(Email(carl, "dns is down"), third)).Outcome);
        await Save("actions", new ActionSettings { Levels = new ActionLevels { Email = new ActionRule(ActionLevel.Off) } });
        Assert.AreEqual("off", (await HandleAsync(Email(carl, "dns is down"), third)).Outcome);
        router.Answer = (schema, _) => schema == nameof(SeraphVerdict) ? "not json" : """{"answer":"pong"}""";
        await Save("actions", new ActionSettings { Levels = new ActionLevels { Email = new ActionRule(ActionLevel.Auto) } });
        Assert.AreEqual("proposed", (await HandleAsync(Email(bea, "dns is down"), third)).Outcome, "without Seraph's verdict, a person");
    }

    [TestMethod]
    public async Task Seraphs_revise_gets_one_retry_and_a_proposal_runs_when_a_person_approves_it()
    {
        await Save("actions", new ActionSettings { Levels = new ActionLevels { Email = new ActionRule(ActionLevel.Propose) } });
        var c = await ProblemAsync("mail");
        Seraph("revise", "approve");
        var retried = 0;
        var log = await HandleAsync(Email(bea, "mail is dwn"), c, (reason, _) =>
        {
            retried++;
            return Task.FromResult<ProposedAction?>(Email(bea, "mail is down"));
        });
        Assert.AreEqual(1, retried);
        Assert.AreEqual("proposed", log.Outcome, "Propose: Seraph approved the retry, a person decides");
        await using (var db = app.NewDb())
        {
            Assert.IsTrue(await db.CookieDecisions.AnyAsync(d => d.CaseId == c.Id && d.Outcome == "revised"));
        }
        using var decide = await admin.PostAsJsonAsync($"/api/v1/decisions/{log.InboxDecisionId}/decide", new { choice = "approve" });
        Assert.AreEqual(HttpStatusCode.OK, decide.StatusCode, await decide.Content.ReadAsStringAsync());
        await Assert.ThrowsExactlyAsync<PNMTD.Api.ApiException>(() => Executor.RunApprovedAsync(log.Id, "a second click", CancellationToken.None),
            "it runs once");
        Assert.AreEqual("proposed", (await HandleAsync(Email(carl, "mail is down", confidence: 0.3), c)).Outcome);
        Assert.AreEqual("duplicate", (await HandleAsync(Email(carl, "mail is down again", confidence: 0.3), c)).Outcome,
            "one waiting proposal per target, whatever its age");
        await using (var db = app.NewDb())
        {
            var after = await db.CookieDecisions.SingleAsync(d => d.Id == log.Id);
            Assert.AreEqual("done", after.Outcome, after.Detail);
            StringAssert.Contains(after.Detail, "Approved by");
            Assert.IsTrue(await db.CookieOutbox.AnyAsync(o => o.Id == after.OutboxId && o.Subject == "mail is down"));
        }
    }

    [TestMethod]
    public async Task An_AI_every_time_mail_waits_for_Cookie_and_becomes_the_rules_event()
    {
        var host = await V1.CreateHostAsync(admin, V1.Unique("backup"));
        var sensor = await V1.CreateSensorAsync(admin, host.Id, name: "nightly");
        Guid ok, unsure;
        await using (var db = app.NewDb())
        {
            var rule = new MailInputRuleEntity
            {
                Id = Guid.NewGuid(), Enabled = true, Name = "Backup report", FromTest = "backup@example.com", Action = MailRuleAction.AskAi, OkCode = 201, FailCode = 501,
                DefaultCode = 200, SensorOutputId = sensor.Id,
            };
            db.MailInputs.Add(rule);
            MailLogEntity Mail(string subject) => new()
            {
                Id = Guid.NewGuid(), RemoteId = Guid.NewGuid().ToString(), From = "backup@example.com", To = "pnmt@example.com", Subject = subject,
                Content = "All 12 jobs finished.", Created = Clock.UtcNow, MessageDate = Clock.UtcNow, ProcessLog = "", ProcessedById = rule.Id,
                AiState = MailAiState.Cookie,
            };
            var first = Mail("Backup: done");
            var second = Mail("Backup: ???");
            db.MailLogs.AddRange(first, second);
            await db.SaveChangesAsync();
            (ok, unsure) = (first.Id, second.Id);
        }
        await Save("actions", new ActionSettings());
        router.Answer = (schema, json) => schema switch
        {
            nameof(MailAnswer) => json.ToJsonString().Contains("???", StringComparison.Ordinal)
                ? """{"outcome":"unknown","message":"can't tell","confidence":0.4}"""
                : """{"outcome":"ok","message":"All 12 jobs finished.","confidence":0.95}""",
            nameof(SeraphVerdict) => """{"verdict":"approve","reason":"the mail says so"}""",
            _ => """{"answer":"pong"}""",
        };
        var p = await app.Services.GetRequiredService<PrivacyFilter>().OpenAsync(CancellationToken.None);
        var result = await app.Services.GetRequiredService<CookieMail>().RunAsync(0, Rights, p, "low", (_, _) => { }, CancellationToken.None);
        Assert.AreEqual(1, result.Done);
        await using (var db = app.NewDb())
        {
            var done = await db.MailLogs.SingleAsync(m => m.Id == ok);
            Assert.IsTrue(done.Processed);
            Assert.AreEqual(MailDisposition.AiEvent, done.Disposition);
            Assert.IsTrue(await db.Events.AnyAsync(e => e.SensorId == sensor.Id && e.Code == 201 && e.Message!.StartsWith("Cookie:")));
            var waiting = await db.MailLogs.SingleAsync(m => m.Id == unsure);
            Assert.IsFalse(waiting.Processed);
            Assert.IsTrue(await db.Decisions.AnyAsync(d => d.Id == waiting.DecisionId && d.Kind == "mail.outcome"), "unknown: a person decides");
        }
    }

    [TestMethod]
    public async Task Cookie_writes_a_duty_text_when_running_and_the_template_goes_out_otherwise()
    {
        var c = await ProblemAsync("printer");
        var duty = new CookieDutyEntity { Id = 1, CaseId = c.Id, Kind = CookieDutyKind.FirstAnswerReminder, DueAt = Clock.UtcNow };
        var template = new DutyTexts.Text("template subject", "template short", "template message", "template note");
        var writer = app.Services.GetRequiredService<DutyWriter>();
        await Save("actions", new ActionSettings { CookieWritesDuties = true });
        await using (var db = app.NewDb())
        {
            await CookieLoop.EnsureAsync(db, CancellationToken.None);
            await db.CookieRuntime.ExecuteUpdateAsync(u => u.SetProperty(r => r.Running, false));
        }
        Assert.AreSame(template, await writer.WriteAsync(c, duty, template, CancellationToken.None), "stopped: the template");
        await using (var db = app.NewDb())
        {
            await db.CookieRuntime.ExecuteUpdateAsync(u => u.SetProperty(r => r.Running, true).SetProperty(r => r.State, CookieState.Sleeping));
        }
        router.Answer = (schema, _) => schema switch
        {
            nameof(DutyTextAnswer) => """{"subject":"Cookie's subject","short":"short","message":"Cookie's message","note":"Cookie's note"}""",
            nameof(SeraphVerdict) => """{"verdict":"approve","reason":"fine"}""",
            _ => """{"answer":"pong"}""",
        };
        Assert.AreEqual("Cookie's subject", (await writer.WriteAsync(c, duty, template, CancellationToken.None)).Subject);
        Seraph("reject");
        Assert.AreSame(template, await writer.WriteAsync(c, duty, template, CancellationToken.None), "Seraph rejected it: the template");
        await using (var db = app.NewDb())
        {
            await db.CookieRuntime.ExecuteUpdateAsync(u => u.SetProperty(r => r.Running, false));
        }
        await Save("actions", new ActionSettings());
    }

    [TestMethod]
    public async Task A_draft_counts_only_once_confirmed_and_stops_counting_when_ended()
    {
        var c = await ProblemAsync("switch");
        var instructions = app.Services.GetRequiredService<Instructions>();
        var draft = await instructions.CreateAsync(new InstructionDraft("suppress", "channel", "email", "E-mail", "Not at night", Clock.UtcNow.AddDays(2)),
            confirmed: false, "chat", TestUsers.Admin, "Ada Admin", CancellationToken.None);
        Assert.IsNull(Instructions.Blocks(await instructions.ActiveAsync(CancellationToken.None), CookieActionKind.Email, c, []), "a draft does nothing");
        using (var confirm = await admin.PostAsync($"/api/v1/cookie/instructions/{draft.Id}/confirm", null))
        {
            Assert.AreEqual(HttpStatusCode.OK, confirm.StatusCode, await confirm.Content.ReadAsStringAsync());
        }
        Assert.IsNotNull(Instructions.Blocks(await instructions.ActiveAsync(CancellationToken.None), CookieActionKind.Email, c, []));
        Assert.IsNull(Instructions.Blocks(await instructions.ActiveAsync(CancellationToken.None), CookieActionKind.ObjectNote, c, []), "a note inside PNMT isn't suppressed");
        using (var end = await admin.PostAsync($"/api/v1/cookie/instructions/{draft.Id}/end", null))
        {
            Assert.AreEqual(HttpStatusCode.OK, end.StatusCode);
        }
        Assert.IsNull(Instructions.Blocks(await instructions.ActiveAsync(CancellationToken.None), CookieActionKind.Email, c, []));
        using var tooLong = await admin.PostAsJsonAsync("/api/v1/cookie/admin/instructions",
            new CookieInstructionRequest("suppress", "channel", "pushover", null, "", Clock.UtcNow.AddDays(31)));
        Assert.AreEqual(HttpStatusCode.BadRequest, tooLong.StatusCode, "at most 30 days");
    }

    [TestMethod]
    public async Task The_big_brother_takes_the_other_model_the_second_time_and_its_limits_send_the_case_to_a_person()
    {
        var c = await ProblemAsync("core");
        var brother = app.Services.GetRequiredService<BigBrother>();
        var p = await app.Services.GetRequiredService<PrivacyFilter>().OpenAsync(CancellationToken.None);
        await Save("actions", new ActionSettings());
        Assert.AreEqual("refused", (await brother.SolveAsync(c, "why is it down", null, Rights, p, null, CancellationToken.None)).Outcome, "off by default");
        await Save("actions", new ActionSettings { BigBrother = true });
        router.Answer = (schema, _) => schema switch
        {
            nameof(Solution) => """{"cause":"the uplink port flaps","confidence":0.7,"evidence":["ping"],"alternatives":[],"steps":["Replace the cable."],"verify":"ping","watch":"errors"}""",
            nameof(SeraphVerdict) => """{"verdict":"approve","reason":"fine"}""",
            _ => "I looked at the ping sensor.",
        };
        var first = await brother.SolveAsync(c, "why is it down", null, Rights, p, null, CancellationToken.None);
        Assert.AreEqual("done", first.Outcome, first.Detail);
        var second = await brother.SolveAsync(c, "why is it down", null, Rights, p, null, CancellationToken.None);
        Assert.AreEqual("done", second.Outcome, second.Detail);
        Assert.AreNotEqual(first.Model, second.Model, "a second opinion");
        var third = await brother.SolveAsync(c, "why is it down", null, Rights, p, null, CancellationToken.None);
        Assert.AreEqual("refused", third.Outcome, "2 a day per case");
        await using var db = app.NewDb();
        var solved = await db.CookieCases.SingleAsync(x => x.Id == c.Id);
        StringAssert.Contains(solved.Solution, "Replace the cable.");
        Assert.IsTrue(await db.CookieDecisions.AnyAsync(d => d.CaseId == c.Id && d.Kind == "hand-to-person"), "the limit sends it to a person");
        await Save("actions", new ActionSettings());
    }

    [TestMethod]
    public async Task Ratings_become_lessons_and_a_rule_lesson_a_suppress_object()
    {
        var c = await ProblemAsync("toner");
        await Save("actions", new ActionSettings { Levels = new ActionLevels { Email = new ActionRule(ActionLevel.Off) } });
        var log = await HandleAsync(Email(bea, "toner low"), c);
        using (var rate = await admin.PutAsJsonAsync($"/api/v1/cookie/decisions/{log.Id}/rating", new CookieRatingRequest("wrong", "No messages for toner.")))
        {
            Assert.AreEqual(HttpStatusCode.OK, rate.StatusCode, await rate.Content.ReadAsStringAsync());
        }
        using (var hidden = await app.ClientWithRights("Cookie:Use").PutAsJsonAsync($"/api/v1/cookie/decisions/{log.Id}/rating", new CookieRatingRequest("right", null)))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode, "without Monitoring, the decision isn't there");
        }
        var mine = new Lesson(Guid.NewGuid(), "Printers are the service desk's.");
        await Save("lessons", new LessonSettings { Lessons = [mine] });
        router.Answer = (schema, _) => schema == nameof(LessonsAnswer)
            ? """{"lessons":[{"text":"No e-mail about toner.","suppressScope":"channel","suppressValue":"email"},{"text":"Say which printer.","suppressScope":"","suppressValue":""}]}"""
            : """{"answer":"pong"}""";
        Assert.AreEqual(2, await app.Services.GetRequiredService<Lessons>().SummariseAsync(CancellationToken.None));
        var all = await (await admin.GetAsync("/api/v1/cookie/admin/settings")).ReadAsync<CookieAllSettings>();
        CollectionAssert.Contains(all.Lessons.Lessons.Select(l => l.Text).ToList(), "Say which printer.");
        Assert.IsTrue(all.Lessons.Lessons.Single(l => l.Id == mine.Id).ByPerson, "a person's lesson stays when the summariser runs");
        var active = await app.Services.GetRequiredService<Instructions>().ActiveAsync(CancellationToken.None);
        Assert.IsTrue(active.Any(i => i.Source == "lesson" && i.Kind == "suppress" && i.Value == "email"));
        var prompt = await app.Services.GetRequiredService<CookieBrain>().ValuesAsync(CancellationToken.None);
        StringAssert.Contains(prompt["lessons"], "Say which printer.");
        StringAssert.Contains(prompt["instructions"], "suppress channel");
        await using var db = app.NewDb();
        await db.CookieInstructions.Where(i => i.Source == "lesson").ExecuteUpdateAsync(u => u.SetProperty(i => i.EndedAt, Clock.UtcNow));
        await Save("lessons", new LessonSettings());
    }

    [TestMethod]
    public async Task Without_a_writable_Redmine_Cookie_proposes_no_tickets_and_says_what_it_does_instead()
    {
        await Save("actions", new ActionSettings());
        var brain = app.Services.GetRequiredService<CookieBrain>();
        var all = await brain.ActingAsync(IntegrationsReading.Everything, CancellationToken.None);
        StringAssert.Contains(all, "pushover, email, ticket, private-note, public-answer, object-note, and hand-to-person");
        Assert.IsFalse(all.Contains("no ticket system", StringComparison.OrdinalIgnoreCase));

        var none = CookieIntegrations.Read(new PNMTD.Redmine.RedmineOptions(), new PNMTD.Zabbix.ZabbixOptions(), new PNMTD.Zabbix.ZabbixSnapshot.State([], null, null, null),
            null, false, Clock.UtcNow);
        var acting = await brain.ActingAsync(none, CancellationToken.None);
        StringAssert.Contains(acting, "pushover, email, object-note, and hand-to-person");
        StringAssert.Contains(acting, "There is no ticket system you can write to now");
        StringAssert.Contains(acting, "e-mail to whoever is responsible (PeopleYouMayAddress), important; when it is urgent, Pushover too");
        var chat = await brain.ChatActingAsync(none, CancellationToken.None);
        StringAssert.Contains(chat, CookieBrain.ChatNoProposal);
        StringAssert.Contains(chat, "pushover (only for what is urgent");
        StringAssert.Contains(chat, "email (for what is important or urgent");
        Assert.IsFalse(chat.Contains("ticket (", StringComparison.Ordinal), "no ticket from the chat's list either");
        StringAssert.Contains(chat, "Admin → Debug");
        await Save("actions", new ActionSettings { Levels = new ActionLevels { Pushover = new ActionRule(ActionLevel.Off), Email = new ActionRule(ActionLevel.Off),
            ObjectNote = new ActionRule(ActionLevel.Off) } });
        StringAssert.Contains(await brain.ChatActingAsync(none, CancellationToken.None), "You only watch", "the ticket actions are on, but there is no Redmine");
        StringAssert.StartsWith(await brain.ActingAsync(none, CancellationToken.None), CookieBrain.WatchOnly);
        await Save("actions", new ActionSettings());

        // Unreachable for longer than the grace period (docs/cookie-fallback-plan.md §3.3): the executor refuses a ticket with why, the ticket
        // tool isn't offered, and the admin tabs see it; the first successful read brings everything back.
        var c = await ProblemAsync("nas");
        await using (var db = app.NewDb())
        {
            var runtime = await CaseBook.RuntimeAsync(db, CancellationToken.None);
            runtime.CaseBookError = "Redmine: 502 Bad Gateway";
            runtime.TicketsAt = Clock.UtcNow - CookieIntegrations.Grace - TimeSpan.FromMinutes(1);
            await db.SaveChangesAsync();
        }
        try
        {
            var ticket = new ProposedAction(CookieActionKind.Ticket, null, 0, null, "nas is down", "The NAS has been down since this morning.", "Nobody took care.", [],
                0.95, Urgency.Important);
            var refused = await HandleAsync(ticket, c);
            Assert.AreEqual("refused", refused.Outcome, refused.Detail);
            StringAssert.Contains(refused.Detail, "Redmine is unreachable since");
            StringAssert.Contains(refused.Detail, "what matters goes by e-mail");
            var note = new ProposedAction(CookieActionKind.PrivateNote, null, 1, null, "nas", "A note.", "Because.", [], 0.95, Urgency.Important);
            StringAssert.Contains((await HandleAsync(note, c)).Detail, "no note on a ticket");

            var tools = CookieTools.For(Rights, app.Services).Select(t => t.Name).ToList();
            Assert.IsFalse(tools.Contains("ticket_get"), string.Join(",", tools));
            Assert.IsTrue(tools.Contains("host_get"));
            var overview = await admin.GetFromJsonAsync<JsonElement>("/api/v1/cookie/admin/overview");
            var integrations = overview.GetProperty("acting").GetProperty("integrations");
            Assert.AreEqual("none", integrations.GetProperty("redmine").GetProperty("state").GetString());
            StringAssert.Contains(integrations.GetProperty("redmine").GetProperty("reason").GetString(), "502 Bad Gateway");
        }
        finally
        {
            await using var db = app.NewDb();
            var runtime = await CaseBook.RuntimeAsync(db, CancellationToken.None);
            runtime.CaseBookError = null;
            runtime.TicketsAt = Clock.UtcNow;
            await db.SaveChangesAsync();
        }
        var back = await app.Services.GetRequiredService<CookieIntegrations>().ReadAsync(CancellationToken.None);
        Assert.AreEqual(RedmineAvailability.Writable, back.Redmine, back.RedmineReason);
        Assert.IsTrue(CookieTools.For(Rights, app.Services).Any(t => t.Name == "ticket_get"), "the first successful read brings the tool back");
    }


    [TestMethod]
    public async Task A_note_on_a_host_is_a_row_of_Cookies_own_with_Monitoring_View_and_leaves_the_peoples_notes_alone()
    {
        // docs/cookie-object-notes-plan.md §3: a row, never the people's field; View suffices; nothing without Monitoring; a cap per object.
        var c = await ProblemAsync("noted");
        var hostId = c.HostId!.Value;
        await using (var db = app.NewDb())
        {
            (await db.Hosts.SingleAsync(h => h.Id == hostId)).Notes = "People's: the ISP is informed.";
            await db.SaveChangesAsync();
        }
        var note = new ProposedAction(CookieActionKind.ObjectNote, null, 0, hostId.ToString(), "Noted", "The backup runs at 02:00; the heartbeat is late then.",
            "Seen three nights in a row", [], 0.9, Urgency.Info);
        var p = await app.Services.GetRequiredService<PrivacyFilter>().OpenAsync(CancellationToken.None);
        var viewOnly = new UserRights(false, new Dictionary<Area, Level> { [Area.Monitoring] = Level.View });
        var log = await Executor.HandleAsync(new ActionRequest(new CookieProposal(note), c, null, viewOnly, p, ["Monitoring"]), CancellationToken.None);
        Assert.AreEqual("done", log.Outcome, log.Detail);
        await using (var check = app.NewDb())
        {
            var row = await check.CookieObjectNotes.SingleAsync(n => n.HostId == hostId);
            Assert.AreEqual("The backup runs at 02:00; the heartbeat is late then.", row.Text);
            Assert.AreEqual(c.Id, row.CaseId);
            Assert.AreEqual(log.Id, row.DecisionId);
            CollectionAssert.AreEqual(new[] { "Monitoring" }, row.Areas);
            Assert.AreEqual("People's: the ISP is informed.", (await check.Hosts.SingleAsync(h => h.Id == hostId)).Notes, "the people's notes are untouched");
            Assert.IsTrue(await check.AuditLog.AnyAsync(l => l.EventType == AuditEvents.CookieNoteAdded && l.TargetId == hostId.ToString()));
        }
        var none = new UserRights(false, new Dictionary<Area, Level> { [Area.Redmine] = Level.View });
        var refused = await Executor.HandleAsync(new ActionRequest(new CookieProposal(note with { Text = "Another" }), c, null, none, p, ["Monitoring"]), CancellationToken.None);
        Assert.AreEqual("refused", refused.Outcome);
        StringAssert.Contains(refused.Detail, "Monitoring: View");

        // The cap (§3.5): a sensor with 20 notes already takes no more.
        var sensorId = Guid.Parse(System.Text.Json.JsonSerializer.Deserialize<List<CaseMember>>(c.Members, CookieSettings.Json)!.Single(m => m.Type == CaseMember.Sensor).Id);
        await using (var db = app.NewDb())
        {
            db.CookieObjectNotes.AddRange(Enumerable.Range(0, 20).Select(i => new CookieObjectNoteEntity
            {
                SensorId = sensorId, At = Clock.UtcNow.AddMinutes(-i), Text = $"Note {i}", Areas = ["Monitoring"],
            }));
            await db.SaveChangesAsync();
        }
        var full = await Executor.HandleAsync(new ActionRequest(new CookieProposal(note with { ObjectId = sensorId.ToString(), Text = "One more" }), c, null, Rights, p,
            ["Monitoring"]), CookieTestsCancellation);
        Assert.AreEqual("refused", full.Outcome, full.Detail);
        StringAssert.Contains(full.Detail, "20 notes of yours already");
    }

    private static CancellationToken CookieTestsCancellation => CancellationToken.None;
}
