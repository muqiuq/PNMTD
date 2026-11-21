<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import SelectButton from 'primevue/selectbutton'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { api, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import FindingReasons from './FindingReasons.vue'
import { acknowledgeDays, defaultAcknowledgeDays } from './osUpdatesFormat'
import { osUpdatesInvalidate } from './useOsUpdates'

/**
 * Acknowledging high-risk findings of one server (docs/linux-os-updates-plan.md §6.5, §10.1 3): the chosen findings with their
 * reasons, the reason (required, at most 500 characters, in the audit log), and for how long: 1, 7 or 30 days, 7 by default,
 * never more than AcknowledgeMaxDays. There is no "forever".
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ sensorId: string; findings: S['OsFindingView'][]; maxDays: number }>()
const emit = defineEmits<{ done: [view: S['OsUpdatesView']] }>()
const { run, busy } = useActions()

const options = computed(() =>
  acknowledgeDays(props.maxDays).map((d) => ({ label: d === 1 ? '1 day' : `${d} days`, value: d })),
)
const reason = ref('')
const days = ref(7)
watch(visible, (open) => {
  if (!open) return
  reason.value = ''
  days.value = defaultAcknowledgeDays(acknowledgeDays(props.maxDays))
})

async function save() {
  const text = reason.value.trim()
  if (!text || !props.findings.length) return
  const view = await run(
    () =>
      api.linux.osUpdates.acknowledge(props.sensorId, {
        findingIds: props.findings.map((f) => f.id),
        reason: text,
        days: days.value,
      }),
    {
      success: props.findings.length === 1 ? 'Finding acknowledged' : `${props.findings.length} findings acknowledged`,
      failure: 'Not acknowledged',
      invalidate: osUpdatesInvalidate(props.sensorId),
    },
  )
  if (!view) return
  emit('done', view)
  visible.value = false
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="
      findings.length === 1 ? 'Acknowledge a high-risk finding' : `Acknowledge ${findings.length} high-risk findings`
    "
    modal
    :style="{ width: '40rem' }"
    :breakpoints="{ '680px': '95vw' }"
  >
    <form id="acknowledge-form" class="form" novalidate @submit.prevent="save">
      <ul class="chosen">
        <li v-for="f in findings" :key="f.id">
          <div class="line">
            <span class="mono cve">{{ f.cve }}</span>
            <strong>{{ f.sourcePackage }}</strong>
            <span class="mono small">{{ f.installed ?? '?' }} → {{ f.candidate }}</span>
          </div>
          <FindingReasons :reasons="f.reasons" />
        </li>
      </ul>
      <div class="field">
        <label for="acknowledge-reason">Reason</label>
        <Textarea
          id="acknowledge-reason"
          v-model="reason"
          rows="3"
          maxlength="500"
          auto-resize
          placeholder="Kernel freeze until the 28th; the CVE is only reachable through a module we don't load"
        />
        <small class="field-hint"
          >Required, at most 500 characters ({{ reason.length }}). It is in the audit log.</small
        >
      </div>
      <div class="field">
        <label id="acknowledge-days">For</label>
        <SelectButton
          v-model="days"
          :options="options"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          aria-labelledby="acknowledge-days"
        />
      </div>
      <Message severity="info" :closable="false" size="small">
        The finding stays visible, greyed out, and leaves 466's message. 466 returns when the time runs out, or earlier
        if the evidence gets stronger, for example when the CVE enters CISA KEV.
      </Message>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="acknowledge-form"
        label="Acknowledge"
        icon="pi pi-check"
        :loading="busy"
        :disabled="!reason.trim() || !findings.length"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.chosen {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  max-height: 16rem;
  overflow: auto;
}

.line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.cve {
  font-weight: 600;
  color: var(--pnmt-fail);
}

.small {
  font-size: 0.857rem;
}
</style>
