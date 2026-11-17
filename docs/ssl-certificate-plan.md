# Plan: SSL certificate sensors: expiry, changes, and everything the certificate says

| | |
|---|---|
| Status | **Built (2026-11-17), §15.** For release 1.13.0. |
| Date | 2026-11-17 |
| Scope | A new sensor type, **SSL certificate**. PNMTD connects to `host:port`, completes a TLS handshake and reads the certificate chain the server presents. It stores every detail of the certificate on the sensor, and shows them on a tab of their own. **The certificate may come from a public CA, from a private CA that PNMT is told to trust, or be self-signed and pinned.** The sensor fails a configurable number of days before expiry, and when the certificate has expired. It can also fail when the certificate, its key or its issuer changes unexpectedly, until a person accepts the change in a warning dialog with an explicit confirmation. A task checks each sensor at the sensor's own interval (daily by default), and *Check now* checks at once. **Who sees and changes certificates is a rights area of its own,** *Certificates*. |
| Goals | <ol><li>**Know before a certificate expires:** *about to expire* from a number of days the sensor sets, and *expired* once it has (R2, R3).</li><li>**Everything the certificate says, in PNMT:** subject, names, issuer, validity, key, fingerprints, extensions, the chain and the connection, stored with the sensor and shown on its *Certificate* tab (R4, R7).</li><li>**Notice a certificate that changes when it shouldn't:** a new certificate, a new key or a new issuer, as the sensor says, fails it as *changed unexpectedly* (R8, R9, R17).</li><li>**Public, private and self-signed certificates alike:** an internal CA is trusted where the sensor says so, and a self-signed certificate is pinned and watched for change (R13).</li><li>**A change is accepted deliberately:** a warning, the comparison, and a checkbox the person must tick (R14).</li><li>**A rights area of its own** for seeing the certificates, changing the sensors and managing the private CAs (R15).</li><li>**Checked on the sensor's schedule, or now:** from hourly to every 30 days, daily by default, and a button (R5, R6, R18).</li><li>**Quiet when nothing happens:** an event when the verdict changes, not at every check.</li></ol> |
| Not in scope | STARTTLS (SMTP, IMAP, POP3, LDAP, PostgreSQL). Revocation (OCSP, CRL). Client certificates (mTLS). Checking every address a name resolves to. Grading the server's TLS configuration (offered versions, weak ciphers): only the negotiated protocol and cipher are shown. Checks from a server's agent, for endpoints PNMTD can't reach (§13). Certificate Transparency monitoring of a domain. A list of all certificates by expiry (§13). Hiding certificate sensors themselves from people without *Certificates* rights: the sensor, its state and its events stay *Monitoring* (§6.4). |
| Depends on | The sensor pipeline: `SensorService`, `SensorEventWriter`, `MonitoringState`. The MikroTik poller as the scheduling pattern (docs/mikrotik-plan.md §5.2). The router's host key pinning as the accept pattern (docs/mikrotik-plan.md §4.4). The Zabbix sensor's side table and pure `Evaluate` (docs/audit-redmine-zabbix-plan.md §7.7). The rights catalog and groups (docs/audit-redmine-zabbix-plan.md §5). The MCP ceilings and the tool catalog (docs/mcp-plan.md §5.3, §7.3). |
| Target | 1.13.0 |

**Words.**

- ***A check*** connects to the endpoint, completes the handshake and stores what it read. It runs at the sensor's interval, or at *Check now*.
- ***An evaluation*** turns what is stored into the sensor's state. It runs every minute, without connecting anywhere.
- ***The endpoint*** is `host:port`, with an optional server name (SNI) and an optional address to connect to instead.
- ***The trust mode*** says what makes a certificate valid for the sensor:
  - ***public:*** the public CAs of PNMT's trust store;
  - ***private:*** one or more of PNMT's private CAs;
  - ***pinned:*** no CA at all, only the accepted certificate (or its key).
- ***A private CA*** is a root certificate an Admin added on *Admin → Certificates*, with its intermediates if wanted.
- ***A version*** is one distinct certificate a sensor has seen, by its SHA-256 fingerprint.
- ***The current certificate*** is the one the last successful check read.
- ***The accepted certificate*** is the one the sensor compares against: the first one it saw, or the one a person accepted.
- ***A change*** is a current certificate that differs from the accepted one in the certificate, its key (SPKI) or its issuer.
- ***The issuer***, for comparing, is the issuing CA's organisation (`O=`) by default, or its full name (§5.3).
- ***The chain as sent*** is what the server presented. ***The built chain*** is what PNMTD made of it, up to a trusted root.
- ***The chain's expiry*** is the earliest `NotAfter` of the leaf, of the intermediates the server sends and, in the private mode, of the private CA's root.
- "SSL" is the name people use. The protocol is TLS throughout.

---

## 1. Today

### 1.1 What the new type builds on

- **A sensor's state is the code of its last event:** `MonitoringState.OwnState` counts 0 to 399 as *OK* and everything above as *Failing*. There is no *warning* state, so *about to expire* is a failure code, as the request asks ("bad").
- **Uptime is computed from the intervals between events** (`MonitoringQueries.UptimeAsync`). A sensor that writes only when its verdict changes still gets correct uptime bars. The Zabbix sensor does this already.
- **PNMTD writes its own events through `SensorEventWriter.WriteAsync`:** a per-sensor advisory lock, the sensor API's dedupe, and the 2000-event cap.
- **Sensors that PNMTD checks itself follow one pattern:**
  - `RouterPoller` ticks every 10 seconds, runs one round at a time (`RunGuard`) and pauses while the uplink is down (`TaskRunDecisionMaker`).
  - A router is due when its `Interval` has passed since its last poll.
  - `RouterPollService.PollAsync(id, wait)` does one poll under a per-router `SemaphoreSlim`. Both the timer and *Poll now* call it.
- **Type-specific settings live in a 1:1 side table**, deleted with the sensor (`ZabbixSensorEntity`, `MikrotikRouterEntity`).
  - `SensorService.ValidateAsync`, `CreateAsync` and `UpdateAsync` call the type's `Validate`/`Apply`/`Remove`.
  - `SensorDetail` carries the type's detail (`Udp`, `Mikrotik`, `Zabbix`).
  - The side table is `[Audited]`, so its changes appear on the sensor's *History* tab. Its state columns are `[AuditIgnore]`.
- **The Zabbix sensor's verdict is a pure function** (`ZabbixSensorService.Evaluate`), tested without Zabbix. Its writer records `LastCode` and writes an event only when the verdict changes.
- **Pinning with a person's acceptance exists for routers:**
  - A router that presents a different SSH host key is refused (441) and the key waits in `PendingHostKey*`.
  - `AcceptHostKeyAsync` moves it to the pinned one, audit-logged (`MikrotikHostKeyAccepted`), and the router is polled again.
- **Certificate fingerprints already appear in PNMT** as SHA-256 of the DER in hex, colons and case ignored: `Redmine__CertificateSha256`, `Zabbix__CertificateSha256` (`RedmineClient.Handler`).
- **Rights:**
  - `RightsCatalog` (`Auth/Rights/Rights.cs`) is a fixed list of areas, each with ordered levels; a level includes the ones before it.
  - A type with its own area keeps its sensors under *Monitoring* and adds the area for what is its own:
    - Zabbix sensors are *Monitoring*, and the Zabbix pages are *Zabbix: View*. `zabbix_attach` `Requires` *Zabbix: View* besides *Monitoring: Edit*.
    - Routers are *Monitoring*, and accepting a host key is *MikroTik & WireGuard: Manage*.
  - **The mirrors of the catalog:**
    - `McpRights` caps a token per area (`Ceiling`, `ReadOnlyCap`);
    - the web client mirrors the levels in `api/rights.ts` (`levelOrder`);
    - *Documentation → Access* reads the catalog from the server.
  - **The built-in groups** *Operators* and *Viewers* got their rights once, in the `GroupsAndRights` migration. The areas added since (Cookie, Availability, MCP) granted nothing to them; admins grant those.
- **The catalogs** of status codes and sensor types live in `SystemService.StatusCodes()` and `SensorTypes()`. The *Documentation* page and the web client's lookups read them. `MonitoringApiTests.ReferenceDataForTheDocs` requires every `SensorType` in the types' catalog. The background tasks are listed on *System health → Background tasks*, keyed by `KeyValueKeyEnums`.
- **The runtime image** is `mcr.microsoft.com/dotnet/aspnet:10.0` (Debian), so .NET validates chains against Debian's `ca-certificates` through OpenSSL.

### 1.2 Free numbers

- **Sensor type:** the types go up to `ZABBIX = 110`. **`SSL_CERTIFICATE = 120`.**
- **Status codes:** 285 and 286, and 485 to 489, are unused. 221, 423 and 424 are documented for the AI analysis but have no constants, so they stay reserved.
- **Key-value key:** `LAST_CERTIFICATE_CHECK = 310`.
- **Rights area:** `Area.Certificates`, appended to the enum (stored by name in `group_rights`).

## 2. Requirements

From the request:

- **R1.** A sensor type that monitors the SSL certificate of an endpoint.
- **R2.** The number of days before expiry at which it turns bad is set per sensor.
- **R3.** Defined states: **OK**, **ABOUT_TO_EXPIRE**, **EXPIRED**.
- **R4.** A second tab on the sensor with the certificate's information.
- **R5.** A task checks each sensor at an interval set on the sensor, 24 hours by default.
- **R6.** A button refreshes the information at once.
- **R7.** All information about the certificate is downloaded, stored in the sensor and displayed.
- **R8.** The certificate itself is monitored, including whether it changes.
- **R9.** It can be configured whether a change of the certificate, of the issuer, or of either, is an error (*certificate changed unexpectedly*).

