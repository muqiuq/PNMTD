<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Message from 'primevue/message'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, reactive, ref, watch } from 'vue'
import { api, type S } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import SinceText from '@/components/SinceText.vue'
import { useActions } from '@/composables/useActions'
import { useMedia } from '@/composables/useMedia'
import CopyRulesDialog from './CopyRulesDialog.vue'
import RuleEditor from './RuleEditor.vue'
import RuleOutcome from './RuleOutcome.vue'
import RuleTester from './RuleTester.vue'
import VerdictLine from './VerdictLine.vue'
import { copyName, draftOf, itemRequest } from './rules'
import { useApplyAdvanced } from './useAdvanced'

/**
 * An Advanced sensor's *Rules* tab (docs/advanced-sensor-plan.md §10.3): the verdict in a line, and the rules in their
 * order (drag them, or *Move up* and *Move down*), each with its condition in words, its severity, what the last report
 * made of it with the values (hidden when the data names people and the viewer lacks Details), since when it fails, and
 * whether it is on. With Edit: add, edit, duplicate, switch on and off, delete; copy the rules of another Advanced sensor.
 * For everyone: *Try the rules…* against the latest report, a stored one, or pasted JSON. Every change answers with the
 * sensor's detail once re-judged (§8.3), which goes into the page.
 */
type Rule = S['AdvancedRuleItem']

const props = defineProps<{ sensor: S['SensorDetail']; canWrite: boolean }>()
const { run, ask } = useActions()
const apply = useApplyAdvanced()
const phone = useMedia('(max-width: 599px)')

const id = computed(() => props.sensor.id)
const a = computed(() => props.sensor.advanced!)
// The order as shown, while a change of it is saved.
const rows = ref<Rule[]>([])
watch(
  () => a.value.rules,
  (rules) => (rows.value = [...rules]),
  { immediate: true },
)
const failing = computed(() => rows.value.filter((r) => r.enabled && r.last?.outcome === 'Fail').length)
const warning = computed(() => rows.value.filter((r) => r.enabled && r.last?.outcome === 'Warn').length)

// --- the dialogs ----------------------------------------------------------------------------------------------------
const editorOpen = ref(false)
const editing = ref<Rule | null>(null)
const initial = ref<Partial<S['AdvancedRuleRequest']> | null>(null)
const testing = ref(false)
const copying = ref(false)

function add() {
  editing.value = null
  initial.value = null
  editorOpen.value = true
}
function edit(rule: Rule) {
  editing.value = rule
  initial.value = null
  editorOpen.value = true
}
/** A copy in the editor, saved only when the person saves it. */
function duplicate(rule: Rule) {
  editing.value = null
  initial.value = { ...draftOf(rule), name: copyName(rule.name), note: rule.note }
  editorOpen.value = true
}

// --- changes: each answers with the detail, re-judged ---------------------------------------------------------------
const busy = reactive(new Set<string>())
async function change(
  rule: Rule | null,
  action: () => Promise<S['AdvancedSensorDetail']>,
  messages: { success?: string; failure: string },
) {
  if (rule) busy.add(rule.id)
  try {
    const detail = await run(action, messages)
    if (detail) void apply(id.value, detail)
    return detail
  } finally {
    if (rule) busy.delete(rule.id)
  }
}

const setEnabled = (rule: Rule, enabled: boolean) =>
  change(rule, () => api.advanced.rules.update(id.value, rule.id, itemRequest(rule, { enabled })), {
    failure: enabled ? 'Not switched on' : 'Not switched off',
  })

async function remove(rule: Rule) {
  if (
    await ask(
      `Delete ${rule.name}?`,
      'The rule is deleted, and the latest report is judged again without it. Its changes stay in the History.',
    )
  ) {
    await change(rule, () => api.advanced.rules.remove(id.value, rule.id), {
      success: `${rule.name} deleted`,
      failure: 'Not deleted',
    })
  }
}

async function saveOrder(list: Rule[]) {
  rows.value = list
  const detail = await change(
    null,
    () =>
      api.advanced.rules.order(
        id.value,
        list.map((r) => r.id),
      ),
    {
      failure: 'Order not saved',
    },
  )
  if (!detail) rows.value = [...a.value.rules]
}
const reorder = (event: { value: Rule[] }) => saveOrder(event.value)
function move(rule: Rule, by: -1 | 1) {
  const list = [...rows.value]
  const at = list.findIndex((r) => r.id === rule.id)
  const to = at + by
  if (at < 0 || to < 0 || to >= list.length) return
  list.splice(to, 0, ...list.splice(at, 1))
  void saveOrder(list)
}

