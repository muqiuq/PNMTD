<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import DatePicker from 'primevue/datepicker'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNotify } from '@/composables/useNotify'
import { useNow } from '@/composables/useNow'
import { formatAbsolute } from '@/format'
import { useSession } from '@/stores/session'
import { accessGroupLabel, accessGroups, caseLink, targetKindLabel } from '@/views/cookie/access'
import LocationPicker from '@/views/locations/LocationPicker.vue'
import {
  knownForm,
  knownProblems,
  knownRequest,
  knownState,
  type KnownForm,
  type KnownState,
  type TargetOption,
} from './accessAdmin'

/**
 * Known access (docs/cookie-access-plan.md §4.4, §7): what people said is expected, which counts as history so it stays
 * quiet. Each for a target, a location or every target; an account or any; a network or any; some ways or any; until a
 * day or without end. Added here or by Remember this on a case; End now stops one. Both take effect at once and are in
 * the audit log; they aren't part of the versioned settings.
 */
const props = defineProps<{ targets: TargetOption[] }>()
const session = useSession()
const queryClient = useQueryClient()
const notify = useNotify()
const now = useNow()
const knownKey = [...keys.cookie, 'admin', 'access', 'known'] as const
const known = useQuery({ queryKey: knownKey, queryFn: api.cookie.accessKnown, refetchInterval: 60_000 })
const stateOf = (k: S['AccessKnownItem']) => knownState(k, now.value)
const stateSeverity = (s: KnownState) => (s === 'active' ? 'success' : 'secondary')
const canLocations = computed(() => session.can('Locations', 'View'))
const canCookie = computed(() => session.can('Cookie', 'Use'))
const scopeText = (k: S['AccessKnownItem']) =>
  k.scope === 'location' && k.target ? `${k.label}, ${targetKindLabel[k.target].toLowerCase()}s` : k.label

// --- End now ----------------------------------------------------------------------------------------------------
const ending = ref<string | null>(null)
async function end(k: S['AccessKnownItem']) {
  ending.value = k.id
  try {
    await api.cookie.endAccessKnown(k.id)
    notify.success('Ended', 'The same access is new again from now on.')
    await queryClient.invalidateQueries({ queryKey: knownKey })
  } catch (e) {
    notify.error(e, 'Not ended')
  } finally {
    ending.value = null
  }
}

// --- a new entry ------------------------------------------------------------------------------------------------
const form = ref<KnownForm | null>(null)
const errors = ref<Record<string, string>>({})
const tried = ref(false)
const busy = ref(false)
const dialogOpen = computed({ get: () => form.value !== null, set: (v) => !v && (form.value = null) })
const problems = computed(() => (form.value ? knownProblems(form.value, Date.now()) : {}))
const shown = computed(() => (tried.value ? { ...problems.value, ...errors.value } : errors.value))
watch(form, () => (errors.value = {}), { deep: true })

const scopes = [
  { label: 'One target', value: 'target' },
  { label: 'A location', value: 'location' },
  { label: 'Every target', value: 'all' },
]
const kinds = [
  { label: 'Every kind of target', value: null as S['AccessTargetKind'] | null },
  { label: 'Servers', value: 'Server' as S['AccessTargetKind'] | null },
  { label: 'Routers', value: 'Router' as S['AccessTargetKind'] | null },
  { label: 'PNMT', value: 'Pnmt' as S['AccessTargetKind'] | null },
]
const groupOptions = accessGroups.map((g) => ({ label: accessGroupLabel(g), value: g as string }))
const minDate = computed(() => new Date(now.value))

function start() {
  form.value = knownForm()
  tried.value = false
  errors.value = {}
}