Added by revision 1 and kept:

- **R10.** An endpoint that doesn't answer has a state of its own (*unreachable*). It is not mistaken for an expired certificate, and one network blip doesn't fail a daily sensor for a day.
- **R11.** A certificate that isn't trusted, or doesn't match the name, fails the sensor (*invalid*), as the sensor's trust mode and checks say.
- **R12.** *Changed unexpectedly* stays until a person accepts the new certificate, or the accepted one comes back.

From the review:

- **R13.** Certificates without a public CA can be checked: those of a private CA, and self-signed ones, whose change is monitored.
- **R14.** A button accepts the change. It opens a warning window, and the person must tick an extra checkbox confirming the change before it is accepted.
- **R15.** Certificates are a rights area of their own, with *View*, *Edit* and *Manage*.
- **R16.** Whether an incomplete chain fails the sensor is set per sensor.
- **R17.** A change of the public key is a change rule of its own, besides the certificate and the issuer.
- **R18.** The interval is from 1 hour to 30 days.

## 3. Codes and states

| Code | Name | Success | When |
|---|---|---|---|
| 285 | `SSL_OK` | yes | Valid, more than the warning days left. |
| 286 | `SSL_CHANGED` | yes | A new certificate the sensor's rules don't alarm on, usually a renewal. Written once; the state stays 286 until the verdict changes. |
| 485 | `SSL_ABOUT_TO_EXPIRE` | no | The chain expires within the sensor's warning days. Only with *Check expiry*. |
| 486 | `SSL_EXPIRED` | no | The chain's expiry has passed. Only with *Check expiry*. |
| 487 | `SSL_CHANGED_UNEXPECTEDLY` | no | The certificate, its key or its issuer changed, and the sensor alarms on that. It stays until the change is accepted, or the accepted certificate returns. |
| 488 | `SSL_INVALID` | no | Not trusted under the sensor's trust mode; the name doesn't match (with *Check the name*); not valid yet (with *Check expiry*); or the chain is incomplete (with *An incomplete chain fails*). |
| 489 | `SSL_UNREACHABLE` | no | Two checks in a row failed: no address, refused, timeout, the handshake failed, or no certificate. |

**Precedence, worst first:** 489 → 486 → 488 → 487 → 485 → 286/285. The code is the worst finding. The message names the others too ("Expired on …; also changed unexpectedly: …").

**Messages use absolute dates in PNMT's time zone (`TZ`), never "in 12 days".** An event is written once, and its message must not go stale while it stays the sensor's last message. The UI computes the days left live (§8).

- 285: `Valid until 1 Feb 2027 13:00 · Let's Encrypt R11 · example.com`
- 286: `New certificate: valid until 2 May 2027 · Let's Encrypt R10 (was R11)`
- 485: `Expires on 29 Nov 2026 13:00, within the 14 days' warning · Let's Encrypt R11`. When an intermediate expires first: `The intermediate ACME Sub CA expires on 29 Nov 2026 13:00, before the certificate`
- 486: `Expired on 15 Nov 2026 13:00 · Let's Encrypt R11`
- 487: `Changed unexpectedly on 17 Nov 2026: new key, issuer Let's Encrypt → ZeroSSL. Accept the new certificate on the sensor's page if this was expected.`
- 488:
  - `Not trusted: the chain ends at a root PNMT doesn't know (self-signed?)`
  - `Not trusted by the private CA Contoso Root CA 2024`
  - `Name mismatch: the certificate is for *.example.org, not shop.example.com`
  - `Not valid before 20 Nov 2026`
  - `Incomplete chain: the server doesn't send its intermediate`
- 489: `Unreachable since 17 Nov 2026 10:02: connection refused (the last certificate seen is valid until 1 Feb 2027)`

**When an event is written:** when the *verdict key* changes. The key is the code with the certificates it is about:

| Verdict | Key |
|---|---|
| 285 or 286 | `ok\|current` (so the step from 286 to 285 writes nothing) |
| 485, 486, 488 | `code\|current` |
| 487 | `487\|current\|accepted` |
| 489 | `489` |

A change of the sensor's settings clears the key, so the next evaluation writes the state the new settings give (as `ZabbixSensorService.ApplyAsync` does).

## 4. The data model

### 4.1 `certificate_sensors` (`CertificateSensorEntity`, `[Audited]`), 1:1 with the sensor, cascade

**The endpoint.** `host:port` is the sensor's `Parameters`, as a ping target is (§4.3). The side table holds the rest:

- `ServerName` (string?, ≤ 253): the SNI and the name the certificate must match. Null: the host, unless the host is an IP address.
- `ConnectTo` (string?, ≤ 253): an address or name to connect to instead of the host. The server name stays. For one backend behind a load balancer, or split DNS.

**The trust** (audited):

- `Trust` (`Public` | `Private` | `Pinned`, stored as a string, default `Public`).
- The private CAs, in the private mode: the link table of §4.5.
- `CheckName` (bool): the certificate must name the server name.
- `CheckExpiry` (bool): 485 and 486, and *not valid yet*. Off for a device whose old self-signed certificate nobody will renew; its change is still watched.
- `IncompleteChainFails` (bool, R16): a chain the server sends without its intermediates is 488. It applies to the public and private modes.

**The change rules** (audited):

- `WarnDays` (int, 1 to 365): the default for new sensors is `Certificates__WarnDays`, 14 (§5.4).
- `AlertOnCertificateChange` (bool).
- `AlertOnKeyChange` (bool, R17).
- `AlertOnIssuerChange` (bool).
- `IssuerMatch` (`Organization` | `Exact`, stored as a string, default `Organization`).

The defaults follow the trust mode (§5.4).

**The state** (`[AuditIgnore]`):

- `NextCheckAt`, `LastCheckAt`, `LastSuccessAt`.
- `ConsecutiveFailures` (int), and `LastError` (string?, ≤ 500).
- `CurrentVersionId` (FK → `certificate_versions`, SET NULL).
- `Connection` (jsonb): the address that answered, the port, the SNI sent, the protocol, the cipher suite, and the handshake's milliseconds.
- `Validation` (jsonb): trusted (and by what: public CAs, which private CA, or nothing), whether the name matches, whether the chain is complete, the chain's expiry with the certificate it comes from, and the problems as text.
- `AnnouncedVersionId`: the version the last state event was about, which tells 286 from 285.
- `LastCode`, `LastVerdictKey`.

