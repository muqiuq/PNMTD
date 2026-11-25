<script setup lang="ts">
import { computed } from 'vue'

/**
 * The documentation of responsibilities (docs/responsibilities-plan.md, docs/responsibilities-extensions-plan.md): who owns what, next
 * to the availability plan's words; the Links tab, the colours and the Overview.
 */
const props = defineProps<{ search: string }>()
const text =
  'responsibilities responsibility responsible deputy global responsible global deputy stands in owner area of work ' +
  'description links pages locations hosts redmine project tracker every tracker subproject combination history restore ' +
  'responsible of the day fallback availability plan mcp cookie responsibilities_list responsibility_get rights view manage ' +
  'edit sort sorted column colour color swatch links tab without a responsibility unlinked covered move also link bulk ' +
  'overview map arrange layout save layout automatic notes note sticky people'
const show = computed(() => {
  const q = props.search.trim().toLowerCase()
  return !q || text.toLowerCase().includes(q)
})
</script>

<template>
  <section v-if="show" class="panel" aria-labelledby="responsibilities-docs-title">
    <div id="responsibilities-docs-title" class="panel-header">Responsibilities</div>
    <div class="panel-body prose">
      <p>
        <strong>Responsibilities say who owns which area of work</strong>, such as Network, Backup or Printers (Admin →
        Responsibilities). They are information only: nobody is sent anything because of them. Alerts and Cookie's
        reminders still follow the notification rules and the availability plan.
      </p>
      <ul>
        <li>
          <strong>A responsibility</strong> has a name, a short description, and a long description written as a
          knowledge base page is: the same editor, images, files and links to pages. Its old texts are kept under
          <em>History</em>, and <em>Restore</em> brings back the text from before a change.
        </li>
        <li>
          <strong>Its responsible and its deputy</strong> are PNMT users, never the same one, and either may be empty.
          Someone deactivated later stays set and is marked <em>inactive</em>; <em>Needs attention</em> on the list
          finds such responsibilities, and those with nobody.
        </li>
        <li>
          <strong>The global responsible and the global deputy</strong> own what no responsibility covers, and stand in
          for a responsibility whose own responsible and deputy are not set or not active: it then shows
          <em>Global responsible stands in</em>.
        </li>
        <li>
          <strong>The list</strong> sorts by a click on a column's header: by name, responsible, deputy, links, Redmine
          combinations or when it was last changed. Nobody comes last both ways; where the global responsible stands in,
          the row sorts by their name. The sort is kept in the address, so going back to the list keeps it.
        </li>
        <li>
          <strong>A colour</strong>, chosen on its page from twelve swatches or as any <code>#rrggbb</code>, shows as a
          dot next to its name: on the list, the Links and Redmine tabs, and the panels of what it links; on the
          Overview it fills its circle. The name always stands beside it.
        </li>
        <li>
          <strong>Links</strong> tie a responsibility to knowledge base pages, locations and hosts, set on its page. A
          host, a location or a page it links shows a <em>Responsibilities</em> panel. Nothing is inherited: a host in a
          linked location shows nothing until the host is linked itself.
        </li>
        <li>
          <strong>The <em>Links</em> tab</strong> lists every link of every responsibility, filtered by responsibility,
          by person (responsible or deputy), by kind and by location, and searched in names and places. It also shows
          the <em>hosts without a responsibility</em> (with <em>Monitoring: View</em>) and the
          <em>locations without a responsibility</em> (with <em>Locations: View</em>), naming those that link their
          location as a hint; <em>Count a linked location as covering what is in it</em> leaves those out of that view
          only. With <em>Edit</em>: <em>Link…</em>, and for the rows selected <em>Link to…</em>, <em>Move to…</em>,
          <em>Also link to…</em> and <em>Unlink</em>, up to 500 at once. What is linked already is skipped, and a
          responsibility still links at most 200 objects.
        </li>
        <li>
          <strong>The <em>Overview</em> tab</strong> draws the responsibilities on a map, as the network map draws
          routers: the people they name (solid to the responsible, dashed to the deputy, dotted where the global
          responsible stands in), and as layers the hosts, locations and pages they link (at most 600, the most shared
          first). Drag a responsibility or a note, or move a focused one with the arrow keys; <em>Save layout</em> keeps
          the places for everyone, <em>Discard</em> puts them back, and
          <em>Layout → Back to automatic for all responsibilities…</em>
          clears them. People and linked objects are always placed automatically around them.
        </li>
        <li>
          <strong>Notes</strong> on the Overview: short Markdown (bold, italics, lists, links; no images or HTML, at
          most 2,000 characters) in one of six colours and three widths, free on the map. A new note goes in the middle
          of the view; its text changes in its dialog, its place with <em>Save layout</em>. A text that looks like a
          password is saved only once that is confirmed.
        </li>
        <li>
          <strong>Redmine</strong>, when PNMT reads Redmine: a responsibility can list a project with a tracker, or with
          every tracker, and the user who handles it there. Each project and tracker is listed once, on one
          responsibility. Subprojects are projects of their own. The user needs no Redmine link. The
          <em>Redmine</em> tab lists them all. No ticket is matched or changed.
        </li>
        <li>
          <strong>Not the responsible of the day.</strong> The availability plan's responsible and fallback say who is
          on duty on a day, for everything, and its global fallback stands in when nobody of the day works. A
          responsibility says who owns one area, every day. They can name the same people, and neither is copied from
          the other.
        </li>
        <li>
          <strong>MCP and Cookie</strong> read them with <code>responsibilities_list</code> (by words, by a linked host,
          location or page, or by a Redmine project) and <code>responsibility_get</code>. A page marked sensitive is
          never named. Over MCP, a token with <em>Responsibilities: Edit</em> also creates and changes them, their links
          and Redmine combinations (<code>responsibility_create</code>, <code>_update</code>, <code>_link</code>,
          <code>_redmine</code>); their history says <em>via MCP</em>. Colours, the Overview and its notes stay in the
          web UI. Cookie only reads them, once its rights group holds <em>Responsibilities: View</em>; in a chat, the
          person asking needs it too.
        </li>
        <li>
          <strong>Rights</strong>, in the area <em>Responsibilities</em>: <em>View</em> reads the page, its tabs and the
          panels; <em>Edit</em> creates and changes them, their colours, links and Redmine combinations, saves the
          Overview's layout and writes its notes; <em>Manage</em> also deletes them and sets the global people. No group
          gets any of them by itself: an admin gives them on <em>Admin → Groups</em>.
        </li>
        <li>
          <strong>Every change is in the audit log</strong>, the Overview's saved places and notes too; a link or unlink
          also shows in the object's own history, one entry per responsibility when many change at once. Deleting a
          responsibility removes its links, combinations and place at once.
        </li>
      </ul>
    </div>
  </section>
</template>
