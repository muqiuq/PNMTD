<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import type { MenuItem } from 'primevue/menuitem'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useNow } from '@/composables/useNow'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute } from '@/format'
import { formatBytes } from '@/format/bytes'
import { codeTone, healthTone } from './advanced'
import { codeLabel, repeatText } from './dataTab'
import ReportCompareDialog from './ReportCompareDialog.vue'

/**
 * An Advanced sensor's reports (docs/advanced-sensor-plan.md §10.3), newest first: received, repeats, the verdict at each
 * (*judging…* until the evaluator has judged it), its failures and warnings, the app's status and message, and the size.
 * A row opens that report in the tree with its outcomes then; *Compare with the previous* shows the lines that changed.
 * With Edit, a report that carried what it shouldn't can be deleted, or all of them (§13), each asked first.
 */
const props = defineProps<{ sensor: S['SensorDetail']; canWrite: boolean; shownId: string | null }>()
const emit = defineEmits<{ open: [reportId: string]; deleted: [reportId: string | null] }>()
const { done, ask } = useActions()
const now = useNow()

const id = props.sensor.id
const table = useServerTable<S['AdvancedReportItem']>({
  key: [...keys.advanced(id), 'reports'],
  fetch: (q) => api.advanced.reports(id, q),
  pageSize: 25,
})
const count = computed(() => props.sensor.advanced?.reportCount ?? table.total.value)
const comparing = ref<string | null>(null)

const statusTone = (r: S['AdvancedReportItem']) => healthTone(r.status) ?? 'muted'
const rowClass = (r: S['AdvancedReportItem']) => (r.id === props.shownId ? 'row-shown' : undefined)
const invalidate = [keys.advanced(id), keys.sensor(id)] as const

async function remove(r: S['AdvancedReportItem']) {
  const repeats = r.repeats ? ` and its ${r.repeats} repeat${r.repeats === 1 ? '' : 's'}` : ''
  if (
    !(await ask(
      'Delete this report?',
      `The report of ${formatAbsolute(r.receivedAt)}${repeats} is deleted, for good, and the deletion is audited. The sensor's verdict stays until the next report.`,
    ))
  )
    return
  if (
    await done(() => api.advanced.deleteReport(id, r.id), {
      success: 'Report deleted',
      failure: 'Not deleted',
      invalidate,
    })
  ) {
    emit('deleted', r.id)
  }
}

async function clear() {
  if (
    !(await ask(
      'Delete all reports?',
      `All ${count.value} reports of ${props.sensor.name} and their charted values are deleted, for good, and the deletion is audited. The rules and fields stay, and the verdict stays until the next report.`,
      'Delete all',
    ))
  )
    return
  if (
    await done(() => api.advanced.clearReports(id), {
      success: 'All reports deleted',
      failure: 'Not deleted',
      invalidate,
    })
  ) {
    emit('deleted', null)
  }
}

const more = computed<MenuItem[]>(() =>
  props.canWrite ? [{ label: 'Delete all reports…', icon: 'pi pi-trash', command: clear }] : [],
)
</script>

