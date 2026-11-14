<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'

/**
 * The documentation of groups and rights, and of the audit log (docs/audit-redmine-zabbix-plan.md §5, §3, §10.3). The
 * areas and levels come from the server's catalog, so this page always says what PNMT does.
 */
const props = defineProps<{ search: string }>()
const has = (...texts: string[]) => {
  const q = props.search.trim().toLowerCase()
  return !q || texts.some((t) => t.toLowerCase().includes(q))
}

const catalog = useQuery({ queryKey: keys.rights, queryFn: api.rights.catalog, staleTime: Infinity })
const rightsText =
  'groups rights admin flag areas levels member operators viewers view edit manage run read decide link details access missing_right 403'
const areas = computed(() =>
  (catalog.data.value ?? []).filter((a) => has(rightsText, a.label, ...a.levels.map((l) => `${l.level} ${l.allows}`))),
)
const auditText =
  'audit log auth log who what when address changes before after secret changed outcome succeeded failed denied export csv retention history keep days entries'
const showAudit = computed(() => has(auditText))
</script>

<template>
  <section v-if="areas.length" class="panel" aria-labelledby="rights-docs-title">
    <div id="rights-docs-title" class="panel-header">Groups and rights</div>
    <div class="panel-body prose">
      <p>
        <strong>A user is an admin, or has what their groups give.</strong> An admin has every right, and alone manages
        users and groups (Admin → Users, Admin → Groups). A group gives a level in each area; a member of several groups
        has the highest level any of them gives, and a level includes the ones before it. There are no exceptions for
        one user and no rights on single objects.
      </p>
      <p>
        The update to 1.4 made the roles into groups: <em>Operators</em> (what User could do) and <em>Viewers</em> (what
        Viewer could). They are ordinary groups. A change to a group applies to its members from their next request; the
        admin flag ends their sessions. When something is refused, PNMT says which right is missing.
      </p>
      <table class="docs-table">
        <thead>
          <tr>
            <th scope="col">Area</th>
            <th scope="col">Levels, and what each allows</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="a in areas" :key="a.area">
            <th scope="row">{{ a.label }}</th>
            <td>
              <div v-for="l in a.levels" :key="l.level">
                <strong>{{ l.level }}</strong>: {{ l.allows }}
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>

  <section v-if="showAudit" class="panel" aria-labelledby="audit-docs-title">
    <div id="audit-docs-title" class="panel-header">The audit log</div>
    <div class="panel-body prose">
      <p>
        <strong>Every operation is in the audit log:</strong> every change through the web UI or the API, by anyone,
        including those that failed or were refused, the sign-ins, the CLI's commands, what agents did for someone, and
        the few changes PNMT makes to its configuration itself. Reading is not logged, except for downloads of backups,
        tunnels and join scripts, and the log's own export.
      </p>
      <p>
        An entry says who, when, from where, what, on what, how it ended, and
        <strong>what changed, field by field</strong>. Secrets are never in it: a changed token or password shows as
        <em>changed</em>. Long texts, such as a page's Markdown, show as the lines added and removed; the page's history
        has the text.
      </p>
      <p>
        Admin → Audit log (Audit log: View) filters by category, event, outcome, who and time, opens each entry with its
        changes, and downloads what it shows as CSV. A host's and a sensor's <em>History</em> tab shows their own
        entries to everyone who sees monitoring, without addresses. Entries are kept for
        <code>Audit__KeepDays</code> (400 days), at most <code>Audit__MaxEntries</code> (500,000); nobody can delete
        them through PNMT.
      </p>
    </div>
  </section>
</template>

<style scoped>
.prose p {
  margin: 0 0 var(--pnmt-space-3);
  max-width: 75ch;
}

.docs-table {
  width: 100%;
  border-collapse: collapse;
}

.docs-table th,
.docs-table td {
  text-align: left;
  vertical-align: top;
  padding: var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
}

.docs-table th[scope='row'] {
  white-space: nowrap;
  width: 12rem;
}
</style>
