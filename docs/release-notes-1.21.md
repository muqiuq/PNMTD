# PNMT 1.21: responsibilities, who owns what

## What's new

### Responsibilities

- **Admin → Responsibilities** (docs/responsibilities-plan.md): who owns which area of work, such as Network, Backup or Printers.
  **Information only:** nobody is sent anything because of them. Alerts and Cookie's reminders still follow the notification rules and
  the availability plan.
- **A responsibility** has:
  - a name and a short description;
  - **a long description, written as a knowledge base page is** (§4.1): the same editor, images, files and links to pages, the 1 MB
    limit, the warning for lines that look like credentials, and edit conflicts. Its old texts are kept under *History*, and *Restore*
    brings back the text from before a change;
  - **a responsible and a deputy**, PNMT users, never the same one. Someone deactivated later stays set and is marked *inactive*;
  - **links to knowledge base pages, locations and hosts.**
- **The global responsible and the global deputy** (§5), set on top of the list. They own what no responsibility covers, and stand in for
  a responsibility whose own people are not set or not active.
- ***Needs attention*** on the list finds responsibilities with nobody, with an inactive person, or with a Redmine combination whose user
  left.
- **A *Responsibilities* panel on hosts, locations and pages** that a responsibility links (§8). Nothing is inherited: a host in a linked
  location shows nothing until the host is linked itself.
- **Redmine, when PNMT reads Redmine** (§6): a responsibility can list Redmine projects with a tracker, or with every tracker, each with
  the user who handles it there and a note.
  - Each project and tracker is listed once, on one responsibility.
  - Subprojects are projects of their own.
  - The user needs no Redmine link; the page says when they have none.
  - The *Redmine* tab lists every combination, by project, responsibility or user.
  - No ticket is matched or changed.
- **MCP and Cookie read them** (§7), read-only, with `responsibilities_list` and `responsibility_get`. A page marked sensitive is never
  named, and Cookie knows the people as it knows them elsewhere.
- **Every change is in the audit log**, under the new category *Responsibilities*; a link or unlink also shows in the object's own
  history.

## Updating from 1.20

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.21.0`. It takes a backup first.

- **Three migrations run at the start:** `Responsibilities` and `ResponsibilityRedmine` add new tables (and the one row of the global
  people), and `ResponsibilityFiles` lets a responsibility's description own files as pages and locations do. Nothing existing changes.
- **Only admins see Admin → Responsibilities** until an admin gives *Responsibilities: View* or *Manage* to a group (*Admin → Groups*).
  No group gets it by itself.
- **For Cookie to read them,** give *Responsibilities: View* to Cookie's group (*Admin → Cookie → Rights*). In a chat, the person asking
  needs it too.
- **Nothing to configure.**

## Good to know

- **Not the responsible of the day.** The availability plan's responsible and fallback say who is on duty on a day, for everything; a
  responsibility says who owns one area, every day. The documentation page puts both side by side.
- **Uploads and files:** *Responsibilities: Manage* uploads files into a description, and *View* reads them, as the knowledge base's and
  locations' rights do for theirs.
- **The Redmine parts appear only while Redmine is configured.** Combinations made before stay and come back with it. Their names are
  refreshed from what PNMT read of Redmine last, without asking Redmine.
- **Deleting a responsibility** removes its links, its Redmine combinations and its history at once; the audit log keeps what it was.
- **No MCP token gets *Responsibilities: Manage*.** MCP reaches *View* only.
- **`pnmtd dev seed`** adds three responsibilities (one with nobody, so the global people stand in), their links, two Redmine
  combinations, and the global people.
- **The plan:** docs/responsibilities-plan.md, with its decisions and, in *As built*, where the build differs from it.
