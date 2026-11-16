<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Column from 'primevue/column'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref } from 'vue'
import { useRoute } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import TabNav from '@/components/TabNav.vue'
import { useActions } from '@/composables/useActions'
import { typeIcon } from '@/composables/useLookups'
import { useServerTable } from '@/composables/useServerTable'
import { useSession } from '@/stores/session'
import { HOST, importanceOptions, importanceSeverity, type Importance } from './importance'
import PushoverTab from './PushoverTab.vue'

/**
 * The Notification Manager (docs/ai-operations-plan.md §11.3): which rule notifies about which sensor. A rule covers
 * a sensor through a link, or through a scope (an importance, a host, everything), unless it is excluded. Viewers
 * read; Users click cells and assign in bulk. Coverage lists the sensors no enabled rule reaches. Pushover is the
 * priority each importance gets on the phone (docs/cookie-object-notes-plan.md §5.4).
 */
const session = useSession()
const canWrite = computed(() => session.can('Monitoring', 'Edit'))
const route = useRoute()
const queryClient = useQueryClient()
const tab = computed(() =>
  route.query.tab === 'coverage' ? 'coverage' : route.query.tab === 'pushover' ? 'pushover' : 'matrix',
)
const tabs = [
  { key: 'matrix', label: 'Matrix', to: { name: 'notifications' } },
  { key: 'coverage', label: 'Coverage', to: { name: 'notifications', query: { tab: 'coverage' } } },
  { key: 'pushover', label: 'Pushover', to: { name: 'notifications', query: { tab: 'pushover' } } },
]

// --- the matrix -----------------------------------------------------------------------------------
const importance = ref<Importance | ''>('')
const uncovered = ref(false)
const filters = computed(() => ({
  importance: importance.value || undefined,
  uncovered: uncovered.value || undefined,
}))
const rules = ref<S['NotificationRuleItem'][]>([])
/** A matrix row with the id ListTable keys rows by. */
type Row = S['NotificationMatrixRow'] & { id: string }
const table = useServerTable<Row>({
  key: [...keys.notifications, 'matrix'],
  fetch: async (q) => {
    const matrix = await api.notifications.matrix(q)
    rules.value = matrix.rules
    return {
      items: matrix.rows.map((r) => ({ ...r, id: r.sensorId })),
      page: matrix.page,
      pageSize: matrix.pageSize,
      totalCount: matrix.totalCount,
    }
  },
  filters,
  pageSize: 100,
})
const importanceChoices = [{ label: 'Any importance', value: '' }, ...importanceOptions]

const cellOf = (row: Row, ruleId: string) => row.cells.find((c) => c.ruleId === ruleId)
const cellIcon = (via?: string) =>
  via === 'link' ? 'pi pi-check' : via === 'scope' ? 'pi pi-circle-fill' : via === 'excluded' ? 'pi pi-ban' : ''
const cellTitle = (cell?: S['NotificationMatrixCell']) =>
  !cell
    ? 'Not covered: click to link'
    : cell.via === 'link'
      ? 'Linked: click to unlink'
      : cell.via === 'scope'
        ? `Through ${cell.scope}: click to exclude`
        : `Excluded from ${cell.scope}: click to include again`

const { run, busy } = useActions()

async function refresh() {
  await queryClient.invalidateQueries({ queryKey: [...keys.notifications] })
  await queryClient.invalidateQueries({ queryKey: [...keys.rules] })
}

async function toggle(row: Row, rule: S['NotificationRuleItem']) {
  if (!canWrite.value) return
  const cell = cellOf(row, rule.id)
  const done = await run(
    async () => {
      if (!cell) await api.sensors.link(row.sensorId, rule.id)
      else if (cell.via === 'link') await api.sensors.unlink(row.sensorId, rule.id)
      else if (cell.via === 'scope') await api.notifications.exclude(rule.id, row.sensorId)
      else await api.notifications.include(rule.id, row.sensorId)
      return true
    },
    { failure: 'Not changed' },
  )
  if (done) await refresh()
}

