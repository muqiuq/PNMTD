using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Certificates;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Udp;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Services.Domain;
using PNMTD.Zabbix;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Write;

/// <summary>
/// Hosts, sensors, their rules, Zabbix hosts and mail input rules (docs/mcp-plan.md §7.3): through the same services and
/// with the same audit events as the endpoints. A sensor's tokens and keys never come back (§6.4).
/// </summary>
public static class MonitoringWriteTools
{
    /// <summary>The types a token may create: those that need nothing set up in another place (§7.3).</summary>
    public static readonly SensorType[] CreatableTypes =
    [
        SensorType.SIMPLE, SensorType.HEARTBEAT, SensorType.HEARTBEAT_VALUECHECK, SensorType.PING, SensorType.VALUECHECK, SensorType.ENCAPSULADED,
        SensorType.ONE_WITHIN_TIMESPAN, SensorType.ALL_WITHIN_TIMESPAN, SensorType.UDP_NODE,
        // docs/ssl-certificate-plan.md §9: the address as parameters, the public mode's defaults; Certificates: Edit besides.
        SensorType.SSL_CERTIFICATE,
        // docs/advanced-sensor-plan.md §11: with the starter rule; its rules are made in PNMT.
        SensorType.ADVANCED,
        // docs/mcp-extensions-plan.md §3.1: the router waits for its join script, which router_join_script gives for direct SSH.
        SensorType.MIKROTIK,
    ];

    /// <summary>A new router's interval and grace period when left out, as the sensor form starts them.</summary>
    public const int RouterInterval = 300, RouterGrace = 600;

    private static readonly JsonElement NoArgs = JsonDocument.Parse("{}").RootElement.Clone();

    private static Field MikrotikField() => Nested("mikrotik", "A MIKROTIK router's settings. Management access, secrets in exports, diagnostics and " +
            "encrypted backups are an admin's, in PNMT.",
        Choice("mode", "WireGuardSsh (when left out): PNMT's tunnel and SSH through it; WireGuardOnly: the tunnel alone; DirectSsh: SSH to an address " +
            "PNMTD can reach, no tunnel", Enum.GetNames<MikrotikMode>()),
        Text("address", "Direct SSH: the router's host name or address, as PNMTD reaches it"),
        Int("sshPort", "22 when left out"),
        Text("sourceAddress", "Direct SSH: PNMTD's address (or network) as the router sees it; the pnmt user may log in only from it. Left out: from anywhere"),
        Int("backupIntervalHours", "A configuration export every 1 to 8760 hours, 0 for none; left out: the installation's default"));

    private static Field CertificateField() => Nested("certificate", "An SSL_CERTIFICATE sensor's trust and rules; on a change, what is left out stays. " +
            "Needs Certificates: Edit.",
        Choice("trust", "Public (when left out on create): public CAs; Private: the private CAs in authorityIds. Pinned is set on the sensor's page in " +
            "PNMT: it trusts the certificate a person accepted", ["Public", "Private"]),
        Ids("authorityIds", "Private: the private CAs, from certificate_authorities", required: false),
        Bool("checkName", "The certificate must name the address"), Bool("checkExpiry", "Fail before and at expiry"),
        Bool("incompleteChainFails", "A chain the server sends incomplete fails"),
        Text("serverName", "The name sent in the handshake (SNI) when it isn't the address's; an empty text clears it"),
        Text("connectTo", "Connect to this host or address instead, keeping the name; an empty text clears it"),
        Int("warnDays", "Fail this many days before expiry, 1 to 365"),
        Bool("alertOnCertificateChange", "Fail on a new certificate until accepted"), Bool("alertOnKeyChange", "Fail on a new key until accepted"),
        Bool("alertOnIssuerChange", "Fail on a new issuer until accepted"),
        Choice("issuerMatch", "How a new issuer is compared", Enum.GetNames<CertificateIssuerMatch>()));

    private static Field ZabbixField() => Nested("zabbix", "A ZABBIX sensor's rules; what is left out stays. Its Zabbix host stays: attach another with " +
            "zabbix_attach. Needs Zabbix: View.",
        Choice("minSeverity", "Problems from this severity up fail it", Enum.GetNames<ZabbixSeverity>()),
        Bool("countAcknowledged", "Acknowledged problems count"), Bool("countSuppressed", "Suppressed problems count"),
        Bool("unavailableFails", "An unavailable interface fails it"));

