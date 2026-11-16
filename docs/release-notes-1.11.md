# PNMT 1.11: a message with powers, and Seraph's veto

Version 1.11.0.

## What's new

- **An admin can make Cookie act from the chat** (docs/cookie-powers-plan.md). A checkbox *With powers* in the chat, shown to admins only and absent for everyone else, opens a warning that has to be confirmed every time. Cookie then proposes exactly what that one message asks for (Pushover, e-mail, a ticket, notes on tickets, hosts and sensors), and the executor carries it out past the gates that are judgement: the action's level, including *Off*; the urgency rule; whether the recipient works today; mutes and suppressions; duplicates; the confidence threshold; working hours; the inbox for *Propose*; the daily limits. The gates that are facts stay: a real PNMT user with a Pushover key or address, a writable Redmine, Cookie's group's rights, nobody outside PNMT.
- **Seraph reads every order and judges harm only.** Its verdict now carries a confidence, in every review. A reject at or above *Seraph stops an order from this confidence* (Admin → Cookie → Actions, 0.90 by default) stops the order; a less sure one waits for the admin in the decisions inbox with Seraph's reason, where one click sends it. Seraph also reviews notes on hosts and sensors when they are ordered, although it doesn't for a case.
- **Everything is on record.** The admin's message is tagged *with powers* in the thread; the reply shows each action with its outcome, written by code, never by the model; the decision log marks each entry *ordered by* and shows Seraph's confidence; the audit log has the admin's order (`CookiePowersUsed`) and each send with the admin's name.
- **The chat's request instruction** names the exception; an installation that overrode it keeps its own text.

## Updating from 1.10

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.11.0`. It takes a backup first.

- **One migration:** `Empowered` on `cookie_messages`, `OrderedBy` and `VerdictConfidence` on `cookie_decisions`. It runs at the start.
- **A new setting on Admin → Cookie → Actions,** *Seraph stops an order from this confidence*, 0.90; nothing to do.
- **MCP messages never carry powers;** nothing changes for MCP clients.

## Good to know

- **Powers are the admin flag's, not a Cookie level:** *Cookie: Manage* is not enough, and the endpoint refuses anyone else whatever the UI did.
- **An order passes mutes and the daily limits.** The detail of the decision names every gate it passed, so an admin sees whose mute they overrode.
- **The plan:** docs/cookie-powers-plan.md, with the decisions (§10) and what was built (§12).
