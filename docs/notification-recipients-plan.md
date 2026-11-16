# Plan: Notification rules to a user, a group, or the responsible of the day

| | |
|---|---|
| Status | **Built (2026-11-16), §13. Released in 1.12.0.** |
| Date | 2026-11-16 |
| Scope | A notification rule can send to **a PNMT user**, to **every member of a group**, or to **whoever the availability plan makes responsible**, each reached at the e-mail address and Pushover key on their account. **The address a rule takes today stays a kind of its own, configured and sent exactly as it is now.** A rule to the responsible has a stand-in for when nobody is, and says whether it alerts at any hour or in working hours only. The list, the rule's page, the manager's matrix and the coverage show whom a rule reaches now. |
| Goals | <ol><li>**A rule can name a person or a team PNMT knows,** not only an address (R1). **The address stays a full kind of its own, entered, validated and sent exactly as today** (R7): for webhooks, shared mailboxes, people outside PNMT, and anyone who prefers it.</li><li>**A team is a group,** and a new member is alerted without anyone touching a rule (R2).</li><li>**"Whoever is responsible today" is a recipient,** following the plan as it changes, with its fallbacks (R3).</li><li>**A rule that reaches nobody is visible,** on its page, in the coverage and on System health, instead of silently dropping alerts (R6).</li><li>**Nothing changes for the rules that exist:** an address stays an address, and every message still goes out through the same providers with the same priority (R7).</li></ol> |
| Not in scope | Several recipients on one rule (decision 1). Skipping group members who don't work today (decision 2). Per-recipient delivery history (decision 7). Changing when a rule fires (`NotificationRuleType`), its scopes or exclusions. Cookie suppressing its own message when a rule has reached the same person (docs/cookie-plan.md §17.4): this plan makes it possible, since a delivery now has a user, but doesn't build it. An off-hours or on-call column in the availability plan. |
| Depends on | The notification rules, scopes and the manager (docs/ai-operations-plan.md §11), the providers and the debug page (§8.3), the person's contact details and the availability plan (docs/cookie-plan.md §8.1, §8.2), the groups (docs/audit-redmine-zabbix-plan.md §5), the Pushover priorities as a setting (docs/cookie-object-notes-plan.md §5), the access watch's contacts as the precedent for *a user or a group* (docs/cookie-access-plan.md §6.2). |
| Target | 1.12.0 |

**Words.** ***A rule*** is a row of `notificationrules`: a name, when it fires (`Type`), whether it is on, and whom it tells; its sensors come through links, scopes and exclusions. ***An address*** is what a rule's `Recipient` holds today: an e-mail address, `pushover://<user key>` or an `http(s)://` webhook. ***A provider*** is what sends an address: `email`, `pushover`, `http`. ***The recipient kind*** is the new choice of whom a rule tells: an address, a user, a group, or the responsible. ***Contact details*** are `users.Email` and `users.PushoverKey`, which each person sets on *Account → Contact* and admins set on the user's profile. ***The responsible*** is whoever `PlanSnapshot.ResponsibleOn` names for a day: the day's responsible if they work, else the day's fallback if they work, else the plan's global fallback, else nobody. ***The stand-in*** is this plan's addition: whom a rule to the responsible tells when the plan names nobody. ***A delivery*** is one message to one address.

---

## 1. Today

### 1.1 A rule is one address, read by its form