    private static Field UdpField() => Nested("udp", "A UDP_NODE's protocol settings (docs/udp-protocol.md); on a change, what is left out stays. Never " +
            "its key or token: they are on its page in PNMT.",
        Int("deviceId", "1 to 4294967295; a random free one on create when left out"),
        Choice("keyFamily", "HmacSha256 when left out. At creation only: a new family means a new key", Enum.GetNames<KeyFamily>()),
        Choices("allowedSuites", "The suites it accepts: None, Token32, or the family's; left out on create: the family's 32-bit MAC", Enum.GetNames<SecuritySuite>()),
        Choice("sessionMode", "None when left out on create", Enum.GetNames<UdpSessionMode>()),
        Choice("ackMode", "OnRequest when left out on create", Enum.GetNames<UdpAckMode>()),
        Bool("replayProtection", "true when left out on create"),
        Text("responseSuite", "The suite it answers in: a suite's name, or uplink for the one each uplink came with"));

    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("host_create", "Create a host", "A new host: its name, the location it belongs to (or a location text), notes, importance.",
            Schema(Text("name", "2 to 64 characters", true), Id("locationId", "The location it belongs to", required: false),
                Text("location", "A location text (rack, room) when it has no location"), Text("notes", "Notes"), Bool("enabled", "true when left out"),
                Choice("importance", "Normal when left out", Enum.GetNames<Importance>())),
            Area.Monitoring, Level.Edit, ToolKind.Write, HostCreateAsync) { AuditEvents = [AuditEvents.HostCreated] },
        new("host_update", "Change a host", "Changes the given fields of a host; the others stay as they are.",
            Schema(Id("id", "The host's id"), Text("name", "2 to 64 characters"), Id("locationId", "The location it belongs to", required: false),
                Bool("clearLocation", "true to take it out of its location"), Text("location", "The location text"), Text("notes", "Notes; an empty text clears them"),
                Bool("enabled", "Monitored or not"), Choice("importance", "Its importance", Enum.GetNames<Importance>())),
            Area.Monitoring, Level.Edit, ToolKind.Write, HostUpdateAsync) { AuditEvents = [AuditEvents.HostUpdated], Idempotent = true },
        new("hosts_set_location", "Move hosts to a location", "Puts up to 100 hosts into a location, or out of any.",
            Schema(Ids("hostIds", "The hosts' ids, 1 to 100"), Id("locationId", "The location; left out takes them out of their location", required: false)),
            Area.Locations, Level.Edit, ToolKind.Write, HostsLocationAsync) { AuditEvents = [AuditEvents.HostsLocationChanged], Idempotent = true },
        new("hosts_set_importance", "Set hosts' importance", "Sets the importance of up to 100 hosts.",
            Schema(Ids("hostIds", "The hosts' ids, 1 to 100"), Choice("importance", "The importance", Enum.GetNames<Importance>(), true)),
            Area.Monitoring, Level.Edit, ToolKind.Write, HostsImportanceAsync) { AuditEvents = [AuditEvents.HostsImportanceChanged], Idempotent = true },
        new("sensor_create", "Create a sensor",
            $"A new sensor on a host, of one of these types: {string.Join(", ", CreatableTypes)}. sensor_types says what each does and what its " +
            "parameters mean. A type's own settings go in its object (mikrotik, certificate, udp). Its URL, tokens and keys are on its page in PNMT, " +
            "never here. A MIKROTIK router then needs its join script: router_join_script gives it for direct SSH; the WireGuard modes' is on the " +
            "sensor's page in PNMT. Servers with an agent are added in PNMT, Zabbix sensors with zabbix_attach.",
            Schema(Id("hostId", "The host"), Choice("type", "The sensor type", CreatableTypes.Select(t => t.ToString()), true), Text("name", "1 to 200 characters", true),
                Int("intervalSeconds", $"The check or report interval; needed for heartbeats, ping and UDP nodes; a router's {RouterInterval} when left out"),
                Int("gracePeriodSeconds", $"How late a heartbeat may be; a router's {RouterGrace} when left out"),
                Text("parameters", "The type's parameters, as sensor_types describes them (a ping target, a regular expression, host:port)"), Text("notes", "Notes"),
                Choice("importance", "Left out: the host's", Enum.GetNames<Importance>()), Bool("enabled", "true when left out"),
                MikrotikField(), CertificateField(), UdpField()),
            Area.Monitoring, Level.Edit, ToolKind.Write, SensorCreateAsync) { AuditEvents = [AuditEvents.SensorCreated] },
        new("sensor_update", "Change a sensor",
            "Changes the given fields of a sensor (name, enabled, ignored, interval, grace period, parameters, notes, importance, and a type's own " +
            "settings in certificate, zabbix or udp); the others, its type, tokens and keys stay as they are. A router's settings stay as they are. " +
            "Disabling or ignoring a sensor is how to silence it.",
            Schema(Id("id", "The sensor's id"), Text("name", "1 to 200 characters"), Bool("enabled", "Monitored or not"), Bool("ignore", "Ignored: its failures don't count"),
                Int("intervalSeconds", "The check or report interval"), Int("gracePeriodSeconds", "How late a heartbeat may be"), Text("parameters", "The type's parameters"),
                Text("notes", "Notes; an empty text clears them"), Choice("importance", "Its importance", Enum.GetNames<Importance>()),
                Bool("hostImportance", "true to take the host's importance instead of its own"),
                CertificateField(), ZabbixField(), UdpField()),
            Area.Monitoring, Level.Edit, ToolKind.Write, SensorUpdateAsync) { AuditEvents = [AuditEvents.SensorUpdated], Idempotent = true },
        new("sensors_set_importance", "Set sensors' importance", "Sets the importance of up to 100 sensors, or gives them their host's again.",
            Schema(Ids("sensorIds", "The sensors' ids, 1 to 100"), Choice("importance", "The importance", Enum.GetNames<Importance>()),
                Bool("hostImportance", "true to give them their host's importance instead")),
            Area.Monitoring, Level.Edit, ToolKind.Write, SensorsImportanceAsync) { AuditEvents = [AuditEvents.SensorsImportanceChanged], Idempotent = true },
        new("sensor_notification_rule", "Link a sensor to a rule",
            "Links a sensor to an existing notification rule, or unlinks it. Rules themselves and their recipients are changed in PNMT only.",
            Schema(Id("sensorId", "The sensor"), Id("ruleId", "The notification rule"), Choice("action", "link or unlink", ["link", "unlink"], true)),
            Area.Monitoring, Level.Edit, ToolKind.Write, RuleLinkAsync)
        { AuditEvents = [AuditEvents.NotificationRuleSensorLinked, AuditEvents.NotificationRuleSensorUnlinked], Idempotent = true },
        new("zabbix_attach", "Attach a Zabbix host",
            "Attaches a Zabbix host to a PNMT host, or to a new one with the given name, as a Zabbix sensor that fails while the Zabbix host has problems " +
            "at or above the severity.",
            Schema(Int("zabbixHostId", "The Zabbix host's id", true), Id("hostId", "The PNMT host", required: false), Text("newHostName", "A new PNMT host's name instead"),
                Choice("minSeverity", "Left out: PNMT's default", Enum.GetNames<ZabbixSeverity>())),
            Area.Monitoring, Level.Edit, ToolKind.Write, ZabbixAttachAsync) { AuditEvents = [AuditEvents.ZabbixHostsAttached], Requires = [(Area.Zabbix, Level.View)] },
        new("mail_rule_create", "Create a mail input rule",
            "A rule that turns incoming mails into a sensor's events: tests on the sender, subject and body (regular expressions), the codes it sets, " +
            "the sensor it writes to.",
            MailRuleSchema(required: true), Area.Monitoring, Level.Edit, ToolKind.Write, MailRuleCreateAsync) { AuditEvents = [AuditEvents.MailInputRuleCreated] },
        new("mail_rule_update", "Change a mail input rule", "Changes the given fields of a mail input rule; the others stay as they are.",
            MailRuleSchema(required: false, id: true), Area.Monitoring, Level.Edit, ToolKind.Write, MailRuleUpdateAsync)
        { AuditEvents = [AuditEvents.MailInputRuleUpdated], Idempotent = true },
    ];

    private static System.Text.Json.Nodes.JsonObject MailRuleSchema(bool required, bool id = false) => Schema([
        .. id ? [Id("id", "The rule's id")] : Array.Empty<Field>(),
        Text("name", "The rule's name", required), Bool("enabled", "true when left out"),
        Text("fromTest", "A regular expression on the sender"), Text("subjectTest", "On the subject"), Text("bodyTest", "On the body"),
        Text("okTest", "When it matches, the event is OK"), Int("okCode", "The OK code (100–399)"), Text("failTest", "When it matches, the event fails"),
        Int("failCode", "The failure code (400–599)"), Int("defaultCode", "The code otherwise; 200 when left out"),
        Text("extractMessageRegex", "A regular expression whose first group becomes the event's message"),
        Id("sensorId", "The sensor it writes to", required: false),
        Choice("action", "What it does with a match", Enum.GetNames<MailRuleAction>()),
        Bool("askAiWhenUnsure", "Ask the AI when no test decides"), Bool("verifyAi", "Have a second model check the AI's answer"),
    ]);

    private static object HostResult(ToolContext ctx, HostDetail h)
    {
        ctx.Target("Host", h.Id, h.Name);
        return new
        {
            h.Id, h.Name, h.Location, LocationId = h.AssignedLocation?.Id, h.Notes, h.Enabled, h.Importance, h.State, Sensors = h.SensorCounts,
            Url = ctx.Link($"hosts/{h.Id}"),
        };
    }

    private static async Task<object> HostCreateAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var host = await ctx.Services.GetRequiredService<HostService>().CreateAsync(new HostRequest
        {
            Name = TextOf(args, "name", 2, 64),
            LocationId = OptionalId(args, "locationId"),
            Location = OptionalText(args, "location", 128),
            Notes = OptionalText(args, "notes", 4000),
            Enabled = OptionalBool(args, "enabled") ?? true,
            Importance = OptionalEnum<Importance>(args, "importance") ?? Importance.Normal,
        }, ct);
        ctx.Summary = $"created host {host.Name}";
        return HostResult(ctx, host);
    }

    private static async Task<object> HostUpdateAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var hosts = ctx.Services.GetRequiredService<HostService>();
        var current = await hosts.GetAsync(id, ct);
        var notes = OptionalText(args, "notes", 4000);
        var host = await hosts.UpdateAsync(id, new HostRequest
        {
            Name = OptionalText(args, "name", 64) ?? current.Name,
            LocationId = OptionalBool(args, "clearLocation") == true ? null : OptionalId(args, "locationId") ?? current.AssignedLocation?.Id,
            Location = OptionalText(args, "location", 128) ?? current.Location,
            Notes = notes is null ? current.Notes : notes.Length == 0 ? null : notes,
            Enabled = OptionalBool(args, "enabled") ?? current.Enabled,
            Importance = OptionalEnum<Importance>(args, "importance") ?? current.Importance,
        }, ct);
        ctx.Summary = $"changed host {host.Name}";
        return HostResult(ctx, host);
    }

    private static async Task<object> HostsLocationAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var ids = IdsOf(args, "hostIds", 100);
        var location = OptionalId(args, "locationId");
        var changed = await ctx.Services.GetRequiredService<LocationService>().SetHostsAsync(new HostLocationRequest { Ids = ids, LocationId = location }, ct);
        ctx.Summary = $"{changed} of {ids.Count} hosts moved";
        return new { Changed = changed, LocationId = location };
    }

    private static async Task<object> HostsImportanceAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var ids = IdsOf(args, "hostIds", 100);
        var importance = OptionalEnum<Importance>(args, "importance") ?? throw ToolException.Invalid("importance is needed.");
        var changed = await ctx.Services.GetRequiredService<HostService>().SetImportanceAsync(new ImportanceRequest { Ids = ids, Importance = importance }, ct);
        ctx.Summary = $"{changed} of {ids.Count} hosts set to {importance}";
        return new { Changed = changed, Importance = importance };
    }

    private static async Task<object> SensorResultAsync(ToolContext ctx, PnmtdDbContext db, Guid id, CancellationToken ct)
    {
        var s = await db.Sensors.AsNoTracking().Where(x => x.Id == id).Select(x => new
        {
            x.Id, x.Name, x.Type, x.ParentId, Host = x.Parent.Name, x.Enabled, x.Ignore, x.Interval, x.GracePeriod, x.Parameters, x.Notes, x.Importance,
        }).SingleAsync(ct);
        ctx.Target("Sensor", s.Id, $"{s.Host} / {s.Name}");
        var router = s.Type == SensorType.MIKROTIK
            ? await db.MikrotikRouters.AsNoTracking().Where(r => r.SensorId == id).Select(r => new { r.Mode, r.SetupState, r.Address, r.SshPort, r.BackupIntervalHours })
                .SingleOrDefaultAsync(ct)
            : null;
        return new
        {
            s.Id, s.Name, Type = s.Type.ToString(), HostId = s.ParentId, s.Host, s.Enabled, s.Ignore, IntervalSeconds = s.Interval, GracePeriodSeconds = s.GracePeriod,
            Parameters = ToolSafety.WithoutCredentials(s.Parameters), s.Notes, OwnImportance = s.Importance,
            Router = router,
            Next = router is null ? null
                : router.SetupState != MikrotikSetupState.AwaitingRouter ? null
                : router.Mode == MikrotikMode.DirectSsh
                    ? "Fetch the join script with router_join_script and paste it into the router's terminal (or import it as a .rsc file)."
                    : "The join script is on the sensor's page in PNMT: it holds the router's WireGuard private key, so it never comes over MCP.",
            Note = "Its URL, tokens and keys are on its page in PNMT.", Url = ctx.Link($"sensors/{s.Id}"),
        };
    }

    /// <summary>The objects a type's own settings come in (docs/mcp-extensions-plan.md §3): only the sensor's type's.</summary>
    private static readonly (string Name, SensorType Type)[] TypeObjects =
    [
        ("mikrotik", SensorType.MIKROTIK), ("certificate", SensorType.SSL_CERTIFICATE), ("zabbix", SensorType.ZABBIX), ("udp", SensorType.UDP_NODE),
    ];

    private static void OnlyTheTypes(JsonElement args, SensorType type)
    {
        foreach (var (name, of) in TypeObjects)
        {
            if (Has(args, name) && of != type)
            {
                throw ToolException.Invalid($"{name} is for a {of} sensor, not a {type} one.");
            }
        }
    }

    private static void CertificatesEdit(ToolContext ctx)
    {
        if (!ctx.Rights.Has(Area.Certificates, Level.Edit))
        {
            // Cookie calls without a request, so SensorService can't ask its caller (docs/ssl-certificate-plan.md §6.1).
            throw new ToolException("missing_right", "An SSL certificate sensor's settings need Certificates: Edit besides Monitoring: Edit.");
        }
    }

    /// <summary>A new router's settings (§3.1): never the admin's; the backup schedule left out is the installation's.</summary>
    private static async Task<MikrotikRouterRequest> MikrotikAsync(JsonElement? o, PnmtdDbContext db, CancellationToken ct)
    {
        var m = o ?? NoArgs;
        int? hours;
        if (Has(m, "backupIntervalHours"))
        {
            hours = IntOf(m, "backupIntervalHours") is var h && h == 0 ? null : h;
        }
        else
        {
            var defaults = await db.MikrotikSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == MikrotikSettingsEntity.SingletonId, ct);
            hours = defaults is null ? 24 : defaults.DefaultBackupIntervalHours;
        }
        return new MikrotikRouterRequest
        {
            Mode = OptionalEnum<MikrotikMode>(m, "mode") ?? MikrotikMode.WireGuardSsh,
            Address = OptionalText(m, "address", 253),
            SshPort = IntOf(m, "sshPort", 22),
            SourceAddress = OptionalText(m, "sourceAddress", 64),
            BackupIntervalHours = hours,
        };
    }

    /// <summary>An SSL sensor's settings (§3.3), merged with its current ones; never switched to Pinned.</summary>
    private static async Task<CertificateSensorRequest?> CertificateAsync(JsonElement? o, Guid? sensorId, PnmtdDbContext db, CancellationToken ct)
    {
        if (o is not { } c)
        {
            return null;
        }
        var existing = sensorId is { } id ? await db.CertificateSensors.AsNoTracking().SingleOrDefaultAsync(x => x.SensorId == id, ct) : null;
        var trust = OptionalEnum<CertificateTrust>(c, "trust");
        if (trust == CertificateTrust.Pinned && existing?.Trust != CertificateTrust.Pinned)
        {
            throw ToolException.Invalid("Pinned trusts the certificate a person accepted: it is set on the sensor's page in PNMT.");
        }
        string? Kept(string name, string? old) => OptionalText(c, name, 253) is { } t ? (t.Length == 0 ? null : t) : old;
        return new CertificateSensorRequest
        {
            Trust = trust ?? existing?.Trust ?? CertificateTrust.Public,
            AuthorityIds = OptionalIds(c, "authorityIds", 50)?.ToList(),
            CheckName = OptionalBool(c, "checkName"),
            CheckExpiry = OptionalBool(c, "checkExpiry"),
            IncompleteChainFails = OptionalBool(c, "incompleteChainFails"),
            ServerName = Kept("serverName", existing?.ServerName),
            ConnectTo = Kept("connectTo", existing?.ConnectTo),
            WarnDays = Has(c, "warnDays") ? IntOf(c, "warnDays") : null,
            AlertOnCertificateChange = OptionalBool(c, "alertOnCertificateChange"),
            AlertOnKeyChange = OptionalBool(c, "alertOnKeyChange"),
            AlertOnIssuerChange = OptionalBool(c, "alertOnIssuerChange"),
            IssuerMatch = OptionalEnum<CertificateIssuerMatch>(c, "issuerMatch"),
        };
    }

    /// <summary>A Zabbix sensor's rules (§3.4), merged with its current ones; its Zabbix host stays.</summary>
    private static async Task<ZabbixSensorRequest?> ZabbixAsync(JsonElement? o, ToolContext ctx, Guid sensorId, PnmtdDbContext db, CancellationToken ct)
    {
        if (o is not { } z)
        {
            return null;
        }
        if (!ctx.Rights.Has(Area.Zabbix, Level.View))
        {
            throw new ToolException("missing_right", "A Zabbix sensor's rules need Zabbix: View besides Monitoring: Edit.");
        }
        var row = await db.ZabbixSensors.AsNoTracking().SingleOrDefaultAsync(x => x.SensorId == sensorId, ct)
            ?? throw ToolException.NotFound("Zabbix sensor");
        return new ZabbixSensorRequest
        {
            ZabbixHostId = row.ZabbixHostId,
            MinSeverity = OptionalEnum<ZabbixSeverity>(z, "minSeverity") ?? row.MinSeverity,
            CountAcknowledged = OptionalBool(z, "countAcknowledged") ?? row.CountAcknowledged,
            CountSuppressed = OptionalBool(z, "countSuppressed") ?? row.CountSuppressed,
            UnavailableFails = OptionalBool(z, "unavailableFails") ?? row.UnavailableFails,
        };
    }

    /// <summary>A UDP node's settings (§3.5), merged with its current ones; never a key, and never a new key family on a change.</summary>
    private static async Task<UdpNodeRequest?> UdpAsync(JsonElement? o, Guid? sensorId, PnmtdDbContext db, CancellationToken ct)
    {
        if (o is not { } u)
        {
            return null;
        }
        var node = sensorId is { } id ? await db.UdpNodes.AsNoTracking().SingleOrDefaultAsync(n => n.SensorId == id, ct) : null;
        var family = OptionalEnum<KeyFamily>(u, "keyFamily");
        if (node is not null && family is { } f && f != node.KeyFamily)
        {
            throw ToolException.Invalid("A new key family means a new key, which cuts the device off until it has it: change it on the sensor's page in PNMT.");
        }
        var response = node?.ResponseSuite;
        if (OptionalText(u, "responseSuite", 32) is { } r)
        {
            response = r.Equals("uplink", StringComparison.OrdinalIgnoreCase) ? null
                : Enum.TryParse<SecuritySuite>(r, ignoreCase: true, out var suite) && Enum.IsDefined(suite) ? suite
                : throw ToolException.Invalid($"responseSuite is uplink or one of {string.Join(", ", Enum.GetNames<SecuritySuite>())}.");
        }
        return new UdpNodeRequest
        {
            DeviceId = OptionalLong(u, "deviceId") ?? node?.DeviceId,
            KeyFamily = family ?? node?.KeyFamily ?? KeyFamily.HmacSha256,
            AllowedSuites = OptionalEnums<SecuritySuite>(u, "allowedSuites")?.ToList()
                ?? (node is null ? null : [.. SecuritySuites.FromMask(node.AllowedSuites)]),
            SessionMode = OptionalEnum<UdpSessionMode>(u, "sessionMode") ?? node?.SessionMode ?? UdpSessionMode.None,
            AckMode = OptionalEnum<UdpAckMode>(u, "ackMode") ?? node?.AckMode ?? UdpAckMode.OnRequest,
            ReplayProtection = OptionalBool(u, "replayProtection") ?? node?.ReplayProtection ?? true,
            ResponseSuite = response,
        };
    }

    private static async Task<object> SensorCreateAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var type = OptionalEnum<SensorType>(args, "type") ?? throw ToolException.Invalid("type is needed.");
        if (!CreatableTypes.Contains(type))
        {
            throw ToolException.Invalid($"A {type} sensor is added in PNMT, not over MCP.");
        }
        OnlyTheTypes(args, type);
        if (Has(args, "zabbix"))
        {
            throw ToolException.Invalid("A Zabbix sensor is made with zabbix_attach.");
        }
        if (type == SensorType.SSL_CERTIFICATE)
        {
            CertificatesEdit(ctx);
        }
        var router = type == SensorType.MIKROTIK;
        var created = await ctx.Services.GetRequiredService<SensorService>().CreateAsync(new SensorRequest
        {
            HostId = IdOf(args, "hostId"),
            Type = type,
            Name = TextOf(args, "name", 1, 200),
            Interval = IntOf(args, "intervalSeconds", router ? RouterInterval : 0),
            GracePeriod = IntOf(args, "gracePeriodSeconds", router ? RouterGrace : 0),
            Parameters = OptionalText(args, "parameters", 4000),
            Notes = OptionalText(args, "notes", 4000),
            Importance = OptionalEnum<Importance>(args, "importance"),
            Enabled = OptionalBool(args, "enabled") ?? true,
            Mikrotik = router ? await MikrotikAsync(ObjectOf(args, "mikrotik"), db, ct) : null,
            Certificate = await CertificateAsync(ObjectOf(args, "certificate"), null, db, ct),
            Udp = await UdpAsync(ObjectOf(args, "udp"), null, db, ct),
        }, ct);
        ctx.Summary = $"created {type} sensor {created.Name}";
        return await SensorResultAsync(ctx, db, created.Id, ct);
    }

    private static async Task<object> SensorsImportanceAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var ids = IdsOf(args, "sensorIds", 100);
        var host = OptionalBool(args, "hostImportance") == true;
        var importance = OptionalEnum<Importance>(args, "importance");
        if (host == (importance is not null))
        {
            throw ToolException.Invalid("Give importance, or hostImportance: true.");
        }
        var changed = await ctx.Services.GetRequiredService<SensorService>().SetImportanceAsync(new ImportanceRequest { Ids = ids, Importance = importance }, ct);
        ctx.Summary = $"{changed} of {ids.Count} sensors set to {importance?.ToString() ?? "their host's importance"}";
        return new { Changed = changed, Importance = importance?.ToString() ?? "the host's" };
    }

    private static async Task<object> SensorUpdateAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var s = await db.Sensors.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ToolException.NotFound("sensor");
        var notes = OptionalText(args, "notes", 4000);
        OnlyTheTypes(args, s.Type);
        if (Has(args, "mikrotik"))
        {
            throw ToolException.Invalid("A router's settings are changed on its page in PNMT.");
        }
        if (Has(args, "certificate"))
        {
            CertificatesEdit(ctx);
        }
        // A type's settings left out (a UDP node's, a router's, a Zabbix or certificate sensor's) stay as they are.
        await ctx.Services.GetRequiredService<SensorService>().UpdateAsync(id, new SensorRequest
        {
            HostId = s.ParentId,
            Type = s.Type,
            Name = OptionalText(args, "name", 200) ?? s.Name,
            Enabled = OptionalBool(args, "enabled") ?? s.Enabled,
            Ignore = OptionalBool(args, "ignore") ?? s.Ignore,
            Interval = Has(args, "intervalSeconds") ? IntOf(args, "intervalSeconds") : s.Interval,
            GracePeriod = Has(args, "gracePeriodSeconds") ? IntOf(args, "gracePeriodSeconds") : s.GracePeriod,
            Parameters = OptionalText(args, "parameters", 4000) ?? s.Parameters,
            TextId = s.TextId,
            Notes = notes is null ? s.Notes : notes.Length == 0 ? null : notes,
            Importance = OptionalBool(args, "hostImportance") == true ? null : OptionalEnum<Importance>(args, "importance") ?? s.Importance,
            Certificate = await CertificateAsync(ObjectOf(args, "certificate"), id, db, ct),
            Zabbix = await ZabbixAsync(ObjectOf(args, "zabbix"), ctx, id, db, ct),
            Udp = await UdpAsync(ObjectOf(args, "udp"), id, db, ct),
        }, withSecrets: false, ct);
        ctx.Summary = $"changed sensor {s.Name}";
        return await SensorResultAsync(ctx, db, id, ct);
    }

    private static async Task<object> RuleLinkAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var sensor = IdOf(args, "sensorId");
        var rule = IdOf(args, "ruleId");
        var rules = ctx.Services.GetRequiredService<NotificationRuleService>();
        var link = OptionalText(args, "action", 8) switch
        {
            "link" => true,
            "unlink" => false,
            _ => throw ToolException.Invalid("action is link or unlink."),
        };
        if (link)
        {
            await rules.LinkAsync(rule, sensor, ct);
        }
        else
        {
            AuditScope.Current?.UseEvent(AuditEvents.NotificationRuleSensorUnlinked);
            await rules.UnlinkAsync(rule, sensor, ct);
        }
        ctx.Target("Sensor", sensor, null);
        ctx.Summary = $"{(link ? "linked" : "unlinked")} sensor and rule";
        return new { SensorId = sensor, RuleId = rule, Linked = link };
    }

    private static async Task<object> ZabbixAttachAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var zabbixHost = OptionalLong(args, "zabbixHostId") ?? throw ToolException.Invalid("zabbixHostId is needed.");
        var attached = await ctx.Services.GetRequiredService<ZabbixSensorService>().AttachAsync(new ZabbixAttachRequest
        {
            Items =
            [
                new ZabbixAttachItem
                {
                    ZabbixHostId = zabbixHost,
                    HostId = OptionalId(args, "hostId"),
                    NewHostName = OptionalText(args, "newHostName", 64),
                    MinSeverity = OptionalEnum<ZabbixSeverity>(args, "minSeverity"),
                },
            ],
        }, ct);
        var a = attached.Single();
        ctx.Target("Sensor", a.SensorId, a.Name);
        ctx.Summary = $"attached Zabbix host {a.ZabbixHostId} to {a.Host.Name}";
        return new { a.SensorId, Host = a.Host, a.ZabbixHostId, a.Name, Url = ctx.Link($"sensors/{a.SensorId}") };
    }

    private static MailInputRuleRequest MailRule(JsonElement args, MailInputRule? current) => new()
    {
        Name = OptionalText(args, "name", 200) ?? current?.Name,
        Enabled = OptionalBool(args, "enabled") ?? current?.Enabled ?? true,
        FromTest = OptionalText(args, "fromTest", 1000) ?? current?.FromTest,
        SubjectTest = OptionalText(args, "subjectTest", 1000) ?? current?.SubjectTest,
        BodyTest = OptionalText(args, "bodyTest", 1000) ?? current?.BodyTest,
        OkTest = OptionalText(args, "okTest", 1000) ?? current?.OkTest,
        OkCode = Has(args, "okCode") ? IntOf(args, "okCode") : current?.OkCode,
        FailTest = OptionalText(args, "failTest", 1000) ?? current?.FailTest,
        FailCode = Has(args, "failCode") ? IntOf(args, "failCode") : current?.FailCode,
        DefaultCode = Has(args, "defaultCode") ? IntOf(args, "defaultCode") : current?.DefaultCode ?? 200,
        ExtractMessageRegex = OptionalText(args, "extractMessageRegex", 1000) ?? current?.ExtractMessageRegex,
        SensorId = OptionalId(args, "sensorId") ?? current?.Sensor?.Id,
        Action = OptionalEnum<MailRuleAction>(args, "action") ?? current?.Action ?? MailRuleAction.Event,
        AskAiWhenUnsure = OptionalBool(args, "askAiWhenUnsure") ?? current?.AskAiWhenUnsure ?? false,
        VerifyAi = OptionalBool(args, "verifyAi") ?? current?.VerifyAi ?? true,
    };

    private static object MailRuleResult(ToolContext ctx, MailInputRule r)
    {
        ctx.Target("MailInputRule", r.Id, r.Name);
        return new
        {
            r.Id, r.Name, r.Enabled, r.FromTest, r.SubjectTest, r.BodyTest, r.OkTest, r.OkCode, r.FailTest, r.FailCode, r.DefaultCode, r.ExtractMessageRegex,
            SensorId = r.Sensor?.Id, Sensor = r.Sensor is { } x ? $"{x.HostName} / {x.Name}" : null, r.Action, r.AskAiWhenUnsure, r.VerifyAi,
            Url = ctx.Link("mail/rules"),
        };
    }

    private static async Task<object> MailRuleCreateAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var rule = await ctx.Services.GetRequiredService<MailService>().CreateRuleAsync(MailRule(args, null), ct);
        ctx.Summary = $"created mail input rule {rule.Name}";
        return MailRuleResult(ctx, rule);
    }

    private static async Task<object> MailRuleUpdateAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var mail = ctx.Services.GetRequiredService<MailService>();
        var rule = await mail.UpdateRuleAsync(id, MailRule(args, await mail.GetRuleAsync(id, ct)), ct);
        ctx.Summary = $"changed mail input rule {rule.Name}";
        return MailRuleResult(ctx, rule);
    }
}
