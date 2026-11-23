<script setup lang="ts">
import { computed } from 'vue'

/** The documentation of boards (docs/boards-plan.md): monitoring dashboards for a wall, at a URL of their own. */
const props = defineProps<{ search: string }>()
const text =
  'boards board wall display dashboard kiosk tv screen url token pnmt-board networks ip ipv4 ipv6 signed in reverse proxy ' +
  'columns elements fit to window text size status bar rules green yellow red grey theme themes lounge redmine zabbix ' +
  'received today solved today newest tickets problems history clock note on duty certificates renew qr code'
const show = computed(() => {
  const q = props.search.trim().toLowerCase()
  return !q || text.toLowerCase().includes(q)
})
</script>

<template>
  <section v-if="show" class="panel" aria-labelledby="boards-docs-title">
    <div id="boards-docs-title" class="panel-header">Boards</div>
    <div class="panel-body prose">
      <p>
        <strong>A board shows chosen parts of PNMT, Redmine and Zabbix on a wall</strong>, at a URL of its own:
        <code>https://pnmt.example.com/pnmt-board/&lt;token&gt;</code>. Boards are set up under <em>Boards</em>, on a
        page of their own with a preview at the wall's screen size.
      </p>
      <ul>
        <li>
          <strong>Its own path.</strong> The board app and its data are all under <code>/pnmt-board/</code>, apart from
          <code>/pnmt-admin/</code> and <code>/api/</code>: a reverse proxy can pass the boards without the admin UI, or
          the admin UI without the boards.
        </li>
        <li>
          <strong>Who opens it.</strong> Anyone with the URL, from the board's networks (IPv4 and IPv6; none means from
          anywhere). A signed-in PNMT user opens it from any address, in the browser they signed in with.
          <em>Renew URL</em> stops the old one at once. Showing the URL is in the audit log, and the board never shows
          its own URL.
        </li>
        <li>
          <strong>What it shows is its elements', to whoever holds the URL.</strong> Adding an element needs its area's
          right: a Redmine element <em>Redmine: View</em>, a Zabbix one <em>Zabbix: View</em>. Private Redmine tickets
          show without subject; sensor messages only where an element says so; people by name, by initials or not at
          all, as the board says.
        </li>
        <li>
          <strong>The layout:</strong> 1 to 4 columns, each with its elements in order. <em>Fit to window</em> takes
          exactly the screen and scales the text with its height; lists show what fits and how many more there are.
          Without it, the board is a page that scrolls, its columns stacked on a phone.
        </li>
        <li>
          <strong>The status bar</strong> is green, yellow or red by its rules: a measure (failing sensors from an
          importance, Zabbix problems from a severity, Redmine's counts, the uplink …), at least how many, and the
          colour. The worst matching rule wins. It turns grey when a source can't be read and nothing worse matched, and
          when the board has had no answer for three refreshes: a board never stays green on old data.
        </li>
        <li>
          <strong>Elements:</strong> the Redmine overview, the newest tickets, tickets received and solved today (in
          PNMT's time zone; solved is a closed status), Zabbix's problems by severity and its latest problems with their
          recovery, the lounge (read-only), sensors, failing sensors, status tiles, host bars, recent changes, the
          uplink, a clock, a note, who is on duty, and expiring certificates.
        </li>
        <li>
          <strong>Themes:</strong> fifteen, from Default and Dark mode to Terminal, Windows 95 and Amiga. A theme never
          changes what green, yellow and red mean.
        </li>
        <li>
          <strong>On the wall</strong> the board keeps the screen awake, hides the cursor, toggles full screen on
          <kbd>F</kbd>, and reloads itself after an update of PNMT. Its tab's title and icon show the bar's colour.
        </li>
      </ul>
    </div>
  </section>
</template>
