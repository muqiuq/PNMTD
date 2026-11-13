# Plan: router backups off or less often, and optionally encrypted one way

| | |
|---|---|
| Status | **Built: B1 to B5, version 1.2.0** (§7, as built). Revision 2: the review's answers applied (§6). |
| Date | 2026-11-13 |
| Scope | The MikroTik routers' configuration backups (docs/mikrotik-plan.md §6): the scheduled `/export`, and every export and binary backup PNMT stores. |
| Goals | <ol><li>Turn the scheduled backup **off**, per router.</li><li>Choose **longer intervals**, up to a year.</li><li>**Encrypt one way, optionally, per router:** PNMT holds only a public key, and the private key never reaches PNMT's server. The key is set up once, in the GUI. Backups are read offline with `age`, or in the GUI with the key loaded into the browser.</li></ol> |
| Not in scope | PNMTD's own backups (`deploy/backup.sh`). Restoring a router. Encrypting backups stored before encryption was turned on. Post-quantum keys (later, §3.2). |

---

## 1. Today

- **The interval is per router:** `BackupIntervalHours`, 1 to 168 hours, default 24, set in the router's settings ("Backup every (hours)"). There is no *off*, and no installation-wide default.
- **When:** a poll exports when the interval has passed since the last export (`ExportDue`). A failed export is retried after `Backups:RetryMinutes` (30); two failures in a row write **442**.
- **Other triggers** export as well: the first login (version 1), *Back up now*, before and after an update, and before and after an admin command if chosen.
- **Storage:** gzip of the normalised export in `mikrotik_backups.content`, kept only when its SHA-256 differs from the last one. **243** names the change in lines (`+3 −1 lines`), counted against the previous version's text.
- **Diffs** are computed on the server (`LineDiff`), and the API returns exports as text.
- **Binary backups** are taken only in update jobs, encrypted by RouterOS with the admin's password, and stored as they are.
- **Retention:** every version younger than 365 days, and at least the last 50 versions per router.

## 2. The schedule: off, or up to a year

### 2.1 Per router

- The router setting becomes **Scheduled backups**: *Off*, or an interval.
- **Presets in the UI:** *Off* · every 6 hours · daily · weekly · monthly (30 days) · custom, in hours or days, up to 365 days.
- **Stored** as `backup_interval_hours`, now nullable: `null` is off, otherwise from 1 to 8760.
- **Who:** CanWrite, as for the interval today.

### 2.2 For many routers

- **Default for new routers:** set on the admin page (*Admin → MikroTik & WireGuard → Backups*), stored in `mikrotik_settings`. The new-router form starts with it.
- **Bulk change:** the Routers tab already selects routers for commands and updates. *Backup settings…* sets the schedule, and the encryption (§3.3), of the selected routers or of all routers that match the filter.

### 2.3 What *off* means

- **No scheduled export, and no 442.**
- ***Back up now*** still works (CanWrite).
- **Exports that belong to a job stay:** before and after an update, and before and after an admin command where chosen. The dialogs keep their own options.
- **No version 1 at the first login** while the schedule is off.
- **The status strip** says *Scheduled backups off*, or with an interval *next in 23 days*.

### 2.4 Long intervals

- **The scheduling stays as it is:** polls run every few minutes, and a poll exports once `LastExportAt` is older than the interval. A month or a year needs no new timer, and a restart loses nothing.
- **Retries** after a failed export still run after 30 minutes. 442 still comes after two failures in a row.
- **Retention** is unchanged. With a monthly export, the minimum of 50 versions keeps about four years' worth.

## 3. Encryption, one way, per router

### 3.1 What it protects, and what it doesn't

- **It protects** the encrypted routers' backups in PostgreSQL, and every copy of it: `backup.sh` dumps, off-site copies, a stolen disk. Neither the database nor the server holds anything that decrypts them.
- **It doesn't protect the routers' current configuration:** a PNMT that is taken over can still read it live, as `pnmt`. It cannot read the encrypted past versions.
- **Metadata stays readable:** router, time, version number, size, lines changed, and who or what took the backup.
- **Not encrypted:**
  - routers whose encryption is off;
  - every backup stored before a router's encryption was turned on (they stay as they are until retention removes them, or an Admin deletes them);
  - command output stored with jobs, so an `/export` run as a command is kept in plaintext in the command's history.