<template>
  <section class="history" aria-labelledby="advanced-history-title">
    <div class="title-row">
      <h2 id="advanced-history-title" class="title">
        History <span class="muted count">{{ count }}</span>
      </h2>
      <FilterBar v-model:search="table.search.value" placeholder="Search status and message" />
      <MoreMenu v-if="more.length" :items="more" label="More actions for the reports" />
    </div>
    <ListTable
      :table="table"
      what="reports"
      empty-text="No reports."
      selectable
      :row-class="rowClass"
      @select="(r) => emit('open', r.id)"
    >
      <Column header="Received" style="width: 9rem">
        <template #body="{ data }">
          <RelativeTime :value="data.receivedAt" />
          <span v-if="data.id === shownId" class="shown-tag">shown</span>
        </template>
      </Column>
      <Column header="Repeats" style="width: 9rem">
        <template #body="{ data }">
          <span v-if="data.repeats" class="small">{{ repeatText(data, now) }}</span>
        </template>
      </Column>
      <Column header="Verdict" style="width: 11rem">
        <template #body="{ data }">
          <span v-if="!data.evaluated" class="muted small judging">
            <i class="pi pi-spin pi-spinner" aria-hidden="true" /> judging…
          </span>
          <span v-else class="verdict">
            <span class="code" :class="`tone-${codeTone(data.code)}`">{{ data.code ?? '–' }}</span>
            <span class="small">{{ codeLabel(data.code) }}</span>
          </span>
        </template>
      </Column>
      <Column header="Rules" style="width: 7rem">
        <template #body="{ data }">
          <span v-if="data.failed" class="tone-fail small" :title="`${data.failed} failing`">✗ {{ data.failed }}</span>
          <span v-if="data.warnings" class="tone-warn small" :title="`${data.warnings} warning`">
            ! {{ data.warnings }}</span
          >
        </template>
      </Column>
      <Column header="Status" style="width: 7rem">
        <template #body="{ data }">
          <span v-if="data.status" class="status ellipsis" :class="`tone-${statusTone(data)}`">{{ data.status }}</span>
        </template>
      </Column>
      <Column header="Message">
        <template #body="{ data }">
          <div class="ellipsis" :title="data.message ?? data.error ?? undefined">
            <span v-if="!data.valid" class="tone-fail">{{ data.error ?? "couldn't be read" }}</span>
            <template v-else>{{ data.message }}</template>
          </div>
        </template>
      </Column>
      <Column header="Size" style="width: 6rem">
        <template #body="{ data }"
          ><span class="small">{{ formatBytes(data.bytes) }}</span></template
        >
      </Column>
      <Column header="" style="width: 6rem">
        <template #body="{ data }">
          <div class="actions">
            <Button
              v-tooltip.top="'Compare with the previous'"
              icon="pi pi-arrow-right-arrow-left"
              text
              rounded
              size="small"
              severity="secondary"
              :aria-label="`Compare the report of ${formatAbsolute(data.receivedAt)} with the previous`"
              @click.stop="comparing = data.id"
            />
            <Button
              v-if="canWrite"
              v-tooltip.top="'Delete'"
              icon="pi pi-trash"
              text
              rounded
              size="small"
              severity="danger"
              :aria-label="`Delete the report of ${formatAbsolute(data.receivedAt)}`"
              @click.stop="remove(data)"
            />
          </div>
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="mobile-row">
          <div class="mobile-line">
            <RelativeTime :value="item.receivedAt" />
            <span v-if="!item.evaluated" class="muted small">judging…</span>
            <span v-else class="code" :class="`tone-${codeTone(item.code)}`">{{ item.code ?? '–' }}</span>
            <span v-if="item.status" class="status" :class="`tone-${statusTone(item)}`">{{ item.status }}</span>
            <span v-if="item.repeats" class="muted small">{{ repeatText(item, now) }}</span>
          </div>
          <div class="mobile-line">
            <span class="ellipsis muted small">{{
              item.valid ? item.message : (item.error ?? "couldn't be read")
            }}</span>
            <span class="actions">
              <Button
                icon="pi pi-arrow-right-arrow-left"
                text
                rounded
                size="small"
                severity="secondary"
                aria-label="Compare with the previous"
                @click.stop="comparing = item.id"
              />
              <Button
                v-if="canWrite"
                icon="pi pi-trash"
                text
                rounded
                size="small"
                severity="danger"
                aria-label="Delete the report"
                @click.stop="remove(item)"
              />
            </span>
          </div>
        </div>
      </template>
    </ListTable>
    <ReportCompareDialog v-model:report-id="comparing" :sensor-id="id" />
  </section>
</template>

<style scoped>
.history {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.title-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2) var(--pnmt-space-3);
}

.title {
  font-size: 1.071rem;
  margin: 0;
  flex: 1;
}

.count {
  font-weight: 400;
}

.small {
  font-size: 0.857rem;
}

.shown-tag {
  margin-left: var(--pnmt-space-1);
  font-size: 0.714rem;
  font-weight: 600;
  text-transform: uppercase;
  color: var(--p-primary-color);
}

.verdict {
  display: inline-flex;
  align-items: baseline;
  gap: var(--pnmt-space-1);
}

.code {
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  font-weight: 600;
  padding: 0.05rem 0.35rem;
  border-radius: var(--p-border-radius-sm);
  background: var(--pnmt-none-soft);
}

.code.tone-ok {
  background: var(--pnmt-ok-soft);
}

.code.tone-warn {
  background: var(--pnmt-warn-soft);
}

.code.tone-fail {
  background: var(--pnmt-fail-soft);
}

.status {
  display: inline-block;
  max-width: 100%;
  font-weight: 600;
  font-size: 0.857rem;
}

.judging {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.actions {
  display: flex;
  justify-content: flex-end;
  flex: none;
}

.mobile-row {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.mobile-line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.mobile-line .ellipsis {
  flex: 1;
}

.tone-ok {
  color: var(--pnmt-ok);
}

.tone-warn {
  color: var(--pnmt-warn);
}

.tone-fail {
  color: var(--pnmt-fail);
}

.tone-muted {
  color: var(--pnmt-muted);
}

:deep(tr.row-shown) > td {
  background: color-mix(in srgb, var(--p-primary-color) 8%, transparent);
}

:deep(li.row-shown) {
  background: color-mix(in srgb, var(--p-primary-color) 8%, transparent);
}
</style>