async function add() {
  if (!form.value) return
  tried.value = true
  if (Object.keys(knownProblems(form.value, Date.now())).length) return
  busy.value = true
  try {
    const created = await api.cookie.addAccessKnown(knownRequest(form.value, props.targets))
    notify.success('Known access added', `${created.label}: ${created.account ?? 'any account'}.`)
    form.value = null
    await queryClient.invalidateQueries({ queryKey: knownKey })
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length)
      errors.value = Object.fromEntries(Object.entries(e.errors).map(([k, v]) => [k, v[0] ?? '']))
    else notify.error(e, 'Not added')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <section class="panel" aria-labelledby="access-known-title">
    <div id="access-known-title" class="panel-header">
      Known access
      <Button label="Add…" icon="pi pi-plus" size="small" severity="secondary" text class="end" @click="start" />
    </div>
    <div class="panel-body body">
      <p class="muted small intro">
        What people said is expected counts as history: the same account, from the same network, in the same way, stays
        quiet. Added here, or by Remember this when a contact answers Expected. Takes effect at once, not with Save.
      </p>
      <ErrorState
        v-if="known.isError.value && !known.data.value"
        :error="known.error.value"
        what="the known access"
        @retry="known.refetch()"
      />
      <p v-else-if="known.data.value && !known.data.value.length" class="muted none">None yet.</p>
      <DataTable v-else :value="known.data.value ?? []" :loading="known.isPending.value" size="small" class="table">
        <Column header="For">
          <template #body="{ data }">
            <div :class="{ muted: stateOf(data) !== 'active' }">{{ scopeText(data) }}</div>
            <div class="small">
              <span :class="data.account ? 'mono' : 'muted'">{{ data.account ?? 'any account' }}</span>
              <span class="muted"> · </span>
              <span :class="data.source ? 'mono' : 'muted'">{{ data.source ?? 'any source' }}</span>
              <span class="muted">
                ·
                {{ data.groups.length ? data.groups.map(accessGroupLabel).join(', ') : 'any way' }}</span
              >
            </div>
          </template>
        </Column>
        <Column header="Why">
          <template #body="{ data }">
            <div class="wrap">{{ data.comment }}</div>
            <div class="muted small">
              {{ data.createdByName || 'someone' }}, <RelativeTime :value="data.createdAt" /><template
                v-if="data.fromCaseId"
              >
                ·
                <RouterLink v-if="canCookie" :to="caseLink(data.fromCaseId)">from a case</RouterLink
                ><span v-else>from a case</span></template
              >
            </div>
          </template>
        </Column>
        <Column header="Until" style="width: 9rem">
          <template #body="{ data }">
            <span v-if="data.until" class="small">{{ formatAbsolute(data.until) }}</span>
            <span v-else class="muted">no end</span>
          </template>
        </Column>
        <Column header="" style="width: 9rem">
          <template #body="{ data }">
            <Button
              v-if="stateOf(data) === 'active'"
              label="End now"
              size="small"
              severity="secondary"
              text
              :loading="ending === data.id"
              @click="end(data)"
            />
            <template v-else>
              <Tag :value="stateOf(data)" :severity="stateSeverity(stateOf(data))" />
              <div v-if="data.endedByName" class="muted small">by {{ data.endedByName }}</div>
            </template>
          </template>
        </Column>
      </DataTable>
    </div>

    <Dialog v-model:visible="dialogOpen" header="Add known access" modal :style="{ width: 'min(34rem, 95vw)' }">
      <form v-if="form" id="access-known-form" class="form" novalidate @submit.prevent="add">
        <div class="field">
          <span class="label">For</span>
          <SelectButton
            v-model="form.scope"
            :options="scopes"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            size="small"
            aria-label="For"
          />
        </div>
        <div v-if="form.scope === 'target'" class="field">
          <label for="known-target">Target</label>
          <Select
            v-model="form.target"
            input-id="known-target"
            :options="targets"
            option-label="label"
            option-value="value"
            filter
            placeholder="Choose a server, a router or PNMT"
            :invalid="!!shown.sensorId"
          />
          <small v-if="shown.sensorId" class="field-error">{{ shown.sensorId }}</small>
        </div>
        <div v-else-if="form.scope === 'location'" class="field">
          <label for="known-location">Location</label>
          <LocationPicker
            v-if="canLocations"
            v-model="form.locationId"
            input-id="known-location"
            none-label="Choose a location"
            :invalid="!!shown.locationId"
          />
          <InputText v-else id="known-location" v-model="form.locationId" class="mono" placeholder="Its id" />
          <small v-if="shown.locationId" class="field-error">{{ shown.locationId }}</small>
        </div>
        <div v-if="form.scope !== 'target'" class="field">
          <label for="known-kind">Kind of target</label>
          <Select
            v-model="form.kind"
            input-id="known-kind"
            :options="kinds"
            option-label="label"
            option-value="value"
          />
        </div>
        <div class="form-grid">
          <div class="field">
            <label for="known-account">Account</label>
            <InputText
              id="known-account"
              v-model="form.account"
              class="mono"
              placeholder="Any account"
              maxlength="520"
              :invalid="!!shown.account"
            />
            <small v-if="shown.account" class="field-error">{{ shown.account }}</small>
          </div>
          <div class="field">
            <label for="known-source">Source</label>
            <InputText
              id="known-source"
              v-model="form.source"
              class="mono"
              placeholder="Any source"
              :invalid="!!shown.source"
            />
            <small v-if="shown.source" class="field-error">{{ shown.source }}</small>
            <small v-else class="field-hint">A network such as 198.51.100.0/24, or an address.</small>
          </div>
        </div>
        <div class="field">
          <label for="known-groups">Ways</label>
          <MultiSelect
            v-model="form.groups"
            input-id="known-groups"
            :options="groupOptions"
            option-label="label"
            option-value="value"
            display="chip"
            placeholder="Any way"
            :invalid="!!shown.groups"
          />
          <small v-if="shown.groups" class="field-error">{{ shown.groups }}</small>
        </div>
        <div class="field">
          <label for="known-until">Until</label>
          <DatePicker
            v-model="form.until"
            input-id="known-until"
            :min-date="minDate"
            show-button-bar
            placeholder="No end"
            :invalid="!!shown.until"
          />
          <small v-if="shown.until" class="field-error">{{ shown.until }}</small>
        </div>
        <div class="field">
          <label for="known-comment">Why</label>
          <Textarea
            id="known-comment"
            v-model="form.comment"
            rows="2"
            auto-resize
            maxlength="500"
            placeholder="The backup vendor connects from its jump host on Saturdays"
            :invalid="!!shown.comment"
          />
          <small v-if="shown.comment" class="field-error">{{ shown.comment }}</small>
        </div>
      </form>
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="form = null" />
        <Button type="submit" form="access-known-form" label="Add" :loading="busy" />
      </template>
    </Dialog>
  </section>
</template>

<style scoped>
.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.intro,
.none {
  margin: 0;
}

.end {
  margin-left: auto;
}

.table {
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  overflow: hidden;
}

.wrap {
  white-space: pre-wrap;
  word-break: break-word;
}

.label {
  font-weight: 500;
}

.small {
  font-size: 0.857rem;
}
</style>