// --- bulk -------------------------------------------------------------------------------------------
const selected = ref(new Set<string>())
const onPage = computed(() => table.items.value.map((r) => r.sensorId))
const allOnPage = computed(() => onPage.value.length > 0 && onPage.value.every((id) => selected.value.has(id)))
function choose(id: string, on: boolean) {
  const next = new Set(selected.value)
  if (on) next.add(id)
  else next.delete(id)
  selected.value = next
}
function chooseAll(on: boolean) {
  const next = new Set(selected.value)
  for (const id of onPage.value) {
    if (on) next.add(id)
    else next.delete(id)
  }
  selected.value = next
}

const assigning = ref<'Add' | 'Remove' | null>(null)
const chosenRules = ref<string[]>([])
async function assign() {
  const result = await run(
    () =>
      api.notifications.assign({
        ruleIds: chosenRules.value,
        sensorIds: [...selected.value],
        mode: assigning.value ?? 'Add',
      }),
    { failure: 'Not assigned' },
  )
  if (!result) return
  assigning.value = null
  chosenRules.value = []
  await refresh()
}

const settingImportance = ref(false)
const newImportance = ref<string>(HOST)
async function setImportance() {
  const done = await run(
    () =>
      api.notifications.setSensorImportance({
        ids: [...selected.value],
        importance: newImportance.value === HOST ? null : (newImportance.value as Importance),
      }),
    { failure: 'Not set' },
  )
  if (done === undefined) return
  settingImportance.value = false
  await refresh()
  await queryClient.invalidateQueries({ queryKey: [...keys.sensors] })
}

// --- coverage ---------------------------------------------------------------------------------------
// Read for the Coverage tab, and for the matrix's column heads: a rule that reaches nobody right now (docs/notification-recipients-plan.md §9).
const coverage = useQuery({
  queryKey: [...keys.notifications, 'coverage'],
  queryFn: api.notifications.coverage,
  enabled: computed(() => tab.value !== 'pushover'),
})
const reachesNobody = (ruleId: string) =>
  (coverage.data.value?.rulesReachingNobody ?? []).find((r) => r.rule.id === ruleId)?.why
</script>