- **`NotificationRuleEntity`** (`PNMTD/Models/Db/NotificationRuleEntity.cs`) has `Name`, `Recipient` (`[AuditSecret]`), `Enabled`, `Type`, and the links, scopes and exclusions. **The recipient's form decides the provider:** `NotificationProviders.Match` (`PNMTD/Services/Domain/NotificationRuleService.cs`) asks `PushoverNotification.Matches` (`pushover://`), then `HttpNotification.Matches` (`http://`, `https://`), then `EMailNotification.Matches` (a mail address regex). Anything else is refused by `NotificationRuleService.Validate`: *An e-mail address, pushover://<user key> or an http(s):// webhook.*
- **The sender** is `NotificationService.SendAsync` (`PNMTD/Notifications/NotificationService.cs`): it picks the first `INotificationProvider` whose `IsMatch` takes the recipient, sends, and logs the provider and the result, **never the recipient,** because a key or a webhook URL can carry secrets. The providers return a `DeliveryResult` and never throw for a delivery problem.
- **The task** (`PNMTD/Tasks/NotificiationTask.cs`) runs every 10 seconds under a `RunGuard`. `GetAllPendingNotificationsForLastMinutes` (`PNMTD/Data/DbContextNotificationExtensions.cs`) finds the events of the last 30 minutes without a `notificationruleevent` row, pairs each with the rules that cover its sensor (links, then scopes minus exclusions, each rule once), and sets the sensor's effective importance on each `PendingNotification`. For each pair the task evaluates `NotificationRuleTriggerLogic.Eval`, and if the rule fires, builds **one** `NotificationMessage(rule.Recipient, "Alert (sensor)" or "Resolved (sensor)", short, long, importance)` and sends it, simulated when `Development:DoNotSendNotifications` is set. It then writes the `notificationruleevent` row with `NoAction`. **The result of the send is not kept** anywhere but the log.
- **For a Viewer, a Pushover key or a webhook is masked** (`NotificationProviders.Mask`: `pushover://…`, `https://host/…`); `CanSeeSecrets` (*Monitoring: Edit*) shows the whole. A mail address is shown to everyone with *Monitoring: View*.
- **The API** is `NotificationRulesController` (`api/v1/notification-rules`, *Monitoring: View*, writes *Edit*) with `NotificationRuleRequest { Name, Recipient, Type, Enabled }`, and `NotificationRuleItem`/`NotificationRuleDetail` carrying `Recipient` and `Provider`. `RuleRow` → `NotificationRuleService.ToItem` is the one place that builds an item, used by the list, by `ForSensorAsync` (the sensor's rules) and by `NotificationManagerService.CoverageAsync` (the matrix's columns).
- **The web UI:** `RulesView.vue` (the list: name, *Sends*, *Recipient* with the provider's icon, on, sensors; search over name and recipient), `RuleFormView.vue` (name, recipient as one text field with `recipientHint`, type, enabled), `RuleView.vue` (the same as inline fields; the sensors; `RuleScopes`), `NotificationsView.vue` (the matrix: a column per enabled rule, the tooltip `name · provider`), `SensorView.vue` (`humanize(rule.type) · rule.recipient`), `ruleHelp.ts` (`providerIcon`, `providerLabel`, `recipientHint`), `resources.ts` (`api.rules.*`, `ruleRequest`).
- **Elsewhere:** `OperationsReadTools.notification_rules_list` (MCP and Cookie) masks the recipient with `MaskRecipient`; `MonitoringWriteTools.sensor_notification_rule` links and unlinks only (*rules themselves and their recipients are changed in PNMT only*); `DevSeedCommand.SeedRules` seeds *Ops on-call* (a Pushover key), *Storage team* (an address) and *Chat webhook*; `SqliteImporter` copies v0.12's `notificationrules` by column, and a column 1.x added **needs a database default** (`Column.HasDefault`, as `hosts.Importance`).

### 1.2 PNMT already knows the people, and who is responsible

- **Every user carries contact details** (docs/cookie-plan.md §8.1): `UserEntity.Email` (254) and `PushoverKey` (30, `[AuditSecret]`), set on *Account → Contact* (`PUT account/contact`, everyone) and by admins on the profile; `People.ValidateContact` checks them; `People.TestAsync` sends a test to one's own address or key by building `pushover://{key}` and going through `NotificationService`. `IsActive` says whether the person may still sign in.
- **Groups** (`GroupEntity`, `UserGroupEntity`, docs/audit-redmine-zabbix-plan.md §5) are the unit of rights, and already the unit of *who is told* in two places: the plan's global fallback (`AvailabilitySettings.GlobalFallbackGroupId`) and the access watch's contacts (`AccessContact(UserId?, GroupId?, …, OutsideHours)`).
- **The availability plan answers who is responsible** (`PNMTD/Availability/AvailabilityPlan.cs`): `AvailabilityPlan.DayAsync(today)` reads a `PlanSnapshot`; `ResponsibleOn(day)` returns a `Responsibility(UserIds, As)` in the order *responsible → fallback → global fallback user → the global fallback group's members who work → nobody*, and **nobody on a closed day** (a weekend or a holiday with no hours); `ResponsibleAt(utc)` adds *nobody outside working hours*. Cookie's duties (`Recipients.ForTicket`, `Duties.ProblemByEmailAsync`), its staffing brief and the case book use it.
- **Three senders already fan out to people by their contact details,** each in its own way: `McpAlerts.TokenBlockedAsync` (every active admin, Pushover **and** e-mail, importance High), `AccessNotices` (the contacts, a user or a group's members, by the rule's `Email` and `Pushover` flags, skipping members who don't work today), and Cookie's `Outbox` (e-mail always, Pushover for what is urgent, with daily limits). **Notification rules can't reach a person at all** except by pasting their key or address.

### 1.3 What that costs

- **For a person PNMT knows, an address is the only way.** Their key goes into the rule as `pushover://uQiRzpo…` next to the same key on their account; when it changes, both change, or one stops working quietly. An address is the right thing for a webhook, a shared mailbox or someone outside PNMT, and it stays; what is missing is the choice to name the person instead.
- **A team is one shared address** or one rule per member, kept by hand. Someone who joins the team gets no alerts until someone remembers the rules.
- **Nothing follows the plan.** The plan knows who is responsible today, with fallbacks for vacations; the alerts go to a fixed phone. Cookie uses the plan for its duties; the monitoring alerts, which are the ones that matter at night, don't.
- **A rule that reaches nobody looks like coverage.** A sensor linked to a rule whose address is dead is *covered* in the matrix and the coverage; nothing in PNMT can tell, because the result of a send is only in the log.

## 2. Requirements

| | |
|---|---|
| R1 | A rule's recipient is an address (as today), a user, a group, or the responsible. A user or a group's members are reached at the contact details of their account, by e-mail and/or Pushover as the rule says; inactive users are skipped. |
| R2 | A rule to a group tells **every active member** at the time of the send. Membership is read at each send, never copied. |
| R3 | A rule to the responsible tells whom `PlanSnapshot.ResponsibleOn` names for today, at any hour by default; a rule may say *working hours only*. When the plan names nobody (a closed day, nobody works, outside hours for a *working hours only* rule), the rule's stand-in is told: a user or a group, or nobody. |
| R4 | The message says why the person got it: *to you as the responsible today*, *as a member of Storage team*, with the rule's name. |
| R5 | A removed user or group leaves the rule in place, showing *removed*; the rule then reaches nobody until it is changed. The audit records the change of every field as it does today, and names the person or group in the summary. |
| R6 | A rule's page shows whom it reaches **now** and when it last sent to whom. The coverage lists enabled rules that reach nobody, and System health counts them. A sensor covered only by such rules counts as uncovered. |
| R7 | Existing rules keep working unchanged: `RecipientKind = Address`, the same providers, the same `DeliveryResult`, the Pushover priority by importance from the settings, the same `notificationruleevent` rows. The API's old request shape (name, recipient, type, enabled) still creates an address rule. |
| R8 | Whoever may edit rules (*Monitoring: Edit*) can choose a user or a group without being an admin: a picker lists users and groups by name, with whether a user has an address or a key, never the values. |
| R9 | MCP's and Cookie's read tools don't name the people a rule reaches: *a user*, *a group (4 members)*, *the responsible*. |

## 3. The data model

### 3.1 The rule

**New columns on `notificationrules`** (migration `NotificationRecipients`); every new column has a database default, so that v0.12 imports and existing rows need nothing (`SqliteImporter.Column.HasDefault`):

| Column | Type | Default | Meaning |
|---|---|---|---|
| `RecipientKind` | smallint, not null | 0 | `NotificationRecipientKind`: `Address` = 0, `User` = 1, `Group` = 2, `Responsible` = 3. Append, never reorder. |
| `Recipient` | text, **now nullable** | | The address, for `Address` only; null for the others. Stays `[AuditSecret]`. |
| `UserId` | uuid, null, FK `users` **ON DELETE SET NULL** | | `User`: the person. `Responsible`: the stand-in user. |
| `GroupId` | uuid, null, FK `groups` **ON DELETE SET NULL** | | `Group`: the group. `Responsible`: the stand-in group. |
| `ByEmail` | boolean, not null | true | For `User`, `Group`, `Responsible`: reach people at their e-mail address. |
| `ByPushover` | boolean, not null | true | … and at their Pushover key. |
| `Hours` | smallint, not null | 0 | `NotificationHours`: `Always` = 0, `WorkingHours` = 1. Read for `Responsible` only (§4.3). |
| `LastSentAt` | timestamptz, null | | When the rule last delivered anything. `[AuditIgnore]`. |
| `LastSentTo` | varchar(500), null | | Whom, as code wrote it: *Anna Muster (responsible): Pushover, e-mail; Ben Ott: e-mail*. For an address rule, the provider's name. `[AuditIgnore]`. |

- **Navigation properties** `User` and `Group` (`UserEntity?`, `GroupEntity?`) on the entity, for the list's labels and search; the FKs in `PnmtdDbContext.OnModelCreating` next to the scopes' (`HasOne(r => r.User).WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.SetNull)`, the same for the group).
- **Stand-in: a user or a group, not both,** as the plan's global fallback (`AvailabilitySettings.Validate`: *A user or a group, not both.*). With `RecipientKind = Responsible`, `UserId` and `GroupId` are the stand-in; with `User` or `Group`, the one that applies is set and the other null; with `Address`, both null.
- **The enums** go to `PNMTD.Lib/Models/Enum/` next to `NotificationRuleType` (`NotificationRecipientKind.cs`, `NotificationHours.cs`): the sensors share the library, and the API serialises them by name like `Importance`.
- **Why SET NULL and not RESTRICT** (decision 5): deleting a user must not fail because of an alerting rule the admin can't see from the user page; the rule stays, shows *removed*, and the coverage says it reaches nobody (R5, R6).

### 3.2 What doesn't change

`notificationrulesensor`, `notification_rule_scopes`, `notification_rule_exclusions`, `notificationruleevent` (one row per rule and event, whoever and however many the rule told), `NotificationRuleType`, `NotificationRuleTriggerLogic`, the providers, `NotificationMessage`, `PushoverPriorities`.

## 4. Who is reached: `NotificationRecipients`

A new class `PNMTD/Notifications/NotificationRecipients.cs`, a singleton like `McpAlerts`, with the `AvailabilityPlan` injected and the `PnmtdDbContext` passed in (the task owns its context). It is **the one place** that turns a rule into deliveries, used by the task (§5), by the rule's page for *reaches now* (§6.2), and by the coverage (§9).

```
public sealed record Delivery(string Recipient, string Person, string Channel);   // "pushover://…", "Anna Muster", "Pushover"
public sealed record Resolved(IReadOnlyList<Delivery> Deliveries, string Who, string? Why, string? Because);
Task<Resolved> ResolveAsync(PnmtdDbContext db, NotificationRuleEntity rule, DateTime utcNow, CancellationToken ct)
```

- **`Who`** is the summary for `LastSentTo` and the page: *Anna Muster (responsible): Pushover, e-mail; Ben Ott (stand-in): e-mail*. **`Why`** is set when there are no deliveries: *nobody is responsible today, and the rule has no stand-in*; *Anna Muster has no e-mail address or Pushover key*; *the user was removed*; *Storage team has no active member with contact details*; *outside working hours, and the rule has no stand-in*. **`Because`** is the line the message carries (R4): *to you as the responsible today*, *as a member of Storage team*, *as the stand-in for the responsible*.

### 4.1 Address

One delivery: `(rule.Recipient, "", provider name)`. Nothing else is looked up. `Who` is the provider's name, so that `LastSentTo` never holds the address.

### 4.2 User and group

- **The people:** for `User`, `db.Users` where `Id == rule.UserId && IsActive`; for `Group`, `db.Users` joined with `db.UserGroups` where `GroupId == rule.GroupId && IsActive`, ordered by `DisplayName`. **Inactive users are skipped** and named in `Why` only when nobody is left.
- **The channels:** for each person, `ByPushover && PushoverKey is set` → `pushover://{key}`; `ByEmail && Email is set` → the address. The forms are exactly what `People.TestAsync` and `Outbox.DeliverAsync` build, so the same providers take them. A person with neither channel the rule allows is counted in `Who` as *(no contact details)* and skipped.
- **Everyone, not only those who work today** (decision 2). An alerting rule is a promise, not Cookie's judgement; whoever wants the plan's judgement uses `Responsible`.

### 4.3 The responsible

1. **Read the plan once:** `plan.DayAsync(today, ct)` with `today = DateOnly.FromDateTime(Clock.ToLocal(utcNow))`.
2. **Ask the plan:** with `Hours = Always`, `snapshot.ResponsibleOn(today)`; with `Hours = WorkingHours`, `snapshot.ResponsibleAt(utcNow)`, which is nobody outside working hours and on a closed day. The plan's own order applies: responsible, fallback, global fallback user, the global fallback group's working members. `Responsibility.As` goes into `Because` (*as the responsible today*, *as the fallback today*, *as the global fallback*).
3. **Nobody → the stand-in:** the rule's `UserId` (one person) or `GroupId` (every active member), **whether or not they work** today: a stand-in is for when the plan has nobody, and asking it whether the stand-in works would make it nobody again on every closed day. `Because` is *as the stand-in for the responsible*.
4. **Still nobody →** no deliveries, `Why` says which step failed.
5. The people found are reached as in §4.2.

**Why `Hours` exists** (decision 3): without it, a rule to the responsible at 03:00 wakes the day's responsible, who may not be on call, or there is no 24/7 alerting at all. `Always` keeps the contract monitoring has today (*PNMT's notification rules still alert at any hour*, docs/cookie-plan.md §8.3); `WorkingHours` makes the night the stand-in's or nobody's, as the admin decides. Two values, nothing more; the plan has no off-hours column (not in scope).

### 4.4 Cost

One query for the users, one for the members, one plan read a run (the snapshot is cached by the task for the run, §5); nothing when no rule of a person kind fires. The plan's settings and types are already cached 30 seconds in `AvailabilityPlan`.

## 5. The notification task

`NotificationTask` gets `NotificationRecipients` injected. In `doWork`, where the one `NotificationMessage` is built and sent:

- **Resolve once per rule per run:** a `Dictionary<Guid, Resolved>` for the run; the first pending notification of a rule calls `ResolveAsync`, the rest reuse it. A run is at most 30 minutes of events, and the responsible doesn't change inside one.
- **One message per delivery:** the same subject, short and long text, importance and simulate flag as today, `Recipient = delivery.Recipient`, the long text followed by a line `— Rule {name}: {Because}` for person kinds (R4). The context for the log becomes `rule {name} → {delivery.Person or provider}`; `NotificationService` still never logs the recipient.
- **After the sends:** when at least one `DeliveryResult.Ok`, set `rule.LastSentAt = Clock.UtcNow` and `rule.LastSentTo = resolved.Who` (the rule is tracked by the run's context already, `CreateNotificationRuleEventEntitiesOfPendingNotifications` attaches it; the two columns are `[AuditIgnore]`, so the audit interceptor leaves them alone). The `notificationruleevent` row is written as today.
- **Nobody:** when `Deliveries` is empty, the rule counts as fired (the trigger logic said so) but the row gets `NoAction = true`, and the task logs **a warning** once per rule per run: *Rule {name} reached nobody: {Why}*. Nothing is retried: the next event evaluates again, and by then the plan or the rule may have changed.
- **Failures** stay as they are: logged by `NotificationService`, not retried.

## 6. The API

### 6.1 The request

`NotificationRuleRequest` (`PNMTD/Api/V1/Contracts/NotificationRules.cs`) grows; every new field has a default, so today's clients and tests (`ConfigurationApiTests`, `DebugTests`, `NotificationManagerTests.RuleAsync`) keep creating address rules:

| Field | Default | Read for |
|---|---|---|
| `RecipientKind` | `Address` | all |
| `Recipient` | | `Address` only; ignored otherwise, so that `ruleRequest()` may copy the label back |
| `UserId`, `GroupId` | null | `User`, `Group`, `Responsible` (the stand-in) |
| `ByEmail`, `ByPushover` | true, true | `User`, `Group`, `Responsible` |
| `Hours` | `Always` | `Responsible` |

**Validation** (`NotificationRuleService.Validate`, now `ValidateAsync` with the database), on top of name and type:

- `Address`: as today (`NotificationProviders.Match`).
- `User`: `userId` is an existing, active user; **and the user has at least one contact detail the rule allows,** else *Anna Muster has no e-mail address or Pushover key* (a rule to one person who can't be reached is a trap, decision 6).
- `Group`: `groupId` is an existing group. An empty or unreachable group is allowed (membership changes); the page and the coverage say so (R6).
- `Responsible`: at most one of `userId`, `groupId` (*A user or a group, not both.*); each existing (active for the user).
- `User`, `Group`, `Responsible`: `byEmail || byPushover` (*Choose e-mail, Pushover or both.*).
- `Apply` sets the kind and the fields of that kind, and nulls the others.

### 6.2 The answers

`NotificationRuleItem` and `NotificationRuleDetail` get:

- `RecipientKind`; `User: Ref?` and `Group: Ref?` (id and the name as it is now; null when the FK was nulled by a deletion, which the UI shows as *removed*); `ByEmail`, `ByPushover`, `Hours`.
- **`Recipient` carries a label for person kinds,** so that every place that shows a rule (the list, the sensor page, the matrix's tooltips, the delete confirmation) needs no new logic: the user's display name, the group's name, *The responsible of the day*, or *Removed user* / *Removed group*. For `Address` it stays the address or its mask.
- **`Provider`** becomes `user`, `group` or `responsible` for the person kinds (`NotificationProviders.Match` is asked for addresses only). `ruleHelp.ts` gets the three icons and labels.
- **The detail adds `Reach`** (`NotificationReach(int People, int Deliveries, string Who, string? Why)`), computed by `NotificationRecipients.ResolveAsync` with `Clock.UtcNow`: *now: Anna Muster (responsible): Pushover, e-mail*, or `Why`. For `Address`, one delivery and the provider. And **`LastSentAt`, `LastSentTo`.**
- **`RuleRow`** grows by `RecipientKind`, `UserName`, `GroupName`, `ByEmail`, `ByPushover` (projected in `ListAsync`, `ForSensorAsync` and `CoverageAsync` from the navigation properties); `ToItem` builds the label and the provider.
- **The list's search** matches the name, the address, the user's display name and the group's name; **sort by recipient** orders by the label (`COALESCE`).

### 6.3 The pickers

`GET /api/v1/notification-rules/people` (*Monitoring: Edit*, a read: no audit attribute) → `NotificationPeople(Users, Groups)`:

- `NotificationPerson(Guid Id, string Username, string DisplayName, bool IsActive, bool HasEmail, bool HasPushover)`, active first, by display name;
- `NotificationGroupRef(Guid Id, string Name, int Members)`.

Modelled on `GET cookie/people` (`CookieService.PeopleAsync`, *Cookie: Manage*), which the rule editor can't use (R8). The booleans let the form say *Anna has no Pushover key* without ever sending a key or an address (R8). **`EndpointAuditTests`'s table** gets the row `["GET /api/v1/notification-rules/people"] = ("Monitoring:Edit", Roles.User)`.

### 6.4 Audit

The entity is `[Audited]`: every field's change is recorded as today, the two `LastSent*` columns excepted. `UserId` and `GroupId` would show as ids, so `CreateAsync` and `UpdateAsync` set `AuditLog.Summary` naming the recipient: *to Anna Muster by Pushover and e-mail*, *to Storage team by e-mail*, *to the responsible, stand-in Ops on-call, at any hour*. No new event types.

## 7. The web UI

### 7.1 A shared block: `RuleRecipientFields.vue`

One component for the form and the page, in `src/views/rules/`: a `SelectButton` *Send to*: **An address · A user · A group · The responsible**, with *An address* preselected so that creating an address rule is the same two fields as today, then the fields of that choice:

- *An address:* the text field with `recipientHint`, as today.
- *A user:* a `Select` with filter over the picker's users (display name, *deactivated*, and a hint *no e-mail address* / *no Pushover key* / *no contact details* in the option), then the channels.
- *A group:* a `Select` over the groups with the member count, then the channels.
- *The responsible:* a sentence on how the plan decides (*the day's responsible, else the fallback, else the plan's global fallback*), **Hours** (*At any hour* / *In working hours only*), **When nobody is responsible** (*Nobody* / *A user…* / *A group…* with the same pickers), then the channels.
- *The channels:* two checkboxes *E-mail* and *Pushover*, both on; a hint *People without the chosen details are skipped.*

The picker comes from a small composable `useRecipientPickers()` (`api.rules.people()`, `staleTime` a minute), after `usePickers.ts`'s `useUserOptions`/`useGroupOptions`; a chosen id that the list no longer has shows as *removed*.

### 7.2 The pages

- **`RuleFormView.vue`:** *Recipient* becomes the block; the request carries the kind and its fields. The success toast stays.
- **`RuleView.vue`:** the *Recipient* row shows the label with the provider's icon and, for person kinds, the channels and, for the responsible, the hours and the stand-in; **a *Change…* button** (with *Monitoring: Edit*) opens a dialog with the block and saves through `api.rules.update` with `ruleRequest(rule, patch)`, which now copies `recipientKind`, `userId` (`rule.user?.id`), `groupId`, `byEmail`, `byPushover`, `hours`. Two new rows: **Reaches now** (`reach.who`, or `reach.why` as a warning `Message`) and **Last sent** (`RelativeTime` of `lastSentAt` and `lastSentTo`). The delete confirmation says *stop notifying {label}*.
- **`RulesView.vue`:** the *Recipient* column shows the icon and the label (no `mono` class for person kinds); the search placeholder becomes *Search name, recipient, user or group*. The mobile row is unchanged.
- **`NotificationsView.vue`:** the columns' icon and tooltip come from `provider`, so they follow; the Coverage tab gets a warning *2 enabled rules reach nobody: Storage on duty (nobody is responsible today, and the rule has no stand-in), …* with links to the rules (§9).
- **`SensorView.vue`:** `rule.recipient` is the label; nothing to do.
- **`SystemView.vue`:** next to *Uncovered High*, *Rules reaching nobody* in the error colour when above 0.
- **`ruleHelp.ts`:** `providerIcon`: `user` → `pi pi-user`, `group` → `pi pi-users`, `responsible` → `pi pi-calendar`; `providerLabel`: *User*, *Group*, *The responsible*.
- **`schema.d.ts`** is regenerated from `openapi.json` (`tools/npm.sh run gen:api`; CI checks both).

## 8. Tools, seed, importer, docs

- **`OperationsReadTools.notification_rules_list`** returns `Recipient` as *a user*, *a group (4 members)* or *the responsible* for person kinds, plus `RecipientKind`; addresses are masked as today (R9). **`MonitoringReadTools.sensor_get`** lists a sensor's rules by id, name, type and enabled: unchanged. **`MonitoringWriteTools.sensor_notification_rule`**: unchanged; its description already says recipients are changed in PNMT only.
- **`DevSeedCommand.SeedRules`:** two rules more. *Storage on duty*: `Responsible`, stand-in the seed's Operators group, `Hours = Always`, `ONLY_FAILURES_ON_CHANGE_AND_BACK_TO_OK`, scoped to importance ≥ High. *Admin by Pushover*: `User` = the seed's admin (whom `DevSeedCookie` gives an e-mail; the seed adds a Pushover key of the right form), `ByEmail = false`. The e2e screenshot test opens *Ops on-call*, which stays as it is.
- **`SqliteImporter`:** nothing to code; the new columns have defaults (§3.1). The upgrade test (`tools/upgrade-test.sh`) covers it.
- **Docs:** `docs/ai-operations-plan.md` §11 gets a pointer to this plan under §11.4 (*the rule's page*); release notes 1.12 describe the three kinds, the migration, and that nothing changes for existing rules.

## 9. Coverage and health

- **`NotificationCoverage`** gets `RulesReachingNobody: IReadOnlyList<NotificationRuleReach>` (`Ref Rule, string Why`): every enabled rule whose `ResolveAsync` has no deliveries, computed in `UncoveredAsync` (a handful of rules; the plan read once).
- **A sensor covered only by rules that reach nobody is uncovered** (R6): `Coverage.Covered` takes the set of reaching rules instead of `Rules.Where(r => r.Enabled)`. The matrix still shows every enabled rule as a column; a column of a rule that reaches nobody carries a warning icon in its header.
- **System health** (`SystemView.vue`) shows the count from the same `coverage` query it already reads.

## 10. Tests

- **`NotificationRecipientsTests`** (new, `PNMTD.Tests/Services/`), with `TestApp`, users and a group as `DutiesTests` makes them, `V1.AvailabilityHoursAsync(admin, [])` for always-open hours where the plan matters:
  - a user with both details, `ByPushover = false` → one e-mail delivery; an inactive user → none, `Why` names it;
  - a group of three: one inactive, one without details → two people, three deliveries; `Who` lists them;
  - the responsible: the day's responsible works → them, `Because` *as the responsible today*; the day's responsible on vacation, the fallback works → the fallback; a closed day → the stand-in group's members, whether they work or not; `WorkingHours` outside hours → the stand-in; nobody and no stand-in → empty, `Why` says why;
  - an address → one delivery, `Who` is the provider.
- **`NotificationManagerTests`:** *Pending notifications to a group rule send one message per member and channel*: a fake handler on the `notifications` `HttpClient` (as `DebugTests` installs one) counts the Pushover posts and the task's `doWork` is driven through `GetAllPendingNotificationsForLastMinutes` and `NotificationRecipients`; `LastSentAt` and `LastSentTo` are set; a rule reaching nobody writes `NoAction = true` and appears in `coverage.rulesReachingNobody`, and its only sensor is uncovered.
- **`ConfigurationApiTests`:** creating a user rule for a user without details is 400 with the field `userId`; a stand-in user **and** group is 400; `byEmail = byPushover = false` is 400; the old request shape still creates an address rule; a Viewer reads the detail with the label and `reach`; deleting the user leaves the rule with `user = null` and `recipient = "Removed user"`.
- **`EndpointAuditTests`:** the new picker endpoint's right.
- **Web UI:** `ruleHelp.test.ts` for the three icons and labels; `pages.spec.ts` keeps its *page-rule* screenshot; one e2e step creates a rule to a group through the form and sees *Reaches now*.
- **`NotificationRuleTriggerLogicTest`, `NotificationServiceTest`:** unchanged, and they must stay green.

## 11. Decisions and open questions

1. **One recipient per rule, not a list.** A rule is *who, and when*; the matrix is a column per rule; several recipients are several rules, as today. A list would change the matrix, the provider icon, masking and `LastSentTo` for little: a group covers *several people*, which is the common case.
2. **A group rule tells every active member,** not only those who work today. Alerting is a promise; the plan's judgement is the `Responsible` kind's. Cookie's duties and the access watch skip non-working members because they are Cookie's; a monitoring alert at 02:00 to a team is what the team asked for.
3. **The responsible follows the plan's own order,** then the rule's stand-in; `Hours` has two values. No new rules of precedence are invented here; `PlanSnapshot.ResponsibleOn`/`ResponsibleAt` stay the one answer to *who is responsible*.
4. **Channels per rule, not per importance.** Cookie's *e-mail always, Pushover when urgent* is Cookie's policy for its own messages. For alerts the Pushover priority already follows the importance by the settings, and a person sets quiet hours on their phone. Two checkboxes are what everyone understands.
5. **Deleted user or group → SET NULL,** the rule stays and says *removed* (R5). RESTRICT would make deleting a user fail for a rule the admin can't see from the user page.
6. **A rule to a user without any allowed contact detail is refused;** a group without reachable members is allowed. One person is a fact the editor can fix now; a group's membership is the admin's and changes.
7. **No delivery table.** `LastSentAt`/`LastSentTo` on the rule answer *when and to whom did this last go*, which is what a responsible rule needs. A per-delivery history, and Cookie suppressing its message because a rule reached that person (docs/cookie-plan.md §17.4), come later on top of `Delivery.Person` having a user behind it.
8. **Names are shown with *Monitoring: View*,** as e-mail addresses are today; only keys and webhooks are masked. The tools mask the people (R9). *Monitoring: Details* isn't asked for a rule's recipient: a rule to *Anna* names her as `anna@example.com` always did.
9. **`Recipient` doubles as the label in the answers.** It spares every list, tooltip and the sensor page a second field; the request reads `recipient` for `Address` only. The alternative, a separate `Label`, is cleaner and costs a dozen template changes; take it if the doubling confuses in the build.
10. **Open:** should a rule to the responsible also tell the *planned* responsible when they don't work (vacation), so that they know what happened while away? No, by default: the plan's answer is who works. It can be a third `Hours`-like option later if asked for.
11. **Open:** whether `GET notification-rules/people` should list inactive users at all. Yes, marked, so that a rule pointing at one can be read; the picker puts them last.
12. **The address kind is kept exactly as it is, not as a leftover.** Same column, same validation (`NotificationProviders.Match`), same text field with the same hint, same providers, same masking for a Viewer, the API's default when `recipientKind` is left out, and the form's preselected choice. An address rule created in 1.12 is indistinguishable from one created in 1.11. The three new kinds are added next to it; nothing nudges anyone away from it.

## 12. Build order

1. **Entities and migration** `NotificationRecipients` (§3): the enums in `PNMTD.Lib`, the columns with defaults, the FKs, `Recipient` nullable; `dotnet ef migrations has-pending-model-changes` clean.
2. **`NotificationRecipients`** (§4) with its tests (§10, first bullet).
3. **The task** (§5): resolve once per rule, one message per delivery, `LastSent*`, the warning for nobody; the manager test for a group rule.
4. **Service and API** (§6): request, validation, `RuleRow`/`ToItem`, `Reach`, the picker endpoint, the audit summaries; `openapi.json` and `schema.d.ts` regenerated; `EndpointAuditTests`.
5. **Coverage and health** (§9).
6. **Web UI** (§7): `RuleRecipientFields.vue`, the four pages, `ruleHelp.ts`, the system page.
7. **Tools, seed, docs** (§8); release notes 1.12.

## 13. As built (2026-11-16)

Built as planned, in the build order of §12, with these particulars:

- **The data model** is §3.1 to the letter: migration `NotificationRecipients` (`20261116191919`), the booleans defaulting to true in the migration itself (not in the model, which spares EF Core's sentinel rules for a bool with a database default), the FKs SET NULL. The enums live in `PNMTD.Lib/Models/Enum/`.
- **The resolver** is `PNMTD/Notifications/NotificationRecipients.cs`, a singleton with the plan injected and the context passed in, registered in `NotificationSetup.AddNotifications`. `Resolved` has `Deliveries`, `Who`, `Why`, `Because` and a `People` count. A person without the rule's details is listed in `Who` by name alone, without their role: *Dora Nowhere (no e-mail address or Pushover key)*. When the plan names people but none can be reached, the stand-in is tried, and `Why` says both: *Nobody is responsible today … and the stand-in Dora Nowhere has no …*.
- **The task** resolves once per rule per run (`RunOnce`, internal, which the tests drive directly), sends one message per delivery with the `— Rule {name}: {Because}` line, sets `LastSentAt`/`LastSentTo` when any delivery succeeded, and writes `NoAction = true` with a warning when there is nobody.
- **The API:** `recipientKind`, `userId`, `groupId`, `byEmail`, `byPushover`, `hours` on the request; `recipientKind`, `user`, `group`, `byEmail`, `byPushover`, `hours`, `reach`, `lastSentAt`, `lastSentTo` on the detail; `recipientKind` on the item. `Recipient` carries the label (decision 9) and `Provider` the kind for the person kinds. `GET notification-rules/people` for *Monitoring: Edit*. A stand-in user without contact details is allowed (only a `User` rule's user is refused, decision 6); the resolver then reports it.
- **The audit:** for the person kinds the service's summary becomes the entry's detail (*Storage on duty: to the responsible, stand-in Storage team, in working hours only, by Pushover and e-mail*); the field changes are still recorded on the entry, as `AuditScope` keeps them apart from the detail. Address rules are audited exactly as before.
- **Coverage:** `NotificationCoverage.RulesReachingNobody`; `Covered` ignores those rules. The reach is resolved for the Coverage tab and for the matrix's *not covered* filter only, not for a bulk assignment. The matrix's column heads take the warning from the coverage query, which the view now reads on the Matrix tab too.
- **The web UI:** `RuleRecipientFields.vue` (shared block, *An address* preselected), `useRecipientPickers.ts`, `ruleHelp.ts` with `recipientDraft`/`recipientRequest`/`recipientErrors` and the labels, the four pages as in §7.2 (the rule page's *Change…* dialog, *Reaches now*, *Last sent*), the System page's count. `ruleHelp.test.ts` covers the helpers.
- **Tests:** `NotificationRecipientsTests` (8 tests) covers §10's first four bullets, including the task's sends against the fake Pushover and the coverage; `EndpointAuditTests` has the new endpoint. The whole suite is green in both time zones. `e2e/rules.spec.ts` creates a rule to a user through the form, reads *Reaches now*, changes it to an address in the dialog and deletes it, and opens the seed's rule to the responsible as a viewer. **Left out:** the upgrade test was not run in this build.
- **The seed** adds *Storage on duty* (the responsible, stand-in Operators, scope importance ≥ High) and *Admin by Pushover* (the first admin, Pushover only), and gives the admin a Pushover key. **The tools** show *a user*, *a group*, *the responsible* and the kind. **Release notes for 1.12** are written at the release, not here.