const rowMenu = (rule: Rule, index: number) => [
  { label: 'Duplicate…', icon: 'pi pi-clone', command: () => duplicate(rule) },
  {
    label: rule.enabled ? 'Switch off' : 'Switch on',
    icon: rule.enabled ? 'pi pi-pause' : 'pi pi-play',
    command: () => setEnabled(rule, !rule.enabled),
  },
  { label: 'Move up', icon: 'pi pi-arrow-up', disabled: index === 0, command: () => move(rule, -1) },
  {
    label: 'Move down',
    icon: 'pi pi-arrow-down',
    disabled: index === rows.value.length - 1,
    command: () => move(rule, 1),
  },
  { separator: true },
  { label: 'Delete…', icon: 'pi pi-trash', command: () => remove(rule) },
]
const tabMenu = computed(() => [
  { label: 'Copy rules from another sensor…', icon: 'pi pi-copy', command: () => (copying.value = true) },
])
const rowClass = (rule: Rule) => (rule.enabled ? undefined : 'row-muted')
const indexOf = (rule: Rule) => rows.value.findIndex((r) => r.id === rule.id)
</script>

<template>
  <div class="rules-tab">
    <section class="panel summary" aria-label="The rules' verdict">
      <div class="summary-text">
        <template v-if="a.verdict">
          <VerdictLine :code="a.verdict.code" :message="a.verdict.message" />
          <span class="muted small">
            <template v-if="failing || warning">
              <span v-if="failing" class="tone-fail">{{ failing }} fail{{ failing === 1 ? 's' : '' }}</span
              ><template v-if="failing && warning"> · </template
              ><span v-if="warning" class="tone-warn">{{ warning }} warn{{ warning === 1 ? 's' : '' }}</span>
              ·
            </template>
            <template v-if="a.verdict.evaluatedAt">judged <RelativeTime :value="a.verdict.evaluatedAt" /></template>
          </span>
        </template>
        <span v-else class="muted">
          {{ a.lastReportAt ? 'Not judged yet: the evaluator is on it.' : 'Not judged yet: no report so far.' }}
        </span>
        <span v-if="a.pending > 0" class="muted small judging"
          ><i class="pi pi-spin pi-spinner" aria-hidden="true" /> judging {{ a.pending }} report{{
            a.pending === 1 ? '' : 's'
          }}…</span
        >
      </div>
      <div class="actions">
        <Button
          label="Try the rules…"
          icon="pi pi-play"
          size="small"
          severity="secondary"
          outlined
          @click="testing = true"
        />
        <Button v-if="canWrite" label="Add rule" icon="pi pi-plus" size="small" @click="add" />
        <MoreMenu v-if="canWrite" :items="tabMenu" label="More actions for the rules" />
      </div>
    </section>

    <Message v-if="!a.canSeeData" severity="secondary" :closable="false" size="small">
      The data of this sensor names people: the rules' values need Monitoring: Details.
    </Message>

    <div v-if="!rows.length" class="panel">
      <EmptyState icon="pi pi-list-check" title="No rules yet." text="Pick a value on the Data tab, or add one here.">
        <Button v-if="canWrite" label="Add rule" icon="pi pi-plus" size="small" outlined @click="add" />
      </EmptyState>
    </div>

    <ul v-else-if="phone" class="panel mobile" aria-label="Rules">
      <li v-for="(rule, index) in rows" :key="rule.id" :class="rowClass(rule)">
        <div class="mobile-head">
          <span class="name ellipsis" :title="rule.note ?? undefined">{{ rule.name }}</span>
          <span class="muted small">{{ rule.severity === 'Warn' ? 'warns' : 'fails' }}</span>
          <ToggleSwitch
            v-if="canWrite"
            :model-value="rule.enabled"
            :disabled="busy.has(rule.id)"
            :aria-label="`${rule.name} on`"
            @update:model-value="(on: boolean) => setEnabled(rule, on)"
          />
        </div>
        <div class="mono small condition">{{ rule.condition }}</div>
        <RuleOutcome v-if="rule.enabled" :result="rule.last" :values="a.canSeeData" cell />
        <span v-else class="muted small">off</span>
        <div v-if="rule.failingSince" class="small tone-fail">failing for <SinceText :since="rule.failingSince" /></div>
        <div v-if="canWrite" class="mobile-actions">
          <Button
            icon="pi pi-arrow-up"
            text
            size="small"
            severity="secondary"
            :disabled="index === 0"
            :aria-label="`Move ${rule.name} up`"
            @click="move(rule, -1)"
          />
          <Button
            icon="pi pi-arrow-down"
            text
            size="small"
            severity="secondary"
            :disabled="index === rows.length - 1"
            :aria-label="`Move ${rule.name} down`"
            @click="move(rule, 1)"
          />
          <Button
            icon="pi pi-pencil"
            text
            size="small"
            severity="secondary"
            :aria-label="`Edit ${rule.name}`"
            @click="edit(rule)"
          />
          <MoreMenu :items="rowMenu(rule, index)" :label="`More actions for ${rule.name}`" />
        </div>
      </li>
    </ul>

    <DataTable
      v-else
      :value="rows"
      data-key="id"
      size="small"
      class="panel rules-table"
      :row-class="rowClass"
      @row-reorder="reorder"
    >
      <Column
        v-if="canWrite"
        row-reorder
        style="width: 2.5rem"
        :pt="{ rowReorderIcon: { 'aria-label': 'Drag to reorder' } }"
      />
      <Column header="Rule" style="min-width: 10rem">
        <template #body="{ data }">
          <span class="name-cell">
            <button
              v-if="canWrite"
              type="button"
              class="link-button name"
              :title="data.note ?? undefined"
              @click="edit(data)"
            >
              {{ data.name }}
            </button>
            <span v-else class="name" :title="data.note ?? undefined">{{ data.name }}</span>
            <i
              v-if="data.note"
              v-tooltip.top="data.note"
              class="pi pi-info-circle muted"
              tabindex="0"
              :aria-label="`Note: ${data.note}`"
            />
          </span>
        </template>
      </Column>
      <Column header="Condition">
        <template #body="{ data }">
          <span class="mono small condition">{{ data.condition }}</span>
        </template>
      </Column>
      <Column header="Severity" style="width: 6rem">
        <template #body="{ data }">
          <span :class="data.severity === 'Warn' ? 'tone-warn' : undefined">{{
            data.severity === 'Warn' ? 'warn' : 'fail'
          }}</span>
        </template>
      </Column>
      <Column header="With the last report" style="width: 30%">
        <template #body="{ data }">
          <RuleOutcome v-if="data.enabled" :result="data.last" :values="a.canSeeData" cell />
          <span v-else class="muted">off</span>
        </template>
      </Column>
      <Column header="Failing since" style="width: 7rem">
        <template #body="{ data }">
          <SinceText v-if="data.failingSince" :since="data.failingSince" prefix="for" />
        </template>
      </Column>
      <Column header="On" style="width: 4rem">
        <template #body="{ data }">
          <ToggleSwitch
            v-if="canWrite"
            :model-value="data.enabled"
            :disabled="busy.has(data.id)"
            :aria-label="`${data.name} on`"
            @update:model-value="(on: boolean) => setEnabled(data, on)"
          />
          <span v-else :class="data.enabled ? undefined : 'muted'">{{ data.enabled ? 'on' : 'off' }}</span>
        </template>
      </Column>
      <Column v-if="canWrite" style="width: 5.5rem">
        <template #body="{ data }">
          <span class="row-actions">
            <Button
              icon="pi pi-pencil"
              text
              rounded
              size="small"
              severity="secondary"
              :aria-label="`Edit ${data.name}`"
              @click="edit(data)"
            />
            <MoreMenu :items="rowMenu(data, indexOf(data))" :label="`More actions for ${data.name}`" />
          </span>
        </template>
      </Column>
    </DataTable>

    <RuleEditor v-model:visible="editorOpen" :sensor="sensor" :rule="editing" :initial="initial" />
    <RuleTester v-model:visible="testing" :sensor="sensor" />
    <CopyRulesDialog v-if="canWrite" v-model:visible="copying" :sensor="sensor" />
  </div>
