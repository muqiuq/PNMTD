<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed } from 'vue'
import type { BoardCatalog } from '@/api/boards'
import { metricNames } from '@/boards/names'
import type { BoardBar, BoardHeader, BoardMetric, BoardRule } from '@/boards/types'
import { severityOptions } from '@/shared/zabbix'

/**
 * The status bar (docs/boards-plan.md §8): shown or not, where and how large, its green text, the header line's title and clock, and the
 * rules. The worst matching rule colours the bar; grey when a source can't be read and nothing worse matched.
 */
const props = defineProps<{ catalog: BoardCatalog | null; manage: boolean }>()
const bar = defineModel<BoardBar>('bar', { required: true })
const header = defineModel<BoardHeader>('header', { required: true })

const metrics = computed(() => props.catalog?.metrics ?? [])
const available = (m: BoardMetric) => metrics.value.find((x) => x.metric === m)?.available ?? true
const filtersOf = (m: BoardMetric) => metrics.value.find((x) => x.metric === m)?.filters ?? ''
const colours = [
  { value: 'Red', label: 'red' },
  { value: 'Yellow', label: 'yellow' },
]
const importances = [
  { value: '', label: 'any importance' },
  { value: 'Normal', label: 'Normal and High' },
  { value: 'High', label: 'High only' },
]
// PrimeVue's Select shows nothing for a null value: '' and 0 stand for "none" and become null again on the way out.
const locations = computed(() => [{ id: '', path: 'everywhere' }, ...(props.catalog?.locations ?? [])])
const projects = computed(() => [{ id: 0, name: 'every project' }, ...(props.catalog?.redmineProjects ?? [])])

function setRule(i: number, rule: BoardRule) {
  bar.value = { ...bar.value, rules: bar.value.rules.map((r, j) => (j === i ? rule : r)) }
}
function moveRule(i: number, delta: number) {
  const rules = [...bar.value.rules]
  const [rule] = rules.splice(i, 1)
  rules.splice(Math.max(0, Math.min(rules.length, i + delta)), 0, rule!)
  bar.value = { ...bar.value, rules }
}
function addRule() {
  const metric = metrics.value.find((m) => m.available)?.metric ?? 'SensorsFailing'
  bar.value = {
    ...bar.value,
    rules: [
      ...bar.value.rules,
      { metric, colour: 'Yellow', atLeast: 1, label: null, filter: {} as BoardRule['filter'] },
    ],
  }
}
const filter = (r: BoardRule) => r.filter ?? ({ suppressed: false } as NonNullable<BoardRule['filter']>)
const setFilter = (i: number, r: BoardRule, patch: Partial<NonNullable<BoardRule['filter']>>) =>
  setRule(i, { ...r, filter: { ...filter(r), ...patch } })
</script>

