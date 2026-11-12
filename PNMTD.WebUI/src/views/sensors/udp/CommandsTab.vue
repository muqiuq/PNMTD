<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S, type UdpCommandState } from '@/api/resources'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import { asciiPreview, fromBase64, hexPreview } from '@/format/bytes'
import { COMMAND_STATES, commandSeverity, isOpenCommand, resultStatusText, toggleRow, words } from '@/format/udp'
import CommandDetail from './CommandDetail.vue'
import CommandDialog from './CommandDialog.vue'

/**
 * A node's commands (docs/udp-iot-plan.md §6.4), the packet log of a node: every command a user
 * sent and every answer the device gave, with their bytes. A row opens its communication. Users
 * queue new ones, cancel open ones, and send one again.
 */
const props = defineProps<{ sensor: S['SensorDetail']; canWrite: boolean }>()
const { ask, done } = useActions()

const state = ref<UdpCommandState | ''>('')
const states = [{ label: 'All states', value: '' }, ...COMMAND_STATES.map((s) => ({ label: s, value: s }))]
const filters = computed(() => ({ state: state.value || undefined, sensorId: props.sensor.id }))
const table = useServerTable<S['UdpCommandItem']>({
  key: [...keys.udp, 'node', props.sensor.id, 'commands'],
  fetch: ({ sensorId: _sensorId, ...q }) => api.udp.commands(props.sensor.id, q),
  defaultSort: '-created',
  pageSize: 25,
  filters,
  refetchInterval: 10_000,
})
const expanded = ref<Record<string, boolean>>({})

const dialog = ref(false)
const prefill = ref<S['UdpCommandItem'] | null>(null)
function compose(from: S['UdpCommandItem'] | null = null) {
  prefill.value = from
  dialog.value = true
}

async function cancel(c: S['UdpCommandItem']) {
  const ok = await ask(
    `Cancel command ${c.cmdId}?`,
    c.state === 'Sent'
      ? 'It was sent already: the device may have run it. It is not sent again, and a late result is still logged.'
      : 'It is not sent. The command and its log stay.',
    'Cancel command',
  )
  if (ok) {
    await done(() => api.udp.cancelCommand(props.sensor.id, c.id), {
      success: `Command ${c.cmdId} cancelled`,
      invalidate: [keys.udpNode(props.sensor.id), keys.sensor(props.sensor.id)],
    })
  }
}

const bytes = (b64: string | null) => fromBase64(b64)
</script>