</template>

<style scoped>
.rules-tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  min-width: 0;
}

.summary {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2) var(--pnmt-space-4);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
}

.summary-text {
  flex: 1 1 24rem;
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: var(--pnmt-space-1) var(--pnmt-space-3);
  min-width: 0;
}

.actions {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  margin-left: auto;
}

.judging {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.rules-table {
  overflow: hidden;
}

.name-cell {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  min-width: 0;
}

.name {
  font-weight: 500;
}

.link-button {
  all: unset;
  cursor: pointer;
  color: var(--p-primary-color);
}

.link-button:hover {
  text-decoration: underline;
}

.link-button:focus-visible {
  outline: 2px solid var(--p-primary-color);
  outline-offset: 2px;
}

.condition {
  overflow-wrap: anywhere;
}

.row-actions {
  display: inline-flex;
  align-items: center;
}

.small {
  font-size: 0.857rem;
}

.tone-warn {
  color: var(--pnmt-warn);
}

.tone-fail {
  color: var(--pnmt-fail);
}

.mobile {
  list-style: none;
  margin: 0;
  padding: 0;
}

.mobile li {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
}

.mobile li:last-child {
  border-bottom: 0;
}

.mobile li.row-muted {
  opacity: 0.6;
}

.mobile-head {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.mobile-head .name {
  flex: 1;
}

.mobile-actions {
  display: flex;
  justify-content: flex-end;
  align-items: center;
}
</style>
