# PNMT 1.13: SSL certificate sensors

## What's new

- **A sensor of the type *SSL certificate* watches the certificate of any `host:port`** (docs/ssl-certificate-plan.md). The address is a host, `host:port` or an `https://` URL. The check runs every day by default, from hourly to every 30 days, and *Check now* runs it at once. It fails **a set number of days before expiry** (14 by default) and **once expired**. Expiry is evaluated every minute from the stored certificate, so *expired* comes at the moment of expiry, not at the next check.
- **Everything the certificate says, on a tab of its own:**
  - the subject and every name;
  - the issuer and the validation level (DV, OV, EV);
  - the validity, the key and the signature;
  - the SHA-256, SHA-1 and public-key fingerprints;
  - every extension: usages, the CA's URLs, policies, the SCTs, must-staple;
  - the chain, with where each certificate came from;
  - the connection's TLS version and cipher;
  - the validation;
  - every certificate the sensor has seen, and the PEM to download.
- **A certificate that changes when it shouldn't fails the sensor** as *changed unexpectedly* (487). Each sensor says what counts: a new certificate, a new key, a new issuer, or any of them. The issuer is compared by its organisation by default, since Let's Encrypt's intermediates rotate. A change that no rule alarms on, a renewal, is an event of its own (286), and the sensor moves on.
- **Accepting a change is deliberate.** *Review and accept…* opens a warning window:
  - accepted and new side by side, with the changed fields highlighted;
  - the `openssl` command that shows the server's fingerprint;
  - a checkbox the person must tick;
  - a note for the audit log.

  The server refuses an accept without the confirmation, and one for a certificate that changed again meanwhile. The audit log records both fingerprints and the note.
- **Public CAs, private CAs and self-signed certificates:**
  - *Public CAs* trusts the image's roots.
  - *A private CA* trusts only the roots added on the new *Admin → Certificates* page; a server that sends an unknown root offers to add it.
  - *Pinned* trusts nothing but the accepted certificate (or its key), for devices with self-signed certificates; any change alarms. *Check expiry* can be turned off for a device whose old certificate nobody will renew.
- **Invalid and unreachable have states of their own:**
  - *invalid* (488): not trusted, the name doesn't match, not valid yet, or the chain incomplete, as the sensor checks;
  - *unreachable* (489): two failed checks in a row, with a retry after 15 minutes, so one blip doesn't fail a daily sensor for a day.

  While a verdict fails, the certificate is checked hourly, so a renewal turns it OK within the hour.
- **One event per change of the verdict,** not per check: rules "on every event" tell each change, not each day.
- **A rights area of its own, *Certificates*:**
  - *View* sees the Certificate tab and the private CAs;
  - *Edit* creates and changes certificate sensors (with Monitoring *Edit*), checks now and accepts a change;
  - *Manage* adds and removes private CAs.

  The sensors themselves, their state and events stay *Monitoring*.
- **MCP and Cookie:** `certificate_get` and `certificate_check_now`, and `sensor_create` for the new type with *Certificates: Edit*. Accepting a change and the private CAs are not tools.

## Updating from 1.12

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.13.0`. It takes a backup first.

- **One migration:** `CertificateSensors`, four new tables:
  - the sensors' settings and state;
  - the certificates they have seen;
  - the private CAs;
  - which sensors trust them.

  Nothing existing changes. It runs at the start.
- **Nobody gets the new area by the update,** as with Cookie, Availability and MCP: admins have it; give *Certificates: Edit* to the group that sets up certificate sensors (Operators, say), and *View* to whoever should read the Certificate tab. Without it, a certificate sensor still shows on the dashboard and the host page.
- **Nothing to configure.** The `Certificates__*` settings (deploy/README.md, *SSL certificates*) are optional.

## Good to know

- **PNMT reads only what the server sends:** a TCP connection and a TLS handshake to the sensor's endpoint, nothing else. It never fetches a URL a certificate names (AIA, OCSP, CRL), so a server can't make it call an internal address. A missing intermediate is reported, never repaired.
- **The first check pins** the certificate the sensor compares against, as a router's host key is pinned. A sensor created while a connection is intercepted would pin the interceptor's certificate: in the CA modes it must also be trusted; in the pinned mode, compare the fingerprint once with the device's own display.
- **Endpoints that speak only TLS 1.0 or 1.1,** or present keys weaker than OpenSSL's security level, fail the handshake and are *unreachable*; the message says the server may be too old for PNMT's TLS.
- **Short-lived certificates:** the tab warns when the warning days are more than a third of the certificate's lifetime, because renewals could then look like problems.
- **Revocation isn't checked,** and STARTTLS (SMTP, IMAP, LDAP) isn't supported yet: docs/ssl-certificate-plan.md §13.
- **The plan:** docs/ssl-certificate-plan.md, with the decisions (§12) and what was built (§15).