<template>
  <fieldset :disabled="!manage" class="form bar-tab">
    <label class="switch"><ToggleSwitch v-model="bar.show" /> Show the status bar</label>
    <template v-if="bar.show">
      <div class="field two">
        <div>
          <label>Where</label>
          <SelectButton v-model="bar.position" :options="['Top', 'Bottom']" :allow-empty="false" />
        </div>
        <div>
          <label>Size</label>
          <SelectButton v-model="bar.size" :options="['Small', 'Medium', 'Large']" :allow-empty="false" />
        </div>
      </div>
      <div class="field">
        <label for="green-text">Green text</label>
        <InputText
          id="green-text"
          :model-value="bar.greenText ?? ''"
          placeholder="All clear"
          @update:model-value="(v) => (bar = { ...bar, greenText: v?.trim() ? v : null })"
        />
      </div>
      <label class="check"><Checkbox v-model="bar.flash" binary /> Flash for ten seconds on turning red</label>
    </template>
    <div class="field">
      <label>{{ bar.show ? 'In the bar' : 'The header line' }}</label>
      <label class="check"><Checkbox v-model="header.showTitle" binary /> The title</label>
      <InputText
        v-if="header.showTitle"
        :model-value="header.title ?? ''"
        placeholder="The board's name"
        aria-label="Title"
        @update:model-value="(v) => (header = { ...header, title: v?.trim() ? v : null })"
      />
      <label class="check"><Checkbox v-model="header.clock" binary /> The clock</label>
      <div v-if="header.clock" class="indent">
        <label class="check"><Checkbox v-model="header.seconds" binary /> Seconds</label>
        <label class="check"><Checkbox v-model="header.date" binary /> The date</label>
      </div>
    </div>

    <template v-if="bar.show">
      <h3>Rules</h3>
      <p class="muted hint">
        The worst matching rule colours the bar. Grey: a source can't be read, and nothing worse matched.
      </p>
      <div v-for="(r, i) in bar.rules" :key="i" class="rule" :data-test="`rule-${i}`">
        <div class="line">
          <Select
            :model-value="r.metric"
            :options="metrics"
            option-label="name"
            option-value="metric"
            :option-disabled="(m) => !m.available"
            class="metric"
            :aria-label="`Measure of rule ${i + 1}`"
            @update:model-value="(m) => setRule(i, { ...r, metric: m, filter: {} as BoardRule['filter'] })"
          />
          <Button
            icon="pi pi-arrow-up"
            text
            size="small"
            severity="secondary"
            :disabled="i === 0"
            aria-label="Up"
            @click="moveRule(i, -1)"
          />
          <Button
            icon="pi pi-arrow-down"
            text
            size="small"
            severity="secondary"
            :disabled="i === bar.rules.length - 1"
            aria-label="Down"
            @click="moveRule(i, 1)"
          />
          <Button
            icon="pi pi-times"
            text
            size="small"
            severity="secondary"
            aria-label="Remove"
            @click="bar = { ...bar, rules: bar.rules.filter((_, j) => j !== i) }"
          />
        </div>
        <small v-if="!available(r.metric)" class="locked"
          ><i class="pi pi-lock" aria-hidden="true" /> {{ metrics.find((m) => m.metric === r.metric)?.reason }}</small
        >
        <div class="line">
          <template v-if="filtersOf(r.metric).includes('importance')">
            <Select
              :model-value="filter(r).importance ?? ''"
              :options="importances"
              option-label="label"
              option-value="value"
              size="small"
              @update:model-value="(v) => setFilter(i, r, { importance: v || null })"
            />
            <Select
              :model-value="filter(r).locationId ?? ''"
              :options="locations"
              option-label="path"
              option-value="id"
              filter
              size="small"
              @update:model-value="(v) => setFilter(i, r, { locationId: v || null })"
            />
          </template>
          <template v-if="filtersOf(r.metric).includes('severity')">
            <Select
              :model-value="filter(r).severity ?? 'Information'"
              :options="severityOptions"
              option-label="label"
              option-value="value"
              size="small"
              @update:model-value="(v) => setFilter(i, r, { severity: v })"
            />
            <MultiSelect
              :model-value="filter(r).groups ?? []"
              :options="catalog?.zabbixGroups ?? []"
              placeholder="every group"
              size="small"
              @update:model-value="(v) => setFilter(i, r, { groups: v.length ? v : null })"
            />
          </template>
          <Select
            v-if="filtersOf(r.metric).includes('project')"
            :model-value="filter(r).projectId ?? 0"
            :options="projects"
            option-label="name"
            option-value="id"
            size="small"
            @update:model-value="(v) => setFilter(i, r, { projectId: v || null })"
          />
          <InputNumber
            v-if="filtersOf(r.metric).includes('days')"
            :model-value="filter(r).days ?? 30"
            :min="1"
            :max="90"
            suffix=" days"
            size="small"
            @update:model-value="(v) => setFilter(i, r, { days: v })"
          />
        </div>
        <div class="line">
          <span class="muted">at least</span>
          <InputNumber
            :model-value="r.atLeast"
            :min="1"
            :max="9999"
            size="small"
            class="count"
            @update:model-value="(v) => setRule(i, { ...r, atLeast: v ?? 1 })"
          />
          <span class="muted">makes it</span>
          <Select
            :model-value="r.colour"
            :options="colours"
            option-label="label"
            option-value="value"
            size="small"
            @update:model-value="(v) => setRule(i, { ...r, colour: v })"
          />
        </div>
        <InputText
          :model-value="r.label ?? ''"
          :placeholder="`Label: ${metricNames[r.metric].toLowerCase()}`"
          size="small"
          @update:model-value="(v) => setRule(i, { ...r, label: v?.trim() ? v : null })"
        />
      </div>
      <Button
        label="Add a rule"
        icon="pi pi-plus"
        text
        size="small"
        :disabled="bar.rules.length >= 20"
        @click="addRule"
      />
    </template>
  </fieldset>
</template>

<style scoped>
.bar-tab {
  border: none;
  margin: 0;
  padding: 0;
  min-width: 0;
}

.switch,
.check {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.two {
  display: grid;
  grid-template-columns: auto auto;
  gap: var(--pnmt-space-2);
  justify-content: start;
}

.two > div,
.indent {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.indent {
  padding-left: var(--pnmt-space-4);
}

h3 {
  margin: var(--pnmt-space-2) 0 0;
  font-size: 1rem;
}

.hint {
  margin: 0;
  font-size: 0.85rem;
}

.rule {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: var(--pnmt-space-2);
  border: 1px solid var(--pnmt-border);
  border-radius: 6px;
}

.line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px;
}

.metric {
  flex: 1;
  min-width: 10rem;
}

.count {
  width: 6rem;
}

.locked {
  color: var(--pnmt-warn);
}
</style>
