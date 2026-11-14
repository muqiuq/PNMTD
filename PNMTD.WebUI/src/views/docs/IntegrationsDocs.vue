<script setup lang="ts">
import { computed } from 'vue'

/** The documentation of the integrations (docs/audit-redmine-zabbix-plan.md §6, §10.3): Redmine. */
const props = defineProps<{ search: string }>()
const has = (...texts: string[]) => {
  const q = props.search.trim().toLowerCase()
  return !q || texts.some((t) => t.toLowerCase().includes(q))
}
const redmineText =
  'redmine tickets issues overview search link unlink new in redmine details copy sync projects api key panel chip urgent overdue unassigned stale 429'
const showRedmine = computed(() => has(redmineText))
</script>

<template>
  <section v-if="showRedmine" class="panel" aria-labelledby="redmine-docs-title">
    <div id="redmine-docs-title" class="panel-header">Redmine</div>
    <div class="panel-body prose">
      <p>
        <strong>PNMT shows the tickets of the Redmine projects the deployment names</strong>
        (<code>Redmine__Projects</code>), through one Redmine account whose API key is in the deployment's
        <code>.env</code>. Who sees what in PNMT is decided by the area <em>Redmine</em>: <em>View</em> shows the
        tickets, <em>Link</em> links and unlinks them. PNMT doesn't change tickets in Redmine.
      </p>
      <ul>
        <li>
          <strong>Integrations → Redmine</strong>: the overview (open, urgent, new and unassigned, overdue, linked to a
          problem in PNMT, without an update for a while, recently updated) and the search with its filters. A number
          (<code>1234</code> or <code>#1234</code>) opens the ticket.
        </li>
        <li>
          <strong>Every ticket has two buttons</strong>: <em>Details</em>, PNMT's page of the ticket with its
          description, history, details and links, and <em>Redmine</em>, the ticket in Redmine.
        </li>
        <li>
          <strong>A ticket can be linked</strong> to knowledge base pages, locations, hosts and sensors, from the
          ticket's page (<em>Link to…</em>) or from their <em>Tickets</em> panels (<em>Link ticket…</em>). A host shows
          its own tickets and those of its location; a sensor those of its host too; a location's <em>Tickets</em> tab
          those of its hosts and sub-locations. <em>New in Redmine</em> opens Redmine's form, prefilled with the
          object's name and its link in PNMT.
        </li>
        <li>
          <strong>A failing sensor with an open linked ticket</strong> shows the ticket next to it, on the dashboard and
          on its page: someone is on it.
        </li>
        <li>
          <strong>PNMT keeps a copy of the linked tickets</strong> (number, subject, status, assignee), refreshed every
          few minutes, so the panels work when Redmine is slow or down; descriptions, comments and attachments always
          come from Redmine. When Redmine stops answering, the system sensor <em>Redmine</em> reports <code>429</code>.
        </li>
      </ul>
    </div>
  </section>
</template>
