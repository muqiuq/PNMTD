<script setup lang="ts">
import Column from 'primevue/column'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys } from '@/api/resources'
import AuditChanges from '@/components/AuditChanges.vue'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useAuditCatalog, type AuditItem } from '@/composables/useAuditCatalog'
import { useServerTable } from '@/composables/useServerTable'
import { useSession } from '@/stores/session'

/**
 * An object's history (docs/audit-redmine-zabbix-plan.md §4.3): who changed what, from the audit log, for everyone who sees
 * monitoring; a host's includes its sensors'. Without addresses and browsers, which stay on the audit log page, linked
 * for those who may read it.
 */
const props = defineProps<{ subjectId: string; name: string }>()
const session = useSession()
const canReadLog = computed(() => session.can('AuditLog', 'View'))
const { label, severity } = useAuditCatalog(canReadLog)
const expanded = ref<Record<string, boolean>>({})

const table = useServerTable<AuditItem>({
  key: [...keys.audit, 'subject', props.subjectId],
  fetch: (q) => api.audit.subject(props.subjectId, q),
  pageSize: 25,
})
const who = (e: AuditItem) => (e.actor === 'User' ? e.username || '–' : e.actor === 'Cli' ? 'CLI' : e.actor)
</script>

<template>
  <section class="history">
    <div class="head">
      <p class="muted">Every change to {{ name }}, newest first: who, when, what changed.</p>
      <RouterLink v-if="canReadLog" :to="{ name: 'audit-log', query: { subject: subjectId, subjectName: name } }"
        >All in the audit log ›</RouterLink
      >
    </div>
    <ListTable v-model:expanded="expanded" :table="table" what="changes" empty-text="No changes recorded yet." fixed>
      <Column expander style="width: 2.5rem" />
      <Column header="When" style="width: 9rem">
        <template #body="{ data }"><RelativeTime :value="data.createdAt" /></template>
      </Column>
      <Column header="Who" style="width: 10rem">
        <template #body="{ data }"
          ><span class="ellipsis cell">{{ who(data) }}</span></template
        >
      </Column>
      <Column header="What" style="width: 13rem">
        <template #body="{ data }">
          <Tag :value="label(data.eventType)" :severity="severity(data.eventType)" />
          <Tag v-if="data.outcome !== 'Succeeded'" :value="data.outcome" severity="danger" class="outcome" />
        </template>
      </Column>
      <Column header="Detail">
        <template #body="{ data }"
          ><span class="ellipsis cell" :title="data.detail ?? ''">{{ data.detail }}</span></template
        >
      </Column>
      <template #expansion="{ data }">
        <div class="details">
          <p v-if="data.detail" class="wrap">{{ data.detail }}</p>
          <AuditChanges v-if="data.changes.length" :changes="data.changes" />
          <p v-if="data.error" class="muted">Error: {{ data.error }}</p>
        </div>
      </template>
      <template #mobile="{ item }">
        <div class="line">
          <Tag :value="label(item.eventType)" :severity="severity(item.eventType)" />
          <span>{{ who(item) }}</span>
          <RelativeTime :value="item.createdAt" class="muted right" />
        </div>
        <div v-if="item.detail" class="muted clamp">{{ item.detail }}</div>
      </template>
    </ListTable>
  </section>
</template>

<style scoped>
.head {
  display: flex;
  justify-content: space-between;
  align-items: baseline;
  gap: var(--pnmt-space-3);
  margin-bottom: var(--pnmt-space-2);
}

.head p {
  margin: 0;
}

.cell {
  display: block;
}

.outcome {
  margin-left: var(--pnmt-space-1);
}

.details {
  display: grid;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2) 0;
}

.wrap {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
}

.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.right {
  margin-left: auto;
}

.clamp {
  display: -webkit-box;
  -webkit-line-clamp: 2;
  line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}
</style>
