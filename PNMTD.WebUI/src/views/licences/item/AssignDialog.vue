<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { licenseKeys, licensesApi, type LicenseDetail, type LicenseTarget } from '@/api/licenses'
import { useNotify } from '@/composables/useNotify'
import TargetPicker from './TargetPicker.vue'

/**
 * Assign to hosts… (docs/licences-plan.md §5.1): hosts found by name, several at once (hardware picks one unless asked for more),
 * the seats each uses, a note, and the seat count live. Going over is said in the warn tone, never refused: the licence may be about
 * to grow, and the finding (449) says what is wrong. Each host gets the item's sensor.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{
  item: Pick<
    LicenseDetail,
    'id' | 'display' | 'type' | 'seats' | 'seatUnit' | 'seatsFromHosts' | 'usedSeats' | 'status'
  >
}>()
const emit = defineEmits<{ done: [] }>()
const queryClient = useQueryClient()
const notify = useNotify()

interface Chosen {
  id: string
  name: string
  locationName: string | null
  seats: number | null
}
const chosen = ref<Chosen[]>([])
const seats = ref<number | null>(1)
const note = ref('')
const several = ref(true)
const problem = ref<string | null>(null)
const errors = ref<Record<string, string | undefined>>({})
const saving = ref(false)
watch(visible, (open) => {
  if (!open) return
  chosen.value = []
  seats.value = 1
  note.value = ''
  several.value = props.item.type !== 'Hardware'
  problem.value = null
  errors.value = {}
})

function pick(target: LicenseTarget) {
  if (target.assigned) {
    notify.info(`${target.name} is assigned already`, 'Its seats and note are changed on the list.')
    return
  }
  if (chosen.value.some((c) => c.id === target.id)) return
  const row = { id: target.id, name: target.name, locationName: target.locationName ?? null, seats: seats.value ?? 1 }
  chosen.value = several.value ? [...chosen.value, row] : [row]
}
const remove = (id: string) => (chosen.value = chosen.value.filter((c) => c.id !== id))
// A new default for the seats applies to the hosts already chosen too.
watch(seats, (value) => chosen.value.forEach((c) => (c.seats = value ?? 1)))

const counted = computed(() => props.item.seatsFromHosts && props.item.seats !== null)
const adding = computed(() => chosen.value.reduce((sum, c) => sum + (c.seats || 1), 0))
const total = computed(() => props.item.usedSeats + adding.value)
const overAssigned = computed(() => counted.value && total.value > props.item.seats!)
const countText = computed(() =>
  overAssigned.value
    ? `This makes ${total.value} of ${props.item.seats} ${props.item.seatUnit}: the licence will be over-assigned (449) on all its hosts.`
    : chosen.value.length
      ? `This makes ${total.value} of ${props.item.seats} ${props.item.seatUnit} used.`
      : `${props.item.usedSeats} of ${props.item.seats} ${props.item.seatUnit} used.`,
)

async function save() {
  if (!chosen.value.length) return
  saving.value = true
  problem.value = null
  errors.value = {}
  try {
    // One call per number of seats: the API takes the seats for all hosts of a call.
    const groups = new Map<number, string[]>()
    for (const c of chosen.value) groups.set(c.seats || 1, [...(groups.get(c.seats || 1) ?? []), c.id])
    for (const [n, hostIds] of groups) await licensesApi.assign(props.item.id, hostIds, n, note.value.trim() || null)
    await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
    notify.success(
      `${props.item.display} assigned to ${chosen.value.length === 1 ? chosen.value[0].name : `${chosen.value.length} hosts`}`,
    )
    visible.value = false
    emit('done')
  } catch (e) {
    if (e instanceof ApiError && (e.code === 'license_retired' || e.code === 'too_many_assignments'))
      problem.value = e.message
    else if (e instanceof ApiError && Object.keys(e.errors).length)
      errors.value = { seats: e.field('seats'), note: e.field('note'), hostIds: e.field('hostIds') }
    else notify.error(e, 'Not assigned')
    // Some groups may be in: the list shows them.
    await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="`Assign ${item.display} to hosts`" modal :style="{ width: '36rem' }">
    <form id="licence-assign" class="form" novalidate @submit.prevent="save">
      <Message v-if="item.status === 'Retired'" severity="warn" :closable="false" size="small">
        A retired item can't be assigned: reactivate it first.
      </Message>
      <Message v-if="problem" severity="error" :closable="false" size="small" data-test="assign-problem">{{
        problem
      }}</Message>
      <div class="field">
        <label for="la-host">{{ several ? 'Hosts' : 'Host' }}</label>
        <TargetPicker kind="host" :license-id="item.id" input-id="la-host" @pick="pick" />
        <small v-if="errors.hostIds" class="field-error">{{ errors.hostIds }}</small>
        <label v-if="item.type === 'Hardware'" class="check">
          <Checkbox v-model="several" binary input-id="la-several" />
          <span>Several hosts (ten laptops on one invoice)</span>
        </label>
      </div>
      <ul v-if="chosen.length" class="chosen" data-test="assign-chosen">
        <li v-for="c in chosen" :key="c.id">
          <i class="pi pi-server muted" aria-hidden="true" />
          <span class="name ellipsis"
            >{{ c.name }}<span v-if="c.locationName" class="muted"> · {{ c.locationName }}</span></span
          >
          <InputNumber
            v-model="c.seats"
            :min="1"
            :max="1000000"
            size="small"
            input-class="seats-input"
            :aria-label="`Seats on ${c.name}`"
          />
          <Button
            icon="pi pi-times"
            text
            rounded
            size="small"
            severity="secondary"
            :aria-label="`Remove ${c.name}`"
            @click="remove(c.id)"
          />
        </li>
      </ul>
      <div class="row">
        <div class="field">
          <label for="la-seats">Seats per host</label>
          <InputNumber v-model="seats" input-id="la-seats" :min="1" :max="1000000" :invalid="!!errors.seats" fluid />
          <small v-if="errors.seats" class="field-error">{{ errors.seats }}</small>
          <small v-else class="field-hint">What each host uses, e.g. 16 cores; changed per host above.</small>
        </div>
        <div class="field grow">
          <label for="la-note">Note</label>
          <InputText id="la-note" v-model="note" :invalid="!!errors.note" />
          <small v-if="errors.note" class="field-error">{{ errors.note }}</small>
        </div>
      </div>
      <p v-if="counted" class="count" :class="{ over: overAssigned }" data-test="assign-count">
        <i v-if="overAssigned" class="pi pi-exclamation-triangle" aria-hidden="true" />
        {{ countText }}
      </p>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="licence-assign"
        :label="chosen.length > 1 ? `Assign to ${chosen.length} hosts` : 'Assign'"
        :disabled="!chosen.length || item.status === 'Retired'"
        :loading="saving"
        data-test="assign-save"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.check {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  margin-top: var(--pnmt-space-1);
}

.chosen {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: var(--pnmt-space-1);
  max-height: 16rem;
  overflow-y: auto;
}

.chosen li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.name {
  flex: 1;
}

.chosen :deep(.seats-input) {
  width: 5.5rem;
}

.row {
  display: flex;
  gap: var(--pnmt-space-3);
}

.grow {
  flex: 1;
}

.count {
  margin: 0;
}

.over {
  color: var(--pnmt-warn);
}
</style>