### 3.2 The format: age, with an X25519 key

- **Why age:** an open, documented format ([age-encryption.org/v1](https://age-encryption.org/v1)) with standard tools (`age`, `rage`, and libraries for Go, Rust and TypeScript). Backups stay readable without PNMT (§3.6). The public key is one line to paste (`age1…`).
- **The server only encrypts.** The encryptor lives in `PNMTD.Lib`, about 150 lines:
  - **X25519** from BouncyCastle, which is in the dependency tree already through SSH.NET. It becomes a direct reference at the same version.
  - **HKDF-SHA256, ChaCha20-Poly1305 and HMAC-SHA256** from .NET itself.
  - **Checked against** the `age` CLI and the TypeScript implementation.
- **The browser** uses `age-encryption`: typage, the TypeScript implementation by age's author (BSD-3-Clause, with `@noble/*` and `@scure/base`, MIT). It generates keys and decrypts.
- **Not chosen:** RSA-OAEP with AES-GCM. It needs no new dependency, but the format would be our own, so no standard tool could decrypt it.
- **Post-quantum keys** (age 1.3's hybrid ML-KEM-768 + X25519, `age1pq1…`) come **later**. .NET 10 has ML-KEM and typage supports them; the key table (§4) already allows a second kind.

### 3.3 Turning it on

- **Once per installation, the backup key** (§3.5). Without it, the routers' encryption switch is disabled, with a link to set it up.
- **Per router, *Encrypt backups*:**
  - off by default;
  - with a default for new routers on the admin page;
  - in the bulk change on the Routers tab (§2.2).
- **Who:** Admin, in both directions, audit-logged: turning it off weakens a router's backups.
- **From the next backup on,** the router's exports and binary backups are encrypted with the active key. What was stored before stays as it is.

### 3.4 What an encrypted backup is

- **Every export of the router**, whatever its trigger: schedule, *Back up now*, first login, before and after an update or a command.
- **Binary backups** from update jobs: age on top of RouterOS's own encryption with the admin's password.
- **Stored content:** the gzip of the normalised export, then age.
- **Downloads** are named `<identity>-<date>-v<n>.rsc.gz.age`, or `….backup.age` for a binary backup.
- **Each backup notes its key** (`encrypted_with`), so the UI can say which key it needs.

### 3.5 Setting up the key in the GUI, once

A new section on *Admin → MikroTik & WireGuard → Backups* offers two ways.

- **Generate a key in this browser:**
  - typage creates an X25519 key pair in the browser.
  - The browser offers `pnmt-backups-<tag>.age-key` for download, in the format of `age-keygen`: its date and public key as comments, then `AGE-SECRET-KEY-1…`.
  - **To activate,** the admin loads the file back, or pastes the secret key. The browser checks that it matches, so a key that was never saved cannot be activated.
  - **Only the public key** (`age1…`) is sent to PNMT.
- **Use my own key:** paste an `age1…` public key from `age-keygen` on another machine. Then the private key has never been in a browser at all.

**The rules:**
- **Who and how:** Admin only, over HTTPS only (as for admin passwords), audit-logged.
- **What the page shows:** the public key, its short fingerprint, who set it when, and how many routers use it.
- **A warning before activation:** losing the private key loses every encrypted backup, and PNMT cannot help. Keep two copies, for example one in a password manager and one offline.
- **Replacing the key:** new backups use the new key, and old ones keep theirs. Retired keys stay listed with their fingerprints.
- **Turning encryption off for the installation is allowed:**
  - retiring the key without a successor turns *Encrypt backups* off on every router;
  - the dialog says how many routers that affects;
  - confirmed and audit-logged;
  - encrypted backups stay encrypted.

### 3.6 Reading encrypted backups, offline and in the GUI

**Offline,** with the standard tool, without PNMT:
```sh
age -d -i pnmt-backups-1a2b.age-key router-2026-11-13-v12.rsc.gz.age | gunzip > router.rsc
age -d -i pnmt-backups-1a2b.age-key router-2026-11-13-v13.backup.age > router.backup
```
deploy/README.md shows this, and where `age` comes from (`apt install age`, Homebrew, Windows builds).

**In the GUI:**
- ***Load key…*** in the Backups tab takes the key file, or a pasted key.
  - **Kept in the page's memory only:** never sent, never stored (no `localStorage`).
  - **Gone** on reload, on sign-out, or after 15 minutes without use.
- **With the key loaded, the browser:**
  - fetches the encrypted content, decrypts it with typage, and unzips it with `DecompressionStream`;
  - shows the export, computes diffs itself, and offers the plain `.rsc` as a download;
  - shows a command's *Show the change* the same way.
- **The diff in the browser:** `LineDiff` (`PNMTD.Lib`, 259 lines) is ported to TypeScript together with its C# tests, so the server and the browser show the same diff. Plaintext backups keep the server's diff. A diff between a plaintext and an encrypted version works too, with the key loaded.
- **Without the key:** the list, the metadata, the line counts, and the download of the `.age` file.
- **The risk:** while it is loaded, the key sits in a browser tab on PNMT's own site, so a script injected into PNMT could read it.
  - **Mitigations:** the existing Content Security Policy, loading on demand, and the idle timeout.
  - **Without that risk:** decrypt offline only.

### 3.7 Detecting changes, the same way for every router

PNMT can no longer read an encrypted router's previous export. So **every export**, encrypted or not, is compared by keyed digests instead of its text and plain SHA-256. That gives one code path.

- **The digest key:** a key under Data Protection, on the `pnmtd-keys` volume.
- **Whether it changed:** an HMAC-SHA256 of the normalised export takes the place of the plain SHA-256. A copy of the database alone therefore cannot confirm a guessed configuration.
- **The counts for 243** (`+3 −1 lines`):
  - PNMT keeps the HMACs of each router's newest export, one per line: only the newest, 16 bytes a line, also keyed;
  - the next export is counted against them, without the old text.
- **The switch after the upgrade, per router at its next export:** a router without line digests is compared with its newest stored export, which is plaintext at that point. An unchanged export gives that version its keyed hash, so the first export after the upgrade isn't counted as a change. Older versions keep their SHA-256; they are plaintext anyway. The content isn't touched.
- **If the key ring is lost** (`pnmtd-keys` not restored), each router's next export counts as a change once. That means one extra version, and a 243 that counts every line as changed.

### 3.8 API changes

| Endpoint | Change |
|---|---|
| router detail and update | `backupIntervalHours` may be `null` (off); new `encryptBackups` (changing it needs Admin) |
| `POST /mikrotik/routers/backup-settings` | **new:** the bulk change for many routers (schedule and/or encryption) |
| `GET …/backups` (list) | adds `encryptedWith`: the key's fingerprint |
| `GET …/backups/{id}` | `{ item, text }` as today, or `{ item, ciphertext }` (base64) |
| `GET …/backups/{id}/diff` | **409** `backup_encrypted` when either side is encrypted; the UI diffs it itself |
| `GET …/backups/{id}/download` | the stored age file for an encrypted backup |
| `GET`, `PUT /mikrotik/backup-settings` | **new:** the defaults for new routers (schedule, encryption) |
| `GET`, `POST`, `DELETE /mikrotik/backup-keys` | **new:** the keys, activating a public key, retiring it (turns encryption off, §3.5) |

`pnmtd mikrotik backup-key set age1…` does the same from the server's command line. This is optional.

## 4. Data model

| Table | Change |
|---|---|
| `mikrotik_routers` | `backup_interval_hours` becomes nullable (`null` is off, 1 to 8760); new `encrypt_backups` (bool); new `last_export_line_digests` (bytea, §3.7) |
| `mikrotik_settings` | new `default_backup_interval_hours`, `default_encrypt_backups`, `backup_key_id` (the active key) |
| `mikrotik_backup_keys` | **new:** `id`, `kind` (`x25519` now, `mlkem768x25519` later), `recipient` (`age1…`), `fingerprint`, `created_at`, `created_by`, `retired_at` |
| `mikrotik_backups` | new `encrypted_with` (key, nullable); `sha256` becomes `content_hash`, an HMAC (§3.7) |

- **The existing `encrypted` column** keeps its meaning: a binary backup encrypted by RouterOS with the admin's password.
- **The switch in §3.7** happens per router at its next export: no background step.

## 5. Phases

| Phase | Content | Size |
|---|---|---|
| **B1** | The schedule (§2): *off*, long intervals, the default, the bulk change, the status strip | small, ships on its own |
| **B2** | Keyed change detection for every router, and the switch (§3.7) | small |
| **B3** | Encryption on the server: the key, *Encrypt backups* per router, the encryptor, the API (§3.2–3.5, §3.8, §4) | medium |
| **B4** | The GUI: setting up the key, *Load key*, decrypting and diffing in the browser (§3.5, §3.6) | medium |
| **B5** | Docs (deploy/README.md with the offline steps, mikrotik-plan.md §6, release notes), version 1.2.0 | small |

**Testing, kept small:**
- the schedule logic, *off* included;
- the keyed counts against today's text-based counts on the same exports;
- the C# encryptor against the `age` CLI in a container, and against typage, with Vitest decrypting a file the C# code wrote;
- one API test for an encrypted backup;
- one browser test: set up a key, turn on a router's encryption, then read a backup with the key.

## 6. Decisions from the review

| # | Question | Decision |
|---|---|---|
| 1 | Is encryption optional? | **Yes.** Per router, off by default, once the installation has a key (§3.3). `backup.sh` stays out of scope. |
| 2 | What does *off* switch off? | **Only the scheduled export.** The exports of update and command jobs stay; there's no version 1 at the first login (§2.3). |
| 3 | Where is the schedule set? | **Per router,** with a default and a bulk change (§2.1, §2.2). |
| 4 | How are encrypted backups read? | **Both offline (`age`) and in the GUI** with the key loaded into the tab (§3.6). |
| 5 | Can encryption be turned off again? | **Yes.** Per router and for the installation; Admin, audit-logged (§3.3, §3.5). |
| 6 | Post-quantum keys? | **Later** (§3.2). |
| 7 | Line counts in 243? | **Yes,** through keyed line digests, for every router (§3.7). |
| 8 | What happens to backups stored before encryption? | **They stay as they are** (§3.1). |
| 9 | Longest interval? | **A year** (8760 hours, §2.1). |

## 7. As built

- **Three migrations,** one per phase: `MikrotikBackupSchedule`, `MikrotikBackupDigests`, `MikrotikBackupEncryption`.
- **The switch to keyed digests** happens per router at its next export (§3.7), not as a background step.
- **Who may do what:** reading the backup settings needs CanWrite (the new-router form starts with them). The defaults, the bulk change and the key need Admin.
- **HTTPS for the key:** `Mikrotik__AllowPasswordsOverHttp=true` lifts the rule for development, as it does for routers' admin passwords.
- **The browser's cryptography exists only in a secure context:** HTTPS, or localhost. On plain HTTP the dialogs say so, and the age tool reads the downloaded files. `tools/e2e.sh` therefore runs Playwright in the app container's network, on localhost.
- **No key, encryption on:** if a router's *Encrypt backups* is on and there is no active key, nothing is stored, and the export counts as failed. Retiring the key turns encryption off everywhere, so this happens only through a database edit.
- **Not built:** `pnmtd mikrotik backup-key set` (optional). The release notes are in docs/release-notes-1.2.md.
- **Tests:**
  - the schedule (off, monthly, the limits, the default, the bulk change);
  - keyed counts against the text-based counts, and the switch from SHA-256;
  - the C# encryptor, checked once against the age CLI 1.2.1 (0, 1, 65536, 65537 and 200000 bytes) and in the suite against the tests' own reading of the spec;
  - Vitest decrypting a file the C# code wrote;
  - two API tests for encrypted backups and for retiring the key;
  - one browser test: the seed's encrypted version and its change, then a new key made, saved, loaded back and activated.
