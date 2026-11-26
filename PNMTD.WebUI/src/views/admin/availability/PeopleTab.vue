<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import { useNotify } from '@/composables/useNotify'
import CodeChip from './CodeChip.vue'
import PlanFields from './PlanFields.vue'
import { chipStyle, fullWeek, planDraftOf, planRequest, targetShare, weekdayShort, type PlanDraft } from './plan'
import { useAvailabilityPeople, useTypeOptions } from './useAvailability'

/**
 * The plan's people (docs/cookie-plan.md §8.1, §9.4), for Availability: Manage: who takes part and in which order, with
 * their short code, colour and default week. Everyone active is listed, those in the plan first, in the plan's order.
 * The contact details are the person's own (Account) and an admin's (Admin → Users).
 */
type Person = S['AvailabilityPeopleItem']
const queryClient = useQueryClient()
const notify = useNotify()
const people = useAvailabilityPeople()
const { types, settings } = useTypeOptions()
/** What the settings call the responsible (docs/availability-extensions-plan.md §7). */
const label = computed(() => settings.data.value?.settings.responsibleLabel || 'Responsible')
const typesById = computed(() => new Map(types.value.map((t) => [t.id, t])))
const list = computed(() => people.data.value ?? [])
const inPlan = computed(() => list.value.filter((p) => p.inPlan))
const name = (p: Person) => (p.displayName && p.displayName !== p.username ? p.displayName : p.username)
/** The share as entered and the target it gives among everyone in the plan (§8): "100 % → 25 %". */
const shareText = (p: Person) =>
  `${p.responsibleShare} % → ${Math.round(
    targetShare(
      p.responsibleShare,
      inPlan.value.map((x) => x.responsibleShare),
    ) * 100,
  )} %`

/** A weekday as the week's chip shows it: the type's colour, or grey when the person is off. */
function weekday(d: S['DefaultDay']) {
  const standard = types.value.find((t) => t.isDefault)
  const type = d.works ? (d.typeId ? typesById.value.get(d.typeId) : standard) : types.value.find((t) => t.isDayOff)
  return {
    label: weekdayShort(d.day),
    style: d.works ? chipStyle(type?.color) : {},
    title: `${d.day}: ${d.works ? (type?.code ?? 'works') : 'off'}`,
    off: !d.works,
  }
}

async function refresh() {
  await queryClient.invalidateQueries({ queryKey: keys.availability })
}

// --- editing a person -----------------------------------------------------------------------------------------------
const editing = ref<Person | null>(null)
const draft = ref<PlanDraft | null>(null)
const errors = ref<Record<string, string | undefined>>({})
const busy = ref(false)
const open = computed({ get: () => editing.value !== null, set: (v) => !v && (editing.value = null) })
function edit(p: Person) {
  editing.value = p
  draft.value = planDraftOf(p)
  // Someone new to the plan gets the last column.
  if (!p.inPlan && !p.shortCode) draft.value.planOrder = nextOrder()
  errors.value = {}
}
const nextOrder = () => Math.max(-10, ...inPlan.value.map((p) => p.planOrder)) + 10

async function save() {
  if (!editing.value || !draft.value) return
  busy.value = true
  errors.value = {}
  try {
    const saved = await api.availability.savePerson(editing.value.id, planRequest(draft.value))
    notify.success(
      `${name(editing.value)} saved`,
      saved.inPlan ? `In the plan as ${saved.shortCode}.` : 'Not in the plan.',
    )
    editing.value = null
    await refresh()
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = Object.fromEntries(Object.entries(e.errors).map(([k, v]) => [k, v[0]]))
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    busy.value = false
  }
}

/** Up or down a column: the people in the plan are numbered again, 0, 10, 20 …, and those that moved are saved. */
async function move(p: Person, by: -1 | 1) {
  const order = [...inPlan.value]
  const i = order.findIndex((x) => x.id === p.id)
  const j = i + by
  if (i < 0 || j < 0 || j >= order.length) return
  ;[order[i], order[j]] = [order[j], order[i]]
  busy.value = true
  try {
    for (const [k, person] of order.entries()) {
      if (person.planOrder !== k * 10)
        await api.availability.savePerson(person.id, planRequest({ ...planDraftOf(person), planOrder: k * 10 }))
    }
  } catch (e) {
    notify.error(e, 'Not moved')
  } finally {
    busy.value = false
    await refresh()
  }
}
</script>