<template>
  <div class="tab">
    <div class="toolbar">
      <Select
        v-model="state"
        :options="states"
        option-label="label"
        option-value="value"
        size="small"
        class="state"
        aria-label="State"
        placeholder="All states"
      />
      <span class="muted">
        Delivered in the response to the node's next packet. Kept permanently, with every packet in both directions.
      </span>
      <Button v-if="canWrite" label="New command" icon="pi pi-plus" size="small" class="new" @click="compose()" />
    </div>
    <ListTable
      v-model:expanded="expanded"
      :table="table"
      what="commands"
      :empty-text="state ? 'No command in this state.' : 'No command sent to this node yet.'"
      selectable
      @select="(c) => (expanded = toggleRow(expanded, c.id))"
    >
      <Column expander style="width: 2.5rem" />
      <Column header="State" style="width: 7.5rem">
        <template #body="{ data }"
          ><Tag :value="words(data.state)" :severity="commandSeverity(data.state, data.resultStatus)"
        /></template>
      </Column>
      <Column header="CMD_ID" style="width: 5rem">
        <template #body="{ data }"
          ><span class="mono">{{ data.cmdId }}</span></template
        >
      </Column>
      <Column header="Command">
        <template #body="{ data }">
          <span class="bytes-cell">
            <code class="ellipsis">{{ hexPreview(bytes(data.payload), 8) }}</code>
            <code class="ellipsis muted">{{ asciiPreview(bytes(data.payload), 16) }}</code>
          </span>
        </template>
      </Column>
      <Column field="created" header="Queued" sortable style="width: 9rem">
        <template #body="{ data }">
          <RelativeTime :value="data.created" />
          <div v-if="data.createdBy" class="muted small">by {{ data.createdBy }}</div>
        </template>
      </Column>
      <Column header="Attempts" style="width: 6rem">
        <template #body="{ data }">
          <span v-if="data.delivery === 'FireAndForget'" title="Fire and forget">{{ data.attempts }} / 1</span>
          <span v-else>{{ data.attempts }} / {{ data.maxAttempts }}</span>
        </template>
      </Column>
      <Column header="Expires" style="width: 7rem">
        <template #body="{ data }">
          <RelativeTime v-if="data.expiresAt && isOpenCommand(data.state)" :value="data.expiresAt" />
          <span v-else-if="!data.expiresAt" class="muted">never</span>
          <span v-else class="muted">–</span>
        </template>
      </Column>
      <Column header="Result">
        <template #body="{ data }">
          <template v-if="data.resultStatus !== null">
            <span :class="{ bad: data.resultStatus !== 0 }">{{
              data.resultStatusText ?? resultStatusText(data.resultStatus)
            }}</span>
            <span v-if="data.result" class="bytes-cell">
              <code class="ellipsis">{{ hexPreview(bytes(data.result), 6) }}</code>
              <code class="ellipsis muted">{{ asciiPreview(bytes(data.result), 12) }}</code>
            </span>
          </template>
          <span v-else-if="data.note" class="muted ellipsis note" :title="data.note">{{ data.note }}</span>
          <span v-else class="muted">–</span>
        </template>
      </Column>
      <Column v-if="canWrite" header="" style="width: 5.5rem">
        <template #body="{ data }">
          <span class="row-actions">
            <Button
              v-tooltip.top="'Send again'"
              icon="pi pi-replay"
              text
              rounded
              size="small"
              severity="secondary"
              :aria-label="`Send command ${data.cmdId} again`"
              @click="compose(data)"
            />
            <Button
              v-if="isOpenCommand(data.state)"
              v-tooltip.top="'Cancel'"
              icon="pi pi-times"
              text
              rounded
              size="small"
              severity="secondary"
              :aria-label="`Cancel command ${data.cmdId}`"
              @click="cancel(data)"
            />
          </span>
        </template>
      </Column>
      <template #expansion="{ data }">
        <CommandDetail :sensor-id="sensor.id" :command="data" />
        <div v-if="canWrite" class="mobile-actions">
          <Button label="Send again" icon="pi pi-replay" size="small" text @click="compose(data)" />
          <Button
            v-if="isOpenCommand(data.state)"
            label="Cancel"
            icon="pi pi-times"
            size="small"
            text
            severity="danger"
            @click="cancel(data)"
          />
        </div>
      </template>
      <template #mobile="{ item }">
        <div class="line">
          <Tag :value="words(item.state)" :severity="commandSeverity(item.state, item.resultStatus)" />
          <span class="mono">#{{ item.cmdId }}</span>
          <code class="ellipsis">{{ asciiPreview(bytes(item.payload), 16) }}</code>
          <RelativeTime :value="item.created" class="muted right" />
        </div>
      </template>
    </ListTable>
    <CommandDialog v-if="canWrite" v-model:visible="dialog" :sensor="sensor" :prefill="prefill" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.toolbar {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
  flex-wrap: wrap;
}

.toolbar .muted {
  font-size: 0.929rem;
  flex: 1 1 18rem;
}

.state {
  width: 10rem;
}

.new {
  margin-left: auto;
}

.bytes-cell {
  display: flex;
  flex-direction: column;
  min-width: 0;
  max-width: 18rem;
}

.small {
  font-size: 0.857rem;
}

.bad {
  color: var(--pnmt-fail);
  font-weight: 500;
}

.note {
  display: block;
  max-width: 14rem;
}

.row-actions {
  display: inline-flex;
}

.mobile-actions {
  display: none;
}

.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.right {
  margin-left: auto;
  white-space: nowrap;
}

@media (max-width: 599px) {
  .mobile-actions {
    display: flex;
    gap: var(--pnmt-space-2);
  }
}
</style>
