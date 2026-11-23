<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { mayAcknowledge, openDifferences } from './mikrotikDraft'

/**
 * Acknowledging differences between a router and its join script as intended (docs/mikrotik-join-options-plan.md §3, §4.2), as
 * OS updates' findings are acknowledged: the open differences, each with a checkbox and all checked (for a User, not those of the
 * Admin settings), and the reason (required, at most 500 characters, in the audit log). There is no expiry: an acknowledgement
 * lasts until what it covers changes. From the Setup tab, and from the status strip, which lists nothing itself.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ sensor: S['SensorDetail']; isAdmin: boolean }>()
const queryClient = useQueryClient()
const { run, busy } = useActions()

const open = computed(() => openDifferences(props.sensor.mikrotik?.differences ?? []))
const adminOnly = computed(() => !props.isAdmin && open.value.some((d) => d.admin))
const chosen = ref<string[]>([])
const reason = ref('')
watch(visible, (isOpen) => {
  if (!isOpen) return
  reason.value = ''
  chosen.value = open.value.filter((d) => mayAcknowledge(d, props.isAdmin)).map((d) => d.id)
})

async function save() {
  const text = reason.value.trim()
  if (!text || !chosen.value.length) return
  const count = chosen.value.length
  const detail = await run(
    () => api.mikrotik.acknowledgeDifferences(props.sensor.id, { ids: chosen.value, reason: text }),
    {
      success: count === 1 ? 'Difference acknowledged' : `${count} differences acknowledged`,
      failure: 'Not acknowledged',
      invalidate: [keys.mikrotik],
    },
  )
  if (!detail) return
  queryClient.setQueryData(keys.sensor(props.sensor.id), { ...props.sensor, mikrotik: detail })
  visible.value = false
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="open.length === 1 ? 'Acknowledge a difference' : 'Acknowledge differences'"
    modal
    :style="{ width: '40rem' }"
    :breakpoints="{ '680px': '95vw' }"
  >
    <form id="acknowledge-differences-form" class="form" novalidate @submit.prevent="save">
      <p class="text">
        When the router differs from its join script on purpose, acknowledge it: the warning goes, and the difference
        stays in the Setup tab, greyed out, with your reason. It comes back when what it covers changes.
      </p>
      <ul class="chosen">
        <li v-for="d in open" :key="d.id">
          <Checkbox
            v-model="chosen"
            :input-id="`difference-${d.id}`"
            :value="d.id"
            :disabled="!mayAcknowledge(d, isAdmin)"
          />
          <label :for="`difference-${d.id}`" :class="{ muted: !mayAcknowledge(d, isAdmin) }">{{ d.text }}</label>
        </li>
      </ul>
      <Message v-if="adminOnly" severity="info" :closable="false" size="small">
        Only an Admin can acknowledge the policies, the management rule and retired keys, as only an Admin can change
        them.
      </Message>
      <div class="field">
        <label for="acknowledge-differences-reason">Reason</label>
        <Textarea
          id="acknowledge-differences-reason"
          v-model="reason"
          rows="3"
          maxlength="500"
          auto-resize
          placeholder="We manage this router's firewall ourselves"
        />
        <small class="field-hint"
          >Required, at most 500 characters ({{ reason.length }}). It is in the audit log.</small
        >
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="acknowledge-differences-form"
        label="Acknowledge"
        icon="pi pi-check"
        :loading="busy"
        :disabled="!reason.trim() || !chosen.length"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.text {
  margin: 0;
}

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

.chosen li {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-2);
}
</style>