**The baseline** (audited, plain: fingerprints aren't secret):

- `AcceptedVersionId` (FK, SET NULL).
- `AcceptedAt`.
- `AcceptedBy`: *first check*, *automatically (renewal)*, or the person's name.
- `AcceptedNote` (string?, ≤ 500): what the person wrote when accepting (§8.4).

`Created`.

No `Pending*` columns: a change is *current ≠ accepted*, computed at each evaluation (§5.3). A change the rules don't alarm on moves the baseline to the current certificate there and then.

### 4.2 `certificate_versions` (`CertificateVersionEntity`, `[NotAudited("the certificates a check saw")]`)

One row per distinct leaf certificate a sensor has seen. Unique on (`SensorId`, `Sha256`); cascade with the sensor.

- **For comparing and listing:**
  - `Sha256` (hex, upper case, the Redmine/Zabbix pin format), and `SpkiSha256` (Base64, for the key rule).
  - `Subject`, `SubjectCommonName`, `Issuer`, `IssuerOrganization`.
  - `Serial`, `NotBefore`, `NotAfter`.
  - `Names` (`text[]`, the SANs, as `ZabbixHostEntity.Groups`).
  - `SelfSigned`.
- **`Facts` (jsonb):** everything parsed, as the API returns it (§4.4). Parsed once when first seen.
- **`ChainPem` (text, ≤ 64 KB):** the leaf and the intermediates as the server sent them. It is refreshed when the same leaf comes with another chain. A later version of PNMTD can parse it again.
- `FirstSeenAt`, `LastSeenAt`.
- **Retention:** the newest 50 versions per sensor (`Certificates__KeepVersions`). The current, accepted and announced versions are always kept. A renewal every 60 to 90 days makes 50 versions about ten years.

### 4.3 Why the address is `Parameters`

- **It is a ping target with a port.** The sensor page already shows `Parameters` as an inline field, and `sensor_create` already takes `parameters`, so the tools need no new argument (§9).
- **Changing a PING sensor's type to SSL certificate keeps its target:** `example.com` becomes `example.com:443`.
- **Accepted forms**, normalised on save:
  - `example.com` and `example.com:8443`.
  - `https://example.com/path` (the scheme's default port; the path is dropped).
  - `10.0.0.5:443`, `[2001:db8::1]:443`, and bare `2001:db8::1` (port 443).
  - International names are stored as typed, and sent as punycode (`IdnMapping`).
- **Stored as** `host:port` (`[v6]:port` for IPv6).

### 4.4 What is read and stored (R7)

The facts are parsed with .NET's typed extensions where they exist (.NET 7+), and `System.Formats.Asn1` where they don't.

- **The leaf:**
  - **Subject and issuer:** the full distinguished name and its parts (CN, O, OU, L, ST, C), from `EnumerateRelativeDistinguishedNames`. An `emailAddress` is kept but never given to the tools (§9).
  - **Names:** DNS names and IP addresses, from `X509SubjectAlternativeNameExtension`.
  - **Identity:** the serial number and the version.
  - **Validity:** `NotBefore`, `NotAfter`, and the lifetime in days.
  - **Key and signature:** the key's algorithm with its size or curve (RSA 2048, ECDSA P-256, Ed25519, or another by its OID), and the signature algorithm.
  - **Fingerprints:** SHA-256 and SHA-1 of the DER, and SHA-256 of the SPKI in Base64 (the `pin-sha256` form).
  - **Usage:** key usage, extended key usage, basic constraints.
  - **Key identifiers:** subject key identifier and authority key identifier.
  - **Where the CA publishes:** AIA (OCSP and CA-issuer URLs), and CRL distribution points.
  - **Policies:** with the validation level read from the CA/B Forum OIDs: DV `2.23.140.1.2.1`, OV `…2.2`, IV `…2.3`, EV `2.23.140.1.1`.
  - **Transparency and stapling:** the number of embedded SCTs, and OCSP must-staple (the TLS feature extension).
  - **Every other extension:** OID, name, whether critical, and its `Format(multiLine: true)`, so nothing the certificate says is lost.
- **The chain:** each certificate with its subject, issuer, validity, SHA-256 and key, and where it came from:
  - *sent by the server*;
  - *a public root from PNMT's trust store*;
  - *from a private CA* (named).

  Flags:
  - *incomplete:* the server doesn't send its intermediate;
  - *sends the root:* harmless;
  - *out of order*.
- **The connection:** the address and port that answered, the SNI sent, the TLS version, the cipher suite, and the handshake's duration.
- **The validation:** trusted or not under the sensor's mode with the chain's status in words, whether the name matches, whether the chain is complete, whether the certificate is valid yet, and the chain's expiry.
- **Limits against a hostile server:**
  - at most 10 certificates of a chain;
  - 500 names;
  - 50 extensions;
  - 1000 characters per name field, and 4000 per formatted extension;
  - 64 KB of PEM.

  More is cut, and the tab says so.

### 4.5 Private CAs (R13)

- **`certificate_authorities`** (`CertificateAuthorityEntity`, `[Audited]`):
  - `Id`, and `Name` (1 to 100 characters, unique).
  - **`Pem`** (≤ 64 KB): the root, and its intermediates if wanted.
  - `RootSha256`, `RootSubject`, `RootNotBefore`, `RootNotAfter`, and `IntermediateCount`.
  - `CreatedAt`, `CreatedBy`.
- **What a PEM may hold:**
  - exactly one self-signed certificate with `CA:TRUE`, which is the trust anchor;
  - and any number of CA certificates under it, which are the intermediates.

  A leaf certificate, a second root, or a certificate that doesn't chain to the root is refused with the reason. A pinned sensor needs no CA (§5.3).
- **`certificate_sensor_authorities`:** (`SensorId`, `AuthorityId`), the primary key both. The sensor's foreign key cascades. The authority's is RESTRICT: a CA in use can't be deleted, and the dialog lists its sensors.
- **Private CAs are not edited.** A new root is a new CA; the sensors are moved to it, and the old one is deleted once unused. That keeps the audit's story simple: which anchors a sensor trusted, and when.

## 5. The check and the evaluation

### 5.1 The probe (`PNMTD.Lib/Certificates/`)

`CertificateProbe.ReadAsync(TlsEndpoint, CertificateTrustInput, timeout, ct)` returns either the facts with the chain's DER and the validation, or a failure with its kind and message.

- `CertificateTrustInput` is the mode, and in the private mode the anchors and the intermediates, as `X509Certificate2Collection`.
- The probe lives in `PNMTD.Lib`, which the agents share (`PNMTD.Sensor.Core` references it), so a later agent-side check reuses it (§13).

1. **Connect:** `TcpClient.ConnectAsync(ConnectTo ?? Host, Port)`, which tries the name's addresses in turn. The address that answered is recorded.
2. **The handshake:** `SslStream.AuthenticateAsClientAsync` with:
   - `TargetHost` set to the server name;
   - the OS's protocol defaults (TLS 1.2 and 1.3);
   - a `RemoteCertificateValidationCallback` that keeps the certificate, the chain and the `SslPolicyErrors`, then **returns true**, so an untrusted or self-signed certificate is still read;
   - **`CertificateChainPolicy` with `DisableCertificateDownloads = true` and `RevocationMode = NoCheck`.**

   Without that policy, .NET follows the AIA and OCSP URLs **of the server's certificate**: a server could make PNMTD fetch any `http://` URL, internal ones included. PNMTD only reads what the server sends.
3. **Validate.** Build the chain once more, at the current time, with downloads off and the server's certificates in `ExtraStore`. Then, by mode:

   | Mode | The chain is built against |
   |---|---|
   | Public | The system's roots (`TrustMode = System`). |
   | Private | `TrustMode = CustomRootTrust`: `CustomTrustStore` holds the chosen CAs' roots, and `ExtraStore` also gets their intermediates. Public roots don't count. |
   | Pinned | No chain verdict: trust is the pin (§5.3). The chain is still built against the system's roots, so the tab can say *also trusted by public CAs* or *self-signed*. |

   - ***Complete*** means the chain builds from the server's certificates and the anchors alone. An intermediate that comes only from a private CA's PEM still counts as missing: other clients don't have it.
   - **The name check** is `RemoteCertificateNameMismatch` from the handshake.
   - **The chain's expiry** is the earliest `NotAfter` of the leaf, the intermediates the server sent and, in the private mode, the anchor.
4. **One timeout** for the connection and the handshake together: `Certificates__TimeoutSeconds`, 15.

**To verify in C2:** that the callback's `chain.ChainPolicy.ExtraStore` holds exactly the certificates the server sent, on Linux and macOS. If it doesn't, the chain as sent comes from a second pass with `X509Chain` over the received bytes.

Failure kinds and their texts:

- *no such name*;
- *refused*;
- *timeout*;
- *handshake failed*: with the TLS alert where there is one;
- *not TLS*: the port answered something else;
- *no certificate*.

### 5.2 The checker (`PNMTD/Certificates/`)

- **`CertificateChecker`** is a hosted service, registered with the monitoring tasks in `Program.cs`, so `BackgroundTasks:Enabled=false` turns it off for the integration tests.
  - **Every 15 seconds** it checks the due sensors. A sensor is due when it is enabled, its type is SSL certificate, and `NextCheckAt` is null or has passed.
  - Up to `Certificates__MaxConcurrent` (8) run at once, with `Parallel.ForEachAsync` as in `RouterPoller`.
  - **Every minute** it evaluates every enabled sensor (§5.3). That is a few small rows per sensor and no network, so *expired* flips at the chain's expiry whatever the interval.
  - **While the uplink is down** it checks nothing (`TaskRunDecisionMaker`), but it still evaluates: expiry doesn't need the network.
  - It writes `LAST_CERTIFICATE_CHECK`, so *System health → Background tasks* shows the task ("Certificate checks", every 60 seconds).
- **`CertificateCheckService.CheckAsync(sensorId, wait)`** is one check under a per-sensor `SemaphoreSlim`, as `RouterPollService.PollAsync`. The timer passes `wait: false` and skips a sensor that is busy. *Check now* waits up to 30 seconds.
  - **Success:**
    - Upsert the version by SHA-256 and set `LastSeenAt`.
    - Set `CurrentVersionId`, `Connection`, `Validation` and `LastSuccessAt`, and reset `ConsecutiveFailures` and `LastError`.
    - On the very first success, the current version becomes the accepted one, *first check* (trust on first use, as a router's host key).
  - **Failure:** increment `ConsecutiveFailures` and set `LastError`. The current version stays: the tab still shows the last certificate seen.
  - **The next check:**

    | After | `NextCheckAt` |
    |---|---|
    | a success, verdict OK (285/286) | now + `Interval` |
    | a success, failing verdict (485 to 488) | now + 60 minutes (`Certificates__FailingCheckMinutes`), never later than the interval would put it |
    | a failure | now + `Certificates__RetryMinutes` (15) |

    The 60-minute rule means a certificate renewed after *about to expire* turns OK within the hour, without *Check now*. The retry means a blip costs 15 minutes, not a day.
  - **Then** the sensor is evaluated at once.
- **Settings and the schedule:**
  - A new sensor has `NextCheckAt = null` and is checked within 15 seconds.
  - Changing the endpoint (`Parameters`, `ServerName`, `ConnectTo`) clears the baseline, the current and announced versions, the key and `NextCheckAt`: it is another endpoint, and the next check pins anew. The versions stay in the history.
  - Changing the trust mode or the private CAs clears `NextCheckAt`, since the validation is computed at the check. The baseline stays.
  - Changing the interval sets `NextCheckAt = LastCheckAt + Interval`.
  - Changing any other rule clears `LastVerdictKey` only.

### 5.3 The verdict (`CertificateVerdict.Evaluate`, pure)

`Evaluate(rules, state, current, accepted, announced, now)` returns the code, the message, the key, and whether the baseline moves. Nothing else decides the state, and §10 tests it without a network.

1. **No success yet:** 489 after `Certificates__FailuresBeforeUnreachable` (2) failures, otherwise nothing (*No data*).
2. **2 or more failures in a row:** 489, with the last certificate's expiry in the message.
3. **`CheckExpiry` and `now ≥` the chain's expiry:** 486, naming the certificate that expired.
4. **488**, when any of these holds:
   - the mode is public or private, and the chain isn't trusted;
   - `CheckName`, and the name doesn't match;
   - `CheckExpiry`, and `now < NotBefore`;
   - `IncompleteChainFails`, the mode is public or private, and the chain isn't complete.
5. **A change** is current ≠ accepted, in any of:
   - *the certificate:* the SHA-256 differs;
   - *the key:* the SPKI SHA-256 differs;
   - *the issuer:* the issuer key differs.

     | `IssuerMatch` | The issuer key |
     |---|---|
     | `Organization` | The issuer's `O=`, trimmed, case ignored; its full name when it has no `O=` |
     | `Exact` | The issuer's full name |

   - **It alarms** when a switched-on rule sees its kind of change: 487, naming every kind that changed (*new certificate*, *new key*, *issuer A → B*).
   - Otherwise **the baseline moves:** accepted = current, *automatically (renewal)*. Evaluation continues.
6. **`CheckExpiry` and the chain's expiry `− now ≤ WarnDays`:** 485.
7. **Otherwise OK:** 286 when `announced` is set and differs from `current`, else 285.

**How the rules include each other:** a new key or a new issuer always means a new certificate. *Fail on a new certificate* therefore includes the other two; the form shows them ticked and greyed. Key and issuer are independent of each other. *Key and issuer* is the useful middle: a renewal that keeps its key and its CA passes, anything else alarms.

**Pinned mode:** nothing vouches for the certificate but the pin.
- At least one of *Fail on a new certificate* and *Fail on a new key* must be on; the API refuses otherwise.
- The issuer rule is hidden: a self-signed certificate is its own issuer.
- Pinning the key suits a device that renews its self-signed certificate with the same key. Pinning the certificate suits everything else.

**Why `Organization` is the default:** Let's Encrypt picks one of several intermediates at each issuance (R10, R11, E5, E6, and its newer ones). Comparing the full name would report every other renewal as a new issuer. The organisation stays *Let's Encrypt*; a move to ZeroSSL, DigiCert or a proxy's own CA still changes it.

### 5.4 Defaults, and why

**The defaults by trust mode.** The form sets them when the mode is chosen; the API applies them to fields a request leaves out.

| | Public | Private | Pinned |
|---|---|---|---|
| Fail on a new certificate | off | off | **on** |
| Fail on a new key | off | off | off |
| Fail on a new issuer | **on** | **on** | – |
| Check the name | **on** | **on** | off |
| Check expiry | **on** | **on** | **on** |
| An incomplete chain fails | off | off | – |

- **Interval: 86 400 seconds (daily).** 0 in a request means daily. The range is **1 hour to 30 days** (R18). Expiry is evaluated every minute and failing sensors are checked hourly, so a long interval misses nothing time-bound.
- **Warning: 14 days, not 30.**
  - Certbot and most ACME clients renew at 30 days left, or at a third of the lifetime with ARI. A 30-day warning would report every healthy renewal for up to a day.
  - 14 leaves two weeks to fix a renewal that failed.
  - `Certificates__WarnDays` changes the default for new sensors.
- **Certificate and key changes don't alarm by default in the CA modes:** with automatic renewal, they would alarm every 60 to 90 days. **Issuer changes do:** they are rare and worth a look.
- **Pinned sensors don't check the name by default:** devices often carry `localhost`, a serial number or an old address. The pin is what counts. **They check expiry,** which *Check expiry* turns off for a device nobody will renew.
- **An incomplete chain is only a finding by default:** Chrome, Edge and Safari fetch the missing intermediate themselves, while curl, Java, Python and Firefox without a cached copy fail. Whoever serves such clients switches it on (R16).
- **Short-lived certificates:** Let's Encrypt offers six-day certificates, and CA/B Forum ballot SC-081 shortens public certificates to 200 days from March 2026, 100 from March 2027 and 47 from March 2029.
  - The *Certificate* tab warns when `WarnDays` is more than a third of the certificate's lifetime: renewals may look like problems.
  - It warns again when `WarnDays` is the lifetime or more: the sensor is always about to expire.

### 5.5 Options (`Certificates__*`, `CertificateOptions`)

| Option | Default | What it does |
|---|---|---|
| `Enabled` | true | Off: no checks or evaluations; the sensors keep their states. |
| `TimeoutSeconds` | 15 | The connection and the handshake together. |
| `RetryMinutes` | 15 | The next check after a failed one. |
| `FailuresBeforeUnreachable` | 2 | Failed checks in a row before 489. |
| `FailingCheckMinutes` | 60 | The check's rhythm while the verdict fails. |
| `MaxConcurrent` | 8 | Checks at once. |
| `WarnDays` | 14 | The default for new sensors. |
| `KeepVersions` | 50 | Versions kept per sensor. |

They go into deploy/README.md's settings table.

### 5.6 Known limits

- **Old devices:**
  - Debian's OpenSSL refuses TLS 1.0 and 1.1, and keys below its security level (RSA 1024, SHA-1 signatures), at the handshake. Such an endpoint is 489 *handshake failed*, and the message says it may be too old for PNMT's TLS.
  - The pinned mode doesn't help here: the handshake fails before there is a certificate to pin. A legacy mode needs its own OpenSSL configuration, so it is §13.
- **A load balancer with different certificates on its backends** reads as changes between checks. `ConnectTo` checks one backend. The tab's *Certificates seen* makes the alternation visible.
- **The public trust store is the image's.** A new public root reaches PNMT with an image update. An internal root doesn't need one: it is a private CA.
- **Outbound reach:** someone with *Certificates: Edit* and *Monitoring: Edit* can make PNMTD open a TCP connection to any `host:port` and send a ClientHello. That is the same reach a PING target or a router's address has. PNMTD sends nothing else, reads only TLS, and follows no URL (§5.1).

## 6. The sensor through `/sensors`, and the rights

### 6.1 `SensorService`

- **`ValidateAsync`, case `SSL_CERTIFICATE`:**
  - `Parameters` parses as an endpoint (§4.3): *"host, host:port or an https:// URL"*.
  - The interval is 0 or from 3 600 to 2 592 000.
  - `CertificateSensorService.Validate(request.Certificate, v)`:
    - `WarnDays` from 1 to 365;
    - `ServerName` a DNS name, and `ConnectTo` a name or an address;
    - `Trust` and `IssuerMatch` defined;
    - the private mode needs at least one existing private CA;
    - the pinned mode needs the certificate or the key rule.
- **The right:** every write to a certificate sensor needs ***Certificates: Edit***, besides the *Monitoring: Edit* the controller asks for.
  - The writes are: create; change, including a change of type to or from SSL certificate; and delete.
  - `SensorService` checks it from the caller on `HttpContext.User`, so the API and the tools both pass through it. It answers 403 `missing_right` like the other right checks.
  - The command line, which has no request, isn't asked.
  - Linking notification rules and the bulk importance stay *Monitoring: Edit* only.
- **`CreateAsync` and `UpdateAsync`** call `ApplyAsync` (the row, the defaults of §5.4, the CA links, and the resets of §5.2), and `RemoveAsync` when the type changes away, as for Zabbix. A request without `certificate` keeps the settings, as a Zabbix sensor's rules are kept.
- **`DeleteAsync`:** nothing new, the cascades remove the row, the links and the versions.
- **`GetAsync`:** `SensorDetail.Certificate = await certificates.SummaryAsync(id, ct)` for the type (§6.2).
- **`Interval`** is the check interval. `GracePeriod` is 0 and unused.
- **The write token exists, as for every sensor.** The page doesn't show the URL, as a router's doesn't. An event posted through it stands until the verdict changes, as with Zabbix.

### 6.2 Contracts (`Api/V1/Contracts/Certificates.cs`)

- **`CertificateSensorRequest`:**
  - `Trust = Public`, and `AuthorityIds`;
  - `CheckName?`, `CheckExpiry?`, `IncompleteChainFails?`;
  - `ServerName?`, `ConnectTo?`, `WarnDays?`;
  - `AlertOnCertificateChange?`, `AlertOnKeyChange?`, `AlertOnIssuerChange?`, `IssuerMatch?`.

  Null takes the trust mode's default (§5.4) on a new sensor, and keeps the value on an existing one. `SensorRequest.Certificate` holds it; other types ignore it.
- **`CertificateSensorSummary`** is `SensorDetail.Certificate`, for *Monitoring: View*: what the overview, the status strip and a save need.
  - **The settings:** the endpoint (`Host`, `Port`, `ServerName`, `ConnectTo`), the trust (`Trust`, `Authorities` as id and name, `CheckName`, `CheckExpiry`, `IncompleteChainFails`), and the change rules (`WarnDays`, `AlertOn…`, `IssuerMatch`).
  - **The schedule:** `LastCheckAt`, `NextCheckAt`, `Checking` (a check is running), `ConsecutiveFailures`, `LastError`.
  - **The certificate in brief:** `ExpiresAt` (the chain's expiry), `DaysLeft`, `IssuerOrganization`, `SubjectCommonName`, and `ChangePending` (the kinds of change, and since when).

  It holds no certificate details, so the sensor's page works for *Monitoring: View* without *Certificates*.
- **`CertificateSensorDetail`**, from `GET …/certificate` for *Certificates: View*:
  - **What was read:** `Current` (`CertificateItem`, with the full facts of §4.4), `Connection` and `Validation`.
  - **The baseline:** `Accepted` (`CertificateItem`, only when it differs from `Current`), with `AcceptedAt`, `AcceptedBy` and `AcceptedNote`.
  - **`Change`** (`CertificateChange?`): whether the certificate, the key and the issuer changed, whether that alarms, since when, and the fields that differ (subject, names added and removed, issuer, key, serial, validity, the SHA-256s).
  - **`VersionCount`**.
- **`CertificateItem`:**
  - **Identity:** `Id`, `Sha256`, `Sha1`, `SpkiSha256`, `Serial`.
  - **Who for, and from whom:** `Subject`/`Issuer` (`CertificateName`: `Dn`, `CommonName`, `Organization`, `OrganizationalUnit`, `Locality`, `State`, `Country`), and `Names`.
  - **Validity and key:** `NotBefore`, `NotAfter`, `LifetimeDays`, `Key` (algorithm, size or curve), `SignatureAlgorithm`, `SelfSigned`.
  - **What the certificate declares:** `ValidationLevel`, `KeyUsage`, `ExtendedKeyUsage`, `Urls` (OCSP, CA issuers, CRL), `Scts`, `MustStaple`, and `Extensions` (OID, name, critical, text).
  - **The chain:** `Chain` (`CertificateChainItem`: depth, subject, issuer, validity, SHA-256, key, `Source` (*server*, *public root*, *private CA* with its name), `TrustAnchor`).
  - **History:** `FirstSeenAt`, `LastSeenAt`.
- **`CertificateAcceptRequest`:**
  - `Sha256`: the version the person saw;
  - **`Confirmed`** (bool, must be true);
  - `Note` (≤ 500, optional).
- **`CertificateVersionItem`** is a row of the history: id, SHA-256, serial, issuer, `NotAfter`, first and last seen, and whether it is the current or the accepted one.
- **`CertificateAuthorityItem`:** `Id`, `Name`, `RootSubject`, `RootSha256`, `RootNotBefore`, `RootNotAfter`, `IntermediateCount`, `UsedBy` (the number of sensors), `CreatedAt`, `CreatedBy`. **`CertificateAuthorityRequest`:** `Name`, `Pem`.

### 6.3 Endpoints

**A sensor's certificate** (`SensorCertificateController`, `api/v1/sensors/{id:guid}/certificate`):

| Method and path | Right | Audit | What |
|---|---|---|---|
| `GET` | Certificates: View | – | `CertificateSensorDetail`. |
| `POST check` | Certificates: Edit | `CertificateCheckRequested` | Checks now, waits for it (≤ 30 s), and returns `CertificateSensorDetail`. |
| `POST accept` | Certificates: Edit | `CertificateAccepted` (warn) | `CertificateAcceptRequest`. It answers: 400 `confirmation_required` without `confirmed: true`; 409 `certificate_changed_again` when the SHA-256 is no longer the current one, so nothing newer is accepted unseen; 409 `no_change` without a change. Then it evaluates, and returns the detail. |
| `GET versions` | Certificates: View | – | The versions, newest first, paged. |
| `GET versions/{versionId}` | Certificates: View | – | A version's full `CertificateItem`. |
| `GET versions/{versionId}/pem?chain=true` | Certificates: View | – | `application/x-pem-file`, named `<host>-<date>-<sha256[:8]>.pem`: the leaf, or the chain as sent. Certificates are public; no audit. |

**Private CAs** (`CertificateAuthoritiesController`, `api/v1/certificate-authorities`):

| Method and path | Right | Audit | What |
|---|---|---|---|
| `GET` | Certificates: View | – | The list, for *Admin → Certificates* and the sensor form's picker. |
| `POST preview` | Certificates: Manage | not audited (changes nothing) | Parses a PEM and returns what §4.5 makes of it, or why not, before saving. |
| `POST` | Certificates: Manage | `CertificateAuthorityAdded` (warn) | Adds a CA. |
| `DELETE {id}` | Certificates: Manage | `CertificateAuthorityRemoved` (warn) | 409 `authority_in_use`, naming the sensors. |
| `GET {id}/pem` | Certificates: View | – | Its PEM. |

**The audit:**

- **The category:** `AuditCategory.Certificates`, with the target types `Sensor` and `CertificateAuthority` (new).
- **`CertificateAccepted`'s summary:** *`host / sensor`: certificate SHA256 …(new) accepted, was …(old); new key; issuer A → B; note: "…"*.
- **`CertificateAuthorityAdded`** names the CA, its root's subject and SHA-256.
- **`EndpointAuditTests`** covers all eleven endpoints.
- **`openapi.json` and `schema.d.ts`** are regenerated (`tools/npm.sh run gen:api`).

### 6.4 The rights area *Certificates* (R15)

**`Area.Certificates`**, label *Certificates*, in `RightsCatalog`:

| Level | Allows |
|---|---|
| **View** | The certificate sensors' *Certificate* tab: the certificate, its chain, the connection, the certificates seen and their PEM; the list of private CAs. |
| **Edit** | View, plus creating, changing and deleting certificate sensors (with *Monitoring: Edit*), *Check now*, and accepting a changed certificate. |
| **Manage** | Edit, plus *Admin → Certificates*: adding and removing private CAs. |

- **What stays *Monitoring*:** the sensor in the lists, the dashboard and the host page; its state, last message, events and uptime; its notifications, importance and *History*.
  - A *Monitoring: View* user without *Certificates* sees a certificate sensor as any other: *Valid until …*, *about to expire*, the status strip.
  - They don't see the *Certificate* tab, the comparison, or the buttons.
- **Why both rights for a write:** *Monitoring: Edit* is who may change sensors at all, and *Certificates: Edit* is who among them may change certificate sensors. It follows `zabbix_attach`, which `Requires` *Zabbix: View* besides *Monitoring: Edit*, and keeps the controller's attribute as it is.
- **Accepting is *Edit*, behind the confirmation of §8.4,** not *Manage*: whoever looks after the certificates decides about a renewal. The private CAs are *Manage*: they decide what a whole set of sensors trusts.
- **MCP:** `McpRights.Ceiling[Certificates] = Edit`, and `ReadOnlyCap[Certificates] = View`. Accepting is no tool, whatever the level (§9).
- **The web client:** `api/rights.ts` gets `Certificates: ['View', 'Edit', 'Manage']`. The group page, *Your access* and *Documentation → Access* read the catalog and need nothing else.
- **The groups:** the migration grants the area to nobody, as Cookie, Availability and MCP did (§12, decision 20).
  - Admins have every level.
  - The release notes say that Operators need *Certificates: Edit* to create certificate sensors.
  - Cookie's seeded group gets *Certificates: View* (`DevSeedCookie`).
- **`Roles` in the tests** keeps the update's groups as they are, so the new area is tested by groups of its own (§10).

### 6.5 The catalogs

- **`SystemService.StatusCodes()`:** the seven codes of §3, with their descriptions.
- **`SystemService.SensorTypes()`:** `SSL_CERTIFICATE`, *"PNMTD reads the TLS certificate of host:port every Interval seconds (daily by default) and evaluates it every minute, trusting public CAs, private CAs or a pinned certificate: 285 while valid, 286 for a new certificate it accepts, 485 within the sensor's warning days, 486 once expired, 487 for a change it alarms on until accepted, 488 when not trusted or the name doesn't match, 489 after two failed checks in a row."*
  - `Parameters`: *"host[:port], or an https:// URL"*.
  - `CanCreate` true, and `UsesInterval` true.
- **The task table:** *Certificate checks*, `LAST_CERTIFICATE_CHECK`, 60 seconds, shown while `Certificates:Enabled`.

## 7. Notifications

- **Nothing new in the rules.** An event per verdict change means:
  - *always* tells each change, not each day;
  - *on change* also tells a renewal (286);
  - *failures and back to OK* tells *about to expire* and then the renewal.
- **Message:** the event's message (§3), so a 487 reads as an instruction.
- **Cookie's case book** takes a failing certificate sensor into its host's case like any failing sensor. Nothing to code.

## 8. The web UI

### 8.1 Lookups, rights and helpers

- **`useLookups.ts`:** `typeLabel('SSL_CERTIFICATE')` is *SSL certificate*, and `typeIcon` is `pi pi-lock`. `usesGracePeriod` stays false.
- **`api/rights.ts`:** the area's levels (§6.4).
- **`resources.ts`:**
  - `sensorRequest` carries `certificate: certificateRequest(s.certificate)` for the type, from the summary, as for `zabbix`.
  - `api.certificates.get`, `check`, `accept`, `versions`, `version` and `pemUrl`.
  - `api.certificateAuthorities.list`, `preview`, `create`, `remove` and `pemUrl`.
- **`views/sensors/certificate/certificate.ts`:**
  - the address parsing of §4.3, for the form's hint;
  - the defaults by trust mode (§5.4);
  - `daysLeft(expiresAt, now)`;
  - the labels of the validation level and of key usage;
  - the interval labels.

  Tested in `certificate.test.ts`.

### 8.2 *Add sensor* (`SensorFormView.vue` + `certificate/CertificateFields.vue`)

The type is offered only to *Certificates: Edit*, as *Zabbix host* is only offered with Zabbix configured.

- **Address** (required): *"host, host:port or an https:// URL"*. The name is filled in from it (`shop.example.com:443`) until someone types one.
- **Trust:**
  - *Public CAs* (default);
  - *A private CA*: a multi-select of PNMT's private CAs, with *Add a CA…* for *Certificates: Manage*, which opens §8.5's dialog in place;
  - *Pinned (self-signed or unknown CA)*.

  Choosing a mode sets the defaults of §5.4. The hint under it says what the mode means.
- **Check every:** presets *hourly*, *every 6 hours*, *every 12 hours*, ***daily***, *weekly* and *monthly (30 days)*, or a custom number of hours or days, from 1 hour to 30 days. It uses the preset-and-custom pattern of `BackupScheduleField.vue`, and replaces the generic *Interval (seconds)* field for this type. `defaults()` sets 86 400.
- **When it fails:**
  - **Fail on a new certificate**, **Fail on a new key**, **Fail on a new issuer**, as switches with the inclusion of §5.3 shown. In the pinned mode the issuer switch is hidden, and one of the other two is required.
  - **Warn before expiry (days):** 1 to 365, preset from the installation's default, together with **Check expiry**.
  - **Check the name**.
  - **An incomplete chain fails** (CA modes only).
- **Advanced**, collapsed:
  - *Compare the issuer by:* organisation, or exact name;
  - *Server name (SNI)*;
  - *Connect to*.

### 8.3 The sensor's page (`SensorView.vue`)

**Tabs:** *Overview* · ***Certificate*** (for *Certificates: View*) · *History*.

- The *Certificate* tab carries a `!` badge while 487 or 488 is the state.
- *Events* stays part of *Overview*, as on every plain sensor (`plainTabs`).
- The overview keeps the plain page's key (`events`, no route parameter). The new tab is `certificate`, which the `tab` computed accepts for the type, as it does for Zabbix.

**Overview** is the plain sensor page, with:

- **A status strip** (`CertificateStatusStrip.vue`, as `MikrotikStatusStrip`), from the summary, for everyone:
  - the endpoint, the trust mode (*public CAs*, *Contoso Root CA*, *pinned*);
  - *valid until 1 Feb 2027 · 76 days left* (live, `useNow`), the issuer's organisation;
  - *checked 3 h ago · next in 21 h*, or *checking…*;
  - and **Check now**, for *Certificates: Edit*.
- **The configuration panel's rows for the type,** each an `InlineField` saving through `sensorRequest(…, { certificate })`:
  - address, trust and CAs, server name, connect to;
  - check every;
  - the three change rules and the issuer comparison;
  - the warning days, *Check expiry*, *Check the name*, *An incomplete chain fails*.

  The rows are editable with *Certificates: Edit* (and *Monitoring: Edit*), and read-only otherwise. The generic *Interval* and *Grace period* rows are hidden for the type.
- **The *Integration* panel is hidden,** as for routers.

**A change waiting** (`ChangePending`) shows on both tabs, for everyone, as a warning `Message`:

- the text: *"The certificate changed unexpectedly on 17 Nov 2026: new key, issuer Let's Encrypt → ZeroSSL."*;
- **Review and accept…**, for *Certificates: Edit*, which opens §8.4.

**Certificate** (`certificate/CertificateTab.vue`) loads `GET …/certificate`:

- **The headline:**
  - *Valid · 76 days left*, or *Expires in 5 days*, or *Expired 2 days ago*;
  - a validity bar from `NotBefore` to the chain's expiry, with *today* and the start of the warning marked;
  - the trust: *Trusted by public CAs*, *Trusted by the private CA Contoso Root CA 2024*, or *Pinned since 3 Mar 2026 by anna*;
  - the warnings of §5.4 about the warning's length.
- **Subject:** the common name, the organisation and its parts, and **the names** as chips (with *+120 more* past 30), each findable.
- **Issuer:** its name and organisation, with the validation level (DV, OV, EV), or *self-signed*.
- **Validity:** from, until, lifetime, days left, and the date the warning starts. When an intermediate expires first, it is named here.
- **Key and signature:** the algorithm and its size or curve, and the signature algorithm.
- **Fingerprints:** SHA-256, SHA-1, and SPKI SHA-256, each with a copy button (`CopyField`).
- **Extensions:** key usage, extended key usage, basic constraints, the key identifiers, the OCSP and CA-issuer URLs, the CRL points, the policies, the SCTs, must-staple. Under ***All extensions***: every extension as stored.
- **Chain:** from the leaf to the root, each row expandable to its own facts.
  - A row is marked *sent by the server*, *public root* or *private CA: …*.
  - *Incomplete*, *sends the root* and *out of order* show as hints.
  - The tab suggests a fix, as a link:
    - **When the server sends a root PNMT doesn't know:** *Trust this root as a private CA…*, for *Certificates: Manage*. It opens §8.5's dialog with that root's PEM.
    - **For a self-signed leaf in a CA mode:** *Switch to pinned…*.
- **Connection:** the address that answered, the SNI sent, the TLS version, the cipher suite, the handshake's milliseconds, and when.
- **Validation:** trusted, the name matches, and the chain is complete, each a tick or a cross with the problem in words, and whether the sensor checks it.
- **Download:** *Certificate (PEM)* and *Chain (PEM)*, and *Copy PEM*.
- **Certificates seen:** the versions, newest first (`ListTable`), with first and last seen, serial, issuer, valid until, and *current*, *accepted* and *changed* tags. A row opens that version's facts in a drawer, with *Compare with current*.
- **When the last check failed:** the last certificate seen stays shown, under a `Message` with the error and *since …*.

### 8.4 Accepting a change: the warning window (R14)

**`certificate/AcceptChangeDialog.vue`**, a modal opened by *Review and accept…*. It is never a one-click `ask()`.

- **Title:** *Accept the new certificate?*, with the warning icon and the warn severity.
- **The warning**, first and in full:

  > Only accept this if you know why the certificate changed: a renewal, a new server, a move to another CA. An unexpected change can mean that someone is intercepting the connection to *shop.example.com*. After accepting, PNMT compares every check against the new certificate, and this sensor turns OK.
- **What changed:** a side-by-side table, *accepted* against *new*, of the SHA-256, the key (algorithm and SPKI), the issuer, the subject, the names (added and removed), the serial and the validity. Changed rows are highlighted, and the kinds that alarm are tagged (*new key*, *new issuer*).
- **How to check it on the server:** the new SHA-256 with a copy button, and the command that shows it, filled in for this endpoint:

  ```sh
  openssl s_client -connect shop.example.com:443 -servername shop.example.com </dev/null 2>/dev/null \
    | openssl x509 -noout -fingerprint -sha256
  ```
- **The confirmation, required:** a checkbox, *"I have checked this change and confirm that the new certificate is expected."* It is unticked whenever the dialog opens.
- **A note for the audit log,** optional (≤ 500 characters): *why, or who confirmed it*.
- **The buttons:** *Cancel* (the default focus), and **Accept the new certificate** (warn severity), disabled until the box is ticked.
- **What is sent:** `{ sha256: <the new certificate's, as shown>, confirmed: true, note }`. The server refuses without `confirmed` (§6.3), so the API and any other client need the same explicit step.
- **If the certificate changed again while the dialog was open** (409 `certificate_changed_again`), the dialog reloads with the newer comparison and an unticked box, and says why.
- **Afterwards:** a toast *New certificate accepted*; the banner disappears; *Accepted by anna, 17 Nov 2026 14:02* shows under the headline with the note; the *History* tab shows the audit entry.

### 8.5 *Admin → Certificates* (`views/admin/CertificatesView.vue`)

- **In the drawer** for *Certificates: Manage* (`navigation.ts`, as *SSH keys*). The page reads with *View*; its buttons need *Manage*.
- **Private CAs:** a list with the name, the root's subject, SHA-256 (short, copyable), *valid until* with days left (in warn colours from 90 days), the number of intermediates, *used by n sensors* (a link to the sensor list filtered by type), and who added it and when.
- **Add a private CA…:**
  - a name, and the PEM, pasted or from a file (`.pem`, `.crt`, `.cer`);
  - `POST preview` shows the root and the intermediates as §4.5 reads them, with their fingerprints, or why the PEM is refused;
  - a note: *Every sensor that trusts this CA accepts any certificate it signs.*
  - *Add* saves.
- **Remove…:** only for an unused CA; otherwise the dialog lists the sensors that use it.
- **Download** a CA's PEM.

### 8.6 Elsewhere

- **The host page and the sensor lists:** nothing to change. The type's icon, label and last message show as for any type.
- **The *Documentation* page** lists the codes and the type from the catalogs. *Documentation → Access* lists the new area from the catalog. **IntegrationsDocs** gets nothing: this needs no integration.

## 9. Tools, seed, fakes, docs

- **`CommandTools`:**
  - `certificate_get` (*Certificates: View*, read) returns the endpoint, the trust, the rules, the state, the current certificate's main facts, the change, and the last error.
  - `certificate_check_now` (*Certificates: Edit*, action, `AuditEvents = [CertificateCheckRequested]`, idempotent) checks and returns the same.
  - Subject, issuer, names and extension texts come from the server, so they go through `ctx.UntrustedOrNull` as sensor messages do. `emailAddress` is dropped.
  - **Accepting is not a tool, and neither are the private CAs:** both are a person's decision, as a router's host key is. `certificate_get` says that accepting is done on the sensor's page.
- **`MonitoringWriteTools.CreatableTypes`** gets `SSL_CERTIFICATE`:
  - `parameters` is the address, `intervalSeconds` 0 means daily, and the other settings take the public mode's defaults.
  - The private and pinned modes are set up in PNMT.
  - `sensor_create` `Requires` *Certificates: Edit* for the type: `SensorService` refuses otherwise (§6.1), and the tool's description says so.
- **`DevSeedIntegrations`** (`SEED=1`):
  - a private CA *PNMT demo root* with an intermediate, all generated by the seed;
  - six certificate sensors on `*.pnmt.test` names, with versions generated by the seed:
    - public OK;
    - about to expire;
    - expired;
    - changed unexpectedly, with an older accepted version;
    - a private-CA sensor;
    - a pinned self-signed one.

  The tabs have content without a network. `NextCheckAt` is 30 days out, so the demo keeps its states; *Check now* makes them unreachable, which is honest.
- **`pnmtd dev fake-integrations --tls-port 7391`:** one TLS port that picks its certificate by SNI (`ServerCertificateSelectionCallback`):
  - `valid.pnmt.test` and `soon.pnmt.test` (5 days left), signed by a fake public CA;
  - `expired.pnmt.test`;
  - `noint.pnmt.test` (without its intermediate);
  - `private.pnmt.test`, signed by a fake private CA whose root `GET /tls/private-ca.pem` serves;
  - `selfsigned.pnmt.test`.

  `POST /tls/rotate?name=…` on the HTTP port gives a name a new certificate: `&key=keep` keeps its key, and `&issuer=other` makes another CA sign it. `tools/e2e.sh` starts it with the port.
- **Docs:**
  - README's features get a bullet;
  - deploy/README.md gets the `Certificates__*` settings;
  - `docs/release-notes-1.13.md`, including the new rights area and who must grant it;
  - this plan's *As built* once built.

## 10. Tests

- **`CertificateVerdictTests`** (pure, `PNMTD.Tests/Certificates/`):
  - **Precedence:** each code and each pair of findings, with the worst first and the other in the message.
  - **Expiry:**
    - the warning at exactly `WarnDays`;
    - expired at exactly the chain's expiry, by a fake `Clock.Provider`;
    - an intermediate that expires before the leaf gives 485, then 486, naming it;
    - `CheckExpiry` off gives neither, nor *not valid yet*.
  - **Failures:** one is no event, two are 489; and a success after 489 returns to the certificate's verdict.
  - **The trust and the checks:**
    - untrusted gives 488 in the public and private modes, never in the pinned one;
    - a name mismatch is 488 only with `CheckName`;
    - an incomplete chain is 488 only with `IncompleteChainFails`.
  - **Changes:**
    - every combination of the three switches with a new certificate, a new key and a new issuer;
    - a renewal keeping its key passes *key + issuer*, and a new key fails it;
    - R10 → R11 passes with `Organization` and alarms with `Exact`;
    - an issuer without `O=` compares by its full name;
    - the accepted certificate returning ends 487;
    - a change that doesn't alarm moves the baseline and gives 286 once, then nothing.
  - **The verdict key:** the step from 286 to 285 is no event; a settings change writes again.
- **`CertificateProbeTests`:** a loopback TLS server (`TcpListener` + `SslStream`) with certificates made by `CertificateRequest` (a test CA, an intermediate, leaves), loaded through a PKCS#12 round trip (`X509CertificateLoader.LoadPkcs12`) so the server works on macOS as on Linux. Cases:
  - **The certificates:** valid; expired; not yet valid; wrong name; self-signed; without the intermediate (*incomplete*); with the root (*sends the root*).
  - **The trust modes:**
    - the test CA is untrusted in the public mode, and trusted in the private mode with it as the anchor;
    - another private CA as the anchor leaves it untrusted;
    - an intermediate given only in the CA's PEM makes it trusted but *incomplete*;
    - the pinned mode reports *self-signed* and no verdict.
  - **The protocol and the name:** TLS 1.2 and 1.3; an IP endpoint with an IP SAN; IPv6 loopback; an IDN name.
  - **Failures:** a refused port; a port that accepts but sends HTTP (*not TLS*); one that accepts and stays silent (*timeout*).
  - **Facts:** the SANs, the key, the fingerprints (checked against `openssl x509 -fingerprint` values computed in the test), the SCT count of a fixture certificate, and every extension present.
  - **No downloads:** a leaf whose AIA points at a local HTTP listener; the listener must see no request.
- **`CertificateAuthorityTests`:** the PEM rules of §4.5 (one root, intermediates under it, a leaf refused, a foreign intermediate refused); the unique name; deletion refused while in use and allowed after; the audit entries.
- **`CertificateSensorTests`** (with `TestApp` and the loopback server):
  - **Create:** valid and invalid addresses, ports, warn days, intervals (3 599 and 2 592 001 refused), and the defaults per trust mode. The private mode needs a CA, and the pinned one a certificate or key rule.
  - **The detail:** the first check pins.
  - **Changes:** the server's certificate swapped gives 286, or 487 with the switch; a renewal with the same key passes the key rule.
  - **Accept:**
    - without `confirmed` → 400 `confirmation_required`, with nothing changed;
    - with it → 285, the audit entry with the note, and `AcceptedBy`/`AcceptedNote` set;
    - a stale SHA-256 → 409.
  - **The schedule:**
    - an endpoint change resets the baseline;
    - a trust or CA change triggers a check;
    - an interval change moves `NextCheckAt`.
  - **Cleanup:** the type changed away removes the row and the links; deleting the sensor removes the versions; retention keeps the referenced versions.
  - **Rights**, with groups made in the test:
    - *Monitoring: View* alone reads the sensor and its summary, but gets 403 on `GET …/certificate`;
    - *Certificates: View* reads the detail and the PEM;
    - *Monitoring: Edit* without *Certificates: Edit* gets 403 `missing_right` on creating, changing or deleting a certificate sensor, and still links rules to it;
    - *Certificates: Edit* checks and accepts;
    - only *Manage* adds a CA.
  - **Notifications:** an ONLY_FAILURES_ON_CHANGE rule gets one 485, not one a day (the evaluation run 48 times over a fake day).
- **`EndpointAuditTests`:** the eleven endpoints' rights and audit.
- **`McpRights` tests:** the ceiling and the read-only cap of the new area; `sensor_create` of the type refused without *Certificates: Edit*.
- **The catalogs:**
  - `MonitoringApiTests.ReferenceDataForTheDocs` gets an assertion that the seven codes are listed, with 285 and 286 as successes;
  - the rights catalog's tests get the area.
- **Web UI:**
  - `certificate.test.ts`: the address forms, the defaults per mode, the days left, the interval labels;
  - `rights` and `navigation.test.ts`: the area and the drawer entry;
  - `AcceptChangeDialog`: the button is disabled until the box is ticked, the box is unticked again after a 409, and the request carries `confirmed`;
  - `CertificateTab` with a mounted detail for each state (`src/test/mount.ts`).
- **E2E (`e2e/certificate.spec.ts`, against the fakes):**
  - **Public:** create a sensor for `valid.pnmt.test` and see *Valid* and the tab. Rotate it with another issuer, press *Check now*, and see 487 and the banner. Open the dialog: *Accept* is disabled, then enabled once the box is ticked; accept, and see 285 and the history.
  - **Private:** add `private-ca.pem` on *Admin → Certificates*, create a private-mode sensor for `private.pnmt.test`, and see it trusted.
  - **Pinned:** a pinned sensor for `selfsigned.pnmt.test` is OK, and rotating it gives 487.
  - **Findings:** `soon` shows 485, and `noint` the incomplete chain.
- **`pages.spec.ts`:** screenshots of the *Certificate* tab, the accept dialog, and *Admin → Certificates*.

## 11. Security and privacy

- **Nothing secret is stored:** certificates and CA roots are public. The PEM downloads are not audited.
- **PNMTD fetches no URL a server names:** no AIA, no OCSP, no CRL (§5.1). Its only outbound traffic is the TCP connection and the handshake to the sensor's own endpoint.
- **Accepting a change is deliberate and traced:**
  - the warning window and its checkbox (§8.4);
  - the server-side `confirmed` (§6.3);
  - the audit entry with both fingerprints and the note.

  An accepted interception can be traced to a person and their reason.
- **A private CA is a trust decision for every sensor that uses it.** It is *Manage*, audited, and never edited in place (§4.5).
- **Trust on first use:**
  - A sensor created during an interception pins the interceptor's certificate.
  - In the CA modes, an interceptor needs a certificate PNMT trusts as well.
  - In the pinned mode, the first certificate is all there is. The tab says *pinned at the first check*, and the form's hint suggests comparing the fingerprint once with the device's own display.
- **The tools mark the certificate's texts as untrusted:** anyone can put "ignore previous instructions" in an `O=`.

## 12. Decisions

1. **The type is `SSL_CERTIFICATE`, labelled *SSL certificate*,** as the request names it, though the protocol is TLS. The codes are `SSL_*`. The code's classes are `Certificate*`.
2. **One sensor per endpoint.** Several ports or names are several sensors, as several pings are.
3. **The address is `Parameters`, the rest a side table** (§4.3).
4. **ABOUT_TO_EXPIRE fails (485), as asked.** PNMT has no warning state. A third state would touch `MonitoringState`, the dashboard, uptime and every rule type, which is more than this sensor.
5. **The warning defaults to 14 days, not the common 30,** so healthy renewals don't alarm (§5.4).
6. **Expiry is evaluated every minute from the stored certificate,** not only at a check. *Expired* is on time with any interval.
7. **Unreachable after two failed checks in a row,** with a retry after 15 minutes. While the verdict fails, the check runs hourly.
8. **Unreachable wins over expired:** without a connection PNMT doesn't know whether the certificate was renewed. The message names the last certificate's expiry.
9. **One accepted certificate, pinned at the first check;** a change alarms until it is accepted or the accepted one returns. A set of accepted certificates (for load balancers) is left out: the alternation is worth seeing, and `ConnectTo` checks one backend.
10. **The issuer is compared by organisation by default** (§5.3); the exact name is an option.
11. **An expected change is a success event of its own (286),** so a renewal is in the events and can be notified. Then the sensor stays silent.
12. **AIA downloads are off,** so a missing intermediate is reported, never repaired by PNMT (§5.1).
13. **The expiry that counts is the chain's:** an intermediate or a private root that expires before the leaf is reported first.

Answered in the review's first round:

14. **An incomplete chain:** *defined per sensor*. `IncompleteChainFails`, off by default (§5.4).
15. **The public key as a change rule:** *yes*. `AlertOnKeyChange`, compared by SPKI SHA-256 (§5.3).
16. **The rights:** *a set of their own*. The area *Certificates* with *View*, *Edit* and *Manage* (§6.4). *Check now* moved from *Commands: Run* to *Certificates: Edit*.
17. **The interval:** *1 hour to 30 days*. *Monthly* is a preset (§5.4, §8.2).
18. **Private CAs and self-signed certificates:** the trust modes *private* and *pinned*, with *Admin → Certificates* for the CAs (§4.5, §5.1, §8.5). Trust anchors moved from *Later* into the plan. *Check expiry* can be turned off, for devices whose self-signed certificate nobody renews.
19. **Accepting:** a warning window with the comparison, a required checkbox, an optional note, and the confirmation enforced by the server (§8.4).

Answered in the review's second round:

20. **The groups after the update:** *nobody is granted the area*. The migration grants *Certificates* to no group, as with Cookie, Availability and MCP. Admins have it, and the release notes ask them to grant it: *Edit* to whoever creates certificate sensors, *View* to whoever reads the *Certificate* tab (§6.4, §9).
21. **A write needs *Monitoring: Edit* and *Certificates: Edit*.** The controller's attribute stays *Monitoring: Edit*, and `SensorService` adds *Certificates: Edit* for the type, from the caller, for the API and the tools alike (§6.1, §6.4).
22. **Accepting is *Certificates: Edit*,** behind the warning window, the checkbox and the server's `confirmed` (§8.4, §6.3). *Manage* is for the private CAs only.
23. **A private CA's own expiry is shown, not a sensor:** on *Admin → Certificates* (in warn colours from 90 days), and in the chain's expiry of each sensor that trusts it (§5.3, §8.5). A system sensor for it is §13.

## 13. Later

- **STARTTLS** for SMTP (25, 587), IMAP (143), POP3 (110) and LDAP (389). A `Protocol` column with one value now would make it a code change only.
- **Checks from a server's agent:** a *TLS certificate* check in diagnostics sets (docs/windows-sensor-plan.md §5), with the probe from `PNMTD.Lib`, for endpoints only the inside of a network reaches.
- **An overview of all certificates** by days left, with the issuer and the names: a filter on the sensors list, or a page of its own. Also the global search by address and SAN.
- **A mode trusting public and private CAs together,** if an endpoint ever needs it.
- **A legacy mode for old devices** (TLS 1.0/1.1, weak keys), with its own OpenSSL configuration.
- **Revocation,** through CRLs fetched from a list PNMT keeps rather than from URLs a server names.
- **A system sensor for the private CAs** (docs/ai-operations-plan.md §4): failing when one of them expires within 90 days, so that a root's end is notified like any other condition and not only seen on *Admin → Certificates* (decision 23).

## 14. Build order

1. **C1. Model and rights:**
   - `SensorType.SSL_CERTIFICATE`, the codes, and `LAST_CERTIFICATE_CHECK`;
   - `Area.Certificates` in `RightsCatalog`, `McpRights` and `api/rights.ts`;
   - the entities and the migration `CertificateSensors` (sensors, versions, CAs, links); `dotnet ef migrations has-pending-model-changes` clean;
   - `CertificateOptions`;
   - the catalogs in `SystemService`.
2. **C2. The probe** in `PNMTD.Lib/Certificates/`: `TlsEndpoint`, `CertificateTrustInput`, `CertificateProbe`, `CertificateFacts`, with `CertificateProbeTests` for all three modes. The check of §5.1 (what `ExtraStore` holds on Linux and macOS) is settled here, before C3 relies on *the chain as sent*.
3. **C3. The verdict and the checker:** `CertificateVerdict`, `CertificateCheckService`, `CertificateChecker`, retention, with `CertificateVerdictTests` and the schedule's tests.
4. **C4. Service and API:**
   - the `SensorService` hooks and the right check;
   - `CertificateSensorService`, and `CertificateAuthorityService` with the PEM rules;
   - the two controllers and the audit events and category;
   - `openapi.json` and `schema.d.ts`;
   - `CertificateSensorTests`, `CertificateAuthorityTests`, `EndpointAuditTests` and the MCP rights tests.
5. **C5. Web UI:**
   - the lookups and the rights;
   - `CertificateFields` with the trust modes;
   - the status strip and the rows;
   - `CertificateTab`, `AcceptChangeDialog` and *Admin → Certificates*;

   with unit tests.
6. **C6. Tools, seed, fakes, e2e, docs:**
   - `certificate_get`, `certificate_check_now`, `CreatableTypes`;
   - the seed with its CA;
   - `--tls-port` with the private CA and the rotations;
   - `certificate.spec.ts` and the screenshots;
   - README, deploy/README.md, and release notes 1.13.0 (a migration, so a minor version, per the release recipe).

## 15. As built (2026-11-17)

Built as planned, in the build order of §14, with these particulars.

**The probe and §5.1's check (C2).**
- **`ExtraStore` holds exactly what the server sent,** on Linux and on macOS, the leaf left out: *the chain as sent* is that. `CertificateProbeTests.TheChainAsSentIsWhatTheServerSends` checks it.
- **No URL a certificate names is fetched on Linux,** where PNMT runs. `NoUrlOfTheCertificateIsFetched` passes there; it was run in the .NET 10 SDK container, as is everything else.
- **On macOS the system's TLS stack fetches a missing intermediate during the handshake.** It does so even against a non-.NET server, because Apple's trust evaluation completes the peer's chain before any policy of PNMTD's applies. The test is therefore *inconclusive* on macOS, saying why. PNMTD's own chain builds never fetch, on either system.
- **"Not TLS" comes from the first bytes the server sent:** a TLS record starts with 0x16 or 0x15, so plain HTTP, for one, is named as such.
- ***Complete* means:**
  - every certificate between the leaf and the root came from the server;
  - a chain that stops at a CA PNMT doesn't know is complete, because only its root is missing;
  - a chain that is the leaf alone is not.

  The message for such a chain names the CA it stops at: *"the chain ends at X, a CA PNMT doesn't trust (a private CA?)"*, or, in the private mode, *"signed by X, not by the sensor's private CA"*.
- **The *out of order* flag of §4.4 was left out.** A .NET server sends its chain in its own order, so no test could produce an out-of-order chain. *Sends the root* is detected but untested, for the same reason.

**The model.**
- **Two state columns more than §4.1:**
  - `FirstFailureAt`, for *unreachable since*;
  - `certificate_versions.IssuerCommonName`, so that the minute's evaluation doesn't parse the stored facts.
- **The migration is `CertificateSensors`.**
- **`AlertOnKeyChange` is `[AuditPlain]`:** its name looks like a secret's to the audit's guard.

**The verdict.**
- **The other findings follow the worst one**, each as *"; also …"*, then the issuer: `Not trusted: …; also changed unexpectedly on 18 Oct 2026: …; also expires on … · ZeroSSL ECC Domain Secure Site CA`.
- **`AcceptedBy` is the account's user name.**
- **The accepted certificate returning after a change** gives 286, an OK event of its own.

**The API.**
- **`CertificateSensorDetail` carries the summary.** The summary also has `FirstFailureAt`, `SelfSigned`, `AcceptedAt` and `AcceptedBy`, for the status strip.
- **A certificate sensor's write without *Certificates: Edit*** is 403 `missing_right` with the area and level, from `ApiException.MissingRight`, as the authorization handler writes it.
- ***Check now* while checks are off** is 409 `certificates_off`. While another check of the sensor runs past 30 seconds, it is 409 `check_busy`.
- **`CertificateAccepted` and `CertificateAuthorityAdded`** have the warn tone. The audit category is *Certificates*.

**The tools.** `sensor_create` checks *Certificates: Edit* itself for the type: Cookie calls without a request, so `SensorService` can't ask its caller.

**The seed.**
- **Seven sensors on seven hosts, each with its demo certificates:**
  - `shop.pnmt.test:443`, `wiki.pnmt.test:443` (the private CA *PNMT demo root*) and `printer.pnmt.test:443` (pinned), all OK;
  - `mail.pnmt.test:993`, about to expire;
  - `legacy.pnmt.test:443`, expired;
  - `vpn.pnmt.test:443`, with a change waiting: a new key and an issuer *Let's Encrypt* → *ZeroSSL*;
  - `backup.pnmt.test:443`, unreachable.
- **The facts, chains and validations are `CertificateProbe.Evaluate`'s** over certificates the seed makes. For the *public* ones, a demo stands in for a public root.
- **The four failing ones are ignored**, with a note saying why, so that the dashboard's acceptance targets (six problems in three hosts) hold.

**The web UI** (`views/sensors/certificate/`, `views/admin/CertificatesView.vue`).
- **`InlineField` has a `custom` type:** the parent edits its own draft in an `#editor` slot. *Trust and private CAs* and *Check every* need it.
- **Changing the trust mode inline** sends the rules as null, so the server applies the new mode's defaults, as the form does.
- **Changing a sensor's type to SSL certificate** sends the interval as 0 (daily): a ping's 60 seconds would be refused.
- **`humanize()` keeps "SSL" upper case.**
- **The status strip shows on every tab,** as a router's does, so *Check now* is at hand on the *Certificate* tab too.
- ***Admin → Certificates*** is at `/admin/certificates`, in the drawer's *Devices* group next to *SSH keys*. The route needs *View*, the drawer entry *Manage*.
- ***Used by n sensors*** is plain text: there is no sensor list filtered by type to link to.
- **The warning days in *Add sensor* start empty,** with the server's default applying. `Certificates__WarnDays` isn't exposed to the browser.
- ***Trust this root as a private CA…*** takes the root from the chain's PEM (`versions/{id}/pem?chain=true`), picked by its SHA-256.
- **The CA list has the number of sensors only;** their names are in the 409 of a removal.

**The fakes.** `pnmtd dev fake-integrations --tls-port 7391` serves the names of §9 on one port, on IPv6 and IPv4. Every name but `selfsigned.pnmt.test` is signed by one fake private CA. `tools/e2e.sh` hands the browser tests `E2E_FAKES_URL`, `E2E_TLS_HOST` and `E2E_TLS_PORT`.

**The tests.**
- **The certificate tests' host is disabled** and cleaned up after the class: the dashboard's recent changes, which another class counts on, show only the newest fifteen of the whole run.
- **The counts that changed:** the rights catalog has 20 areas, the task list 17 tasks, and the seed 315 sensors.
- **The browser tests' host is disabled** for the same reason: `dashboard.spec.ts` counts the seed's six problems, and the certificate spec leaves an expiring sensor and an incomplete chain failing. The spec clicks the form's pickers centred in the window: choosing an option scrolls the long form, and the sticky app bar would take the click. For PrimeVue's MultiSelect, whose combobox is a visually hidden input, it clicks the box.

