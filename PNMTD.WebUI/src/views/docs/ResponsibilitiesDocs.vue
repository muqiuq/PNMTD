<script setup lang="ts">
import { computed } from 'vue'

/** The documentation of responsibilities (docs/responsibilities-plan.md): who owns what, next to the availability plan's words. */
const props = defineProps<{ search: string }>()
const text =
  'responsibilities responsibility responsible deputy global responsible global deputy stands in owner area of work ' +
  'description links pages locations hosts redmine project tracker every tracker subproject combination history restore ' +
  'responsible of the day fallback availability plan mcp cookie responsibilities_list responsibility_get rights view manage'
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
          <strong>Links</strong> tie a responsibility to knowledge base pages, locations and hosts, set on its page. A
          host, a location or a page it links shows a <em>Responsibilities</em> panel. Nothing is inherited: a host in a
          linked location shows nothing until the host is linked itself.
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
          location or page, or by a Redmine project) and <code>responsibility_get</code>, and never change them. A page
          marked sensitive is never named. Cookie reads them once its rights group holds
          <em>Responsibilities: View</em>; in a chat, the person asking needs it too.
        </li>
        <li>
          <strong>Rights</strong>, in the area <em>Responsibilities</em>: <em>View</em> reads the page and the panels;
          <em>Manage</em> creates, changes and deletes them, their links and Redmine combinations, and sets the global
          people. No group gets either by itself: an admin gives them on <em>Admin → Groups</em>.
        </li>
        <li>
          <strong>Every change is in the audit log</strong>; a link or unlink also shows in the object's own history.
          Deleting a responsibility removes its links and combinations at once.
        </li>
      </ul>
    </div>
  </section>
</template>