<template>
  <ErrorState
    v-if="people.isError.value && !people.data.value"
    :error="people.error.value"
    what="the people"
    @retry="people.refetch()"
  />
  <section v-else class="panel" aria-labelledby="people-title">
    <div id="people-title" class="panel-header">
      People
      <span class="muted normal small">{{ inPlan.length }} in the plan</span>
    </div>
    <div class="panel-body body">
      <p class="muted small intro">
        Everyone in the plan has a column and edits it from today on. The short code and the colour show them in the
        plan, as responsible and fallback too; the default week is what the fill writes. E-mail and Pushover are each
        person's own, on Account; the Redmine user is set by admins under Admin → Users.
      </p>
      <DataTable :value="list" :loading="people.isLoading.value" size="small" data-key="id" class="table">
        <Column header="Column" style="width: 6.5rem">
          <template #body="{ data, index }">
            <span v-if="data.inPlan" class="order">
              <Button
                icon="pi pi-arrow-up"
                size="small"
                severity="secondary"
                text
                :disabled="busy || index === 0"
                :aria-label="`Move ${name(data)} up, a column to the left`"
                @click="move(data, -1)"
              />
              <Button
                icon="pi pi-arrow-down"
                size="small"
                severity="secondary"
                text
                :disabled="busy || index === inPlan.length - 1"
                :aria-label="`Move ${name(data)} down, a column to the right`"
                @click="move(data, 1)"
              />
            </span>
            <span v-else class="muted">–</span>
          </template>
        </Column>
        <Column header="Person">
          <template #body="{ data }">
            <span class="person">
              <span>{{ name(data) }}</span>
              <span class="muted mono small">{{ data.username }}</span>
            </span>
          </template>
        </Column>
        <Column header="Code" style="width: 6rem">
          <template #body="{ data }">
            <CodeChip v-if="data.shortCode" :code="data.shortCode" :colour="data.color" />
            <span v-else class="muted">–</span>
          </template>
        </Column>
        <Column header="Plan" style="width: 7rem">
          <template #body="{ data }">
            <Tag v-if="data.inPlan" value="in the plan" severity="info" />
            <span v-else class="muted">not in it</span>
          </template>
        </Column>
        <Column style="width: 8rem">
          <template #header>
            <span :title="`The share of the days as ${label}, and the target it gives among everyone in the plan`"
              >Share</span
            >
          </template>
          <template #body="{ data }">
            <span v-if="data.inPlan" class="mono small">{{ shareText(data) }}</span>
            <span v-else class="muted">–</span>
          </template>
        </Column>
        <Column header="Default week" style="width: 17rem">
          <template #body="{ data }">
            <span class="week">
              <span
                v-for="d in fullWeek(data.defaultWeek)"
                :key="d.day"
                class="weekday"
                :class="{ off: weekday(d).off }"
                :style="weekday(d).style"
                :title="weekday(d).title"
                >{{ weekday(d).label }}</span
              >
            </span>
          </template>
        </Column>
        <Column style="width: 4rem">
          <template #body="{ data }">
            <Button
              icon="pi pi-pencil"
              size="small"
              severity="secondary"
              text
              :aria-label="`Edit ${name(data)}`"
              @click="edit(data)"
            />
          </template>
        </Column>
      </DataTable>
    </div>
  </section>

  <Dialog v-model:visible="open" :header="editing ? name(editing) : ''" modal :style="{ width: 'min(40rem, 95vw)' }">
    <form v-if="draft" id="person-form" class="form" novalidate @submit.prevent="save">
      <PlanFields v-model="draft" :types="types" :errors="errors" id-prefix="person" :label="label" />
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="editing = null" />
      <Button type="submit" form="person-form" label="Save" :loading="busy" />
    </template>
  </Dialog>
</template>

<style scoped>
.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.intro {
  margin: 0;
  max-width: 80ch;
}

.normal {
  font-weight: 400;
}

.table {
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  overflow: hidden;
}

.order {
  display: inline-flex;
}

.person {
  display: inline-flex;
  gap: var(--pnmt-space-2);
  align-items: baseline;
}

.week {
  display: inline-flex;
  gap: 2px;
}

.weekday {
  display: inline-block;
  width: 2rem;
  text-align: center;
  border-radius: 3px;
  font-size: 0.786rem;
  font-weight: 600;
  border: 1px solid var(--pnmt-border);
}

.weekday.off {
  background: var(--pnmt-none-soft);
  color: var(--pnmt-muted);
  font-weight: 400;
}

.small {
  font-size: 0.857rem;
}
</style>