<template>
  <div class="page">
    <PageHeader title="Notifications" />
    <TabNav :items="tabs" :active="tab" label="Notification manager" />

    <template v-if="tab === 'matrix'">
      <p class="muted intro">
        A rule notifies about a sensor when it is <i class="pi pi-check" aria-hidden="true" /> linked, or
        <i class="pi pi-circle-fill small-dot" aria-hidden="true" /> covered by one of its scopes (an importance, a
        host, everything), unless the sensor is <i class="pi pi-ban" aria-hidden="true" /> excluded. Scopes cover
        sensors added later too; edit them on the rule's page.
      </p>
      <FilterBar
        v-model:search="table.search.value"
        placeholder="Search sensor or host"
        :active="(importance ? 1 : 0) + (uncovered ? 1 : 0)"
      >
        <Select
          v-model="importance"
          :options="importanceChoices"
          option-label="label"
          option-value="value"
          size="small"
          aria-label="Importance"
        />
        <label class="switch"
          ><ToggleSwitch v-model="uncovered" input-id="uncovered" /><span>Not covered by any rule</span></label
        >
      </FilterBar>
      <div v-if="canWrite && selected.size" class="bulk">
        <span>{{ selected.size }} sensor(s)</span>
        <Button label="Assign rules…" icon="pi pi-plus" size="small" @click="assigning = 'Add'" />
        <Button
          label="Remove rules…"
          icon="pi pi-minus"
          size="small"
          severity="secondary"
          @click="assigning = 'Remove'"
        />
        <Button
          label="Set importance…"
          icon="pi pi-flag"
          size="small"
          severity="secondary"
          @click="settingImportance = true"
        />
        <Button label="Clear" size="small" text @click="selected = new Set()" />
      </div>
      <div class="matrix">
        <ListTable :table="table" what="sensors" empty-text="No sensor matches.">
          <Column v-if="canWrite" style="width: 2.5rem">
            <template #header>
              <Checkbox
                :model-value="allOnPage"
                binary
                aria-label="Choose every sensor on this page"
                @update:model-value="(v: boolean) => chooseAll(v)"
              />
            </template>
            <template #body="{ data }">
              <Checkbox
                :model-value="selected.has(data.sensorId)"
                binary
                :aria-label="`Choose ${data.sensorName}`"
                @update:model-value="(v: boolean) => choose(data.sensorId, v)"
              />
            </template>
          </Column>
          <Column header="Sensor" style="min-width: 14rem">
            <template #body="{ data }">
              <span class="line">
                <StatusBadge :status="data.state" icon-only />
                <i :class="typeIcon(data.type)" class="muted" aria-hidden="true" />
                <RouterLink :to="{ name: 'sensor', params: { id: data.sensorId } }">{{ data.sensorName }}</RouterLink>
              </span>
              <RouterLink :to="{ name: 'host', params: { id: data.host.id } }" class="muted small">{{
                data.host.name
              }}</RouterLink>
            </template>
          </Column>
          <Column header="Importance" style="width: 7rem">
            <template #body="{ data }">
              <Tag
                :value="data.effectiveImportance"
                :severity="importanceSeverity(data.effectiveImportance)"
                :title="data.importance ? 'Its own' : 'Its host\'s'"
              />
            </template>
          </Column>
          <Column v-for="rule in rules" :key="rule.id" style="width: 5.5rem" class="cell-column">
            <template #header>
              <RouterLink
                :to="{ name: 'notification-rule', params: { id: rule.id } }"
                class="rule-head"
                :title="`${rule.name} · ${rule.provider}${reachesNobody(rule.id) ? ` · reaches nobody: ${reachesNobody(rule.id)}` : ''}`"
                ><i v-if="reachesNobody(rule.id)" class="pi pi-exclamation-triangle nobody" aria-hidden="true" />{{
                  rule.name
                }}</RouterLink
              >
            </template>
            <template #body="{ data }">
              <button
                type="button"
                class="cell"
                :class="cellOf(data, rule.id)?.via ?? 'none'"
                :disabled="!canWrite || busy"
                :title="cellTitle(cellOf(data, rule.id))"
                :aria-label="`${rule.name} for ${data.sensorName}: ${cellTitle(cellOf(data, rule.id))}`"
                @click="toggle(data, rule)"
              >
                <i :class="cellIcon(cellOf(data, rule.id)?.via)" aria-hidden="true" />
              </button>
            </template>
          </Column>
          <template #mobile="{ item }">
            <div class="line">
              <StatusBadge :status="item.state" icon-only />
              <RouterLink :to="{ name: 'sensor', params: { id: item.sensorId } }">{{ item.sensorName }}</RouterLink>
              <Tag :value="item.effectiveImportance" :severity="importanceSeverity(item.effectiveImportance)" />
            </div>
            <div class="muted small">
              {{ item.host.name }} · {{ item.cells.filter((c) => c.via !== 'excluded').length }} rule(s)
            </div>
          </template>
        </ListTable>
      </div>
    </template>

    <PushoverTab v-else-if="tab === 'pushover'" :can-write="canWrite" />

    <template v-else>
      <section v-if="coverage.data.value" class="panel" aria-labelledby="coverage-title">
        <div id="coverage-title" class="panel-header">Sensors no rule covers</div>
        <div class="panel-body">
          <p>
            {{ coverage.data.value.covered }} of {{ coverage.data.value.total }} sensors are covered by an enabled rule.
            Not covered: {{ coverage.data.value.uncoveredHigh }} High, {{ coverage.data.value.uncoveredNormal }} Normal,
            {{ coverage.data.value.uncoveredLow }} Low.
          </p>
          <Message v-if="coverage.data.value.uncoveredHigh" severity="warn" :closable="false" size="small">
            {{ coverage.data.value.uncoveredHigh }} High sensor(s) would fail without anyone being told. A scope
            "importance at least High" on a rule covers them, and every High sensor added later.
          </Message>
          <Message v-if="coverage.data.value.rulesReachingNobody.length" severity="warn" :closable="false" size="small">
            {{ coverage.data.value.rulesReachingNobody.length }} enabled rule(s) reach nobody right now, so their
            sensors count as uncovered:
            <ul class="nobody-list">
              <li v-for="r in coverage.data.value.rulesReachingNobody" :key="r.rule.id">
                <RouterLink :to="{ name: 'notification-rule', params: { id: r.rule.id } }">{{
                  r.rule.name
                }}</RouterLink>
                <span class="muted">{{ r.why }}</span>
              </li>
            </ul>
          </Message>
          <ul class="uncovered">
            <li v-for="s in coverage.data.value.sensors" :key="s.sensorId">
              <Tag :value="s.effectiveImportance" :severity="importanceSeverity(s.effectiveImportance)" />
              <RouterLink :to="{ name: 'sensor', params: { id: s.sensorId } }">{{ s.sensorName }}</RouterLink>
              <span class="muted small">{{ s.host.name }}</span>
            </li>
          </ul>
        </div>
      </section>
    </template>

    <Dialog
      :visible="assigning !== null"
      :header="assigning === 'Remove' ? 'Remove rules' : 'Assign rules'"
      modal
      :style="{ width: '32rem' }"
      @update:visible="(v: boolean) => !v && (assigning = null)"
    >
      <p class="muted">
        <template v-if="assigning === 'Remove'"
          >The rules are unlinked from the {{ selected.size }} sensor(s); where one of their scopes still covers a
          sensor, it is excluded.</template
        >
        <template v-else
          >The rules are linked to the {{ selected.size }} sensor(s), and earlier exclusions are lifted.</template
        >
      </p>
      <MultiSelect
        v-model="chosenRules"
        :options="rules"
        option-label="name"
        option-value="id"
        placeholder="Rules"
        display="chip"
        class="full"
        filter
      />
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="assigning = null" />
        <Button
          :label="assigning === 'Remove' ? 'Remove' : 'Assign'"
          :loading="busy"
          :disabled="!chosenRules.length"
          @click="assign"
        />
      </template>
    </Dialog>

    <Dialog v-model:visible="settingImportance" header="Set importance" modal :style="{ width: '28rem' }">
      <p class="muted">For the {{ selected.size }} sensor(s): their own importance, or their host's.</p>
      <Select
        v-model="newImportance"
        :options="[{ label: 'As their host', value: HOST }, ...importanceOptions]"
        option-label="label"
        option-value="value"
        class="full"
      />
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="settingImportance = false" />
        <Button label="Set" :loading="busy" @click="setImportance" />
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
.intro {
  margin: 0 0 var(--pnmt-space-3);
}
.switch {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
.bulk {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
  margin-bottom: var(--pnmt-space-3);
}
.matrix {
  overflow-x: auto;
}
.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
.rule-head {
  display: inline-block;
  max-width: 5rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 0.857rem;
}
.cell {
  width: 2rem;
  height: 2rem;
  border: 1px solid var(--pnmt-border, transparent);
  border-radius: 6px;
  background: transparent;
  cursor: pointer;
  color: var(--pnmt-muted);
}
.cell:disabled {
  cursor: default;
}
.cell.link {
  color: var(--pnmt-ok, green);
}
.cell.scope {
  color: var(--pnmt-accent, currentColor);
}
.cell.scope i {
  font-size: 0.6rem;
}
.cell.excluded {
  color: var(--pnmt-danger, red);
}
.small-dot {
  font-size: 0.6rem;
}
.uncovered {
  list-style: none;
  margin: var(--pnmt-space-3) 0 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}
.uncovered li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
.nobody {
  color: var(--pnmt-warn);
  margin-right: var(--pnmt-space-1);
  font-size: 0.75rem;
}
.nobody-list {
  margin: var(--pnmt-space-2) 0 0;
  padding-left: var(--pnmt-space-4);
}
.nobody-list li {
  display: flex;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}
.small {
  font-size: 0.857rem;
}
.full {
  width: 100%;
}
</style>
