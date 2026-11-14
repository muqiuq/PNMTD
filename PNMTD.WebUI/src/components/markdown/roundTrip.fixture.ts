/**
 * Thirty Markdown documents as PNMT's pages and fields are written (docs/locations-knowledge-plan.md
 * K0): by hand in the code editor, pasted from elsewhere, or from GitHub. roundTrip.test.ts counts
 * the lines Crepe rewrites in each.
 */
export const roundTripCorpus: { name: string; markdown: string }[] = [
  {
    name: 'headings and paragraphs',
    markdown: `# Printer setup, 2nd floor

The Ricoh on the 2nd floor prints through **print01**.

## Drivers

Install from \`\\\\print01\\ricoh\`, then pick *Ricoh IM C3000*.
`,
  },
  {
    name: 'dash lists',
    markdown: `## Checklist

- Power on the UPS
- Check the uplink
- Log the time
`,
  },
  {
    name: 'star lists',
    markdown: `* fw-zrh-01
* sw-core-01
* nas-01
`,
  },
  {
    name: 'plus lists',
    markdown: `+ first
+ second
`,
  },
  {
    name: 'nested lists',
    markdown: `- Building A
  - Room 2.13
    - Rack 4
  - Room 2.14
- Building B
  1. Cage B
  2. Cage C
`,
  },
  {
    name: 'ordered lists',
    markdown: `1. Open the cage
2. Unplug the old battery
3. Plug in the new one

Then:

3. Wait for the self-test
4. Close the cage
`,
  },
  {
    name: 'loose list',
    markdown: `- First paragraph item

- Second, after a blank line

- Third
`,
  },
  {
    name: 'task list',
    markdown: `## Maintenance 2026-11

- [x] Replace UPS battery
- [ ] Update firmware on fw-zrh-01
- [ ] Rotate the backup disks
`,
  },
  {
    name: 'table',
    markdown: `| Host | IP | Role |
|------|----|------|
| fw-zrh-01 | 10.0.0.1 | Firewall |
| sw-core-01 | 10.0.0.2 | Core switch |
`,
  },
  {
    name: 'aligned table',
    markdown: `| Item | Count | Price |
| :--- | :---: | ----: |
| Patch cable | 24 | 3.50 |
| SFP+ | 4 | 89.00 |
`,
  },
  {
    name: 'table from a generator',
    markdown: `| Port | VLAN |
| ---- | ---- |
| ether1 | 10 |
| ether2 | 20 |
`,
  },
  {
    name: 'fenced code',
    markdown: `Restart the service:

\`\`\`bash
sudo systemctl restart nginx
journalctl -u nginx --since "10 min ago"
\`\`\`
`,
  },
  {
    name: 'code in several languages',
    markdown: `\`\`\`yaml
services:
  pnmtd:
    image: pnmtd:1.2
\`\`\`

\`\`\`powershell
Get-Service -Name Spooler | Restart-Service
\`\`\`

\`\`\`
plain text without a language
\`\`\`
`,
  },
  {
    name: 'tilde fence',
    markdown: `~~~sql
SELECT * FROM hosts WHERE location_id IS NULL;
~~~
`,
  },
  {
    name: 'indented code',
    markdown: `An old export:

    /ip address
    add address=10.0.0.1/24 interface=bridge
`,
  },
  {
    name: 'mermaid',
    markdown: `\`\`\`mermaid
flowchart LR
  isp[Init7 uplink] --> fw[fw-zrh-01]
  fw --> core[sw-core-01]
\`\`\`
`,
  },
  {
    name: 'images',
    markdown: `![Cage B, front](/api/v1/files/0193f3c2/cage-b.png)

The floor plan:

![Floor plan](/api/v1/files/0193f3c3/floor-plan.webp "2nd floor")

Inline ![icon](/api/v1/files/0193f3c4/icon.png) in a line.
`,
  },
  {
    name: 'attachments',
    markdown: `The manual: [UPS manual.pdf](/api/v1/files/0194aa/ups-manual.pdf), and the
config [router.rsc](/api/v1/files/0194ab/router.rsc).
`,
  },
  {
    name: 'links',
    markdown: `See [KB-12](/pnmt-admin/kb/12), the [vendor's page](https://example.com/docs "Docs"),
and <https://status.example.com>. Mail <noc@example.com>.
`,
  },
  {
    name: 'reference links',
    markdown: `The [runbook][rb] and the [wiki][].

[rb]: https://example.com/runbook
[wiki]: https://example.com/wiki
`,
  },
  {
    name: 'emphasis variants',
    markdown: `Some *em*, some _em_, **strong**, __strong__, ***both***, ~~gone~~ and \`code\`.
`,
  },
  {
    name: 'hard line breaks',
    markdown: `Anna Muster
Bahnhofstrasse 1
8001 Zürich

Or with a backslash:\\
next line
`,
  },
  {
    name: 'soft wraps',
    markdown: `This paragraph was wrapped at eighty columns by an editor, so its lines end
in the middle of sentences, and it continues on the next line until it
stops here.
`,
  },
  {
    name: 'blockquote',
    markdown: `> **Note:** the badge reader fails when it is cold.
> Ask at the front desk.

> Nested:
> > inner
`,
  },
  {
    name: 'rules and setext headings',
    markdown: `Overview
========

Part one
--------

***

Text after a rule.

___
`,
  },
  {
    name: 'escapes',
    markdown: `Use \\* for a literal star, 2 \\* 3 = 6, and a_b_c stays. A \\[bracket\\] and 50 \\% off.
`,
  },
  {
    name: 'raw HTML',
    markdown: `Press <kbd>Ctrl</kbd> + <kbd>K</kbd>.

<details>
<summary>More</summary>

Hidden text.

</details>
`,
  },
  {
    name: 'footnotes',
    markdown: `The circuit ID is on the contract[^1].

[^1]: Contract 12-3456, Init7.
`,
  },
  {
    name: 'umlauts and emoji',
    markdown: `## Zürich – Gebäude A

Schlüssel beim Empfang. ✅ Geprüft am 14. November.
`,
  },
  {
    name: 'a full page',
    markdown: `# Power outage procedure

**Applies to:** Zurich DC, cage B.

## When the UPS alarms

1. Check the UPS display.
2. If on battery for more than 5 minutes:
   - shut down \`nas-01\` first,
   - then the hypervisors.

| Device | Runtime |
|---|---|
| UPS-1 | 18 min |

\`\`\`bash
ssh admin@nas-01 'sudo shutdown -h now'
\`\`\`

- [ ] Inform the on-call
- [ ] Log in the ticket

See also [KB-31](/pnmt-admin/kb/31).
`,
  },
]
