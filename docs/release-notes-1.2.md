# PNMT 1.2: router backups off, rarer, or encrypted one way

Version 1.2.0.

## What changes

- **Each router's backup schedule:** off, every few hours, daily (still the default), weekly, monthly, or up to once a year.
  - New routers start with the default set on *Admin → MikroTik & WireGuard → Backups*.
  - *Routers → Backup settings…* changes many routers at once.
  - With the schedule off, *Back up now* and the exports of updates and commands still take one; no 442 is written.
- **Backups encrypted one way, per router:**
  - **Set up once:** an Admin sets up a backup key on the new *Backups* tab, by generating it in the browser (the key file is downloaded and loaded back before it can be activated) or by pasting an `age1…` key from `age-keygen`.
  - **Then per router:** routers with *Encrypt backups* on store their exports and binary backups in the [age](https://age-encryption.org) format.
  - **PNMT can't read them:** it only has the public key, so a copy of its database or of a `./backup.sh` folder reveals no encrypted configuration.
- **Reading encrypted backups:**
  - **In the router's *Backups* tab:** *Load key…* keeps the key in that browser tab only, for 15 minutes without use; versions are viewed, compared and downloaded decrypted there.
  - **Offline:** `age -d -i pnmt-backups.age-key router-…-v12.rsc.gz.age | gunzip`.
- **Change detection uses keyed digests** instead of the plain text and its SHA-256, the same for every router. "Configuration changed" keeps its line counts.

## Updating from 1.1

`git pull` in `deploy/`, then `./update.sh --tag v1.2.0`. The migrations run when the new image starts. Every router keeps its daily schedule, and nothing is encrypted until an Admin sets up a key and turns encryption on for a router.

## Good to know

- **Keep the private key safe, in two copies:** losing it loses the backups encrypted with it, and PNMT cannot help.
- **What stays readable:** a PNMT that is taken over can still read the routers' current configuration live. Encryption protects their history, and so do the metadata (router, time, size, lines changed).
- **Earlier backups stay as they were.** Retiring the key turns encryption off for every router; encrypted versions stay encrypted.
- **Setting the key needs HTTPS;** decrypting in the browser needs HTTPS or localhost.
