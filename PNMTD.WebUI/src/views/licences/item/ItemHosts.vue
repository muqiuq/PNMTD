<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import {
  licenseKeys,
  licensesApi,
  type LicenseAssignment,
  type LicenseDetail,
  type LicenseSensorLink,
} from '@/api/licenses'
import CodeTag from '@/components/CodeTag.vue'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import InlineField from '@/components/InlineField.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'
import VerdictTag from '../VerdictTag.vue'
import LinkSensorsDialog from './LinkSensorsDialog.vue'
import ActivationMatches from '../ActivationMatches.vue'

/**
 * The item's *Hosts & sensors* tab (docs/licences-plan.md §5.1, §5.3, §5.4, §8.7): the hosts it is assigned to, each with its location,
 * its sensor's state and last message, the seats and a note changed in place, and who assigned it when; unassigning one or many.
 * Then the linked sensors that watch it, with their state and last message. The hosts and sensors link to their pages for Monitoring:
 * View.
 */
const props = defineProps<{ item: LicenseDetail; edit: boolean }>()
const emit = defineEmits<{ assign: [] }>()
const session = useSession()
const queryClient = useQueryClient()
const { run, ask } = useActions()
const monitoring = computed(() => session.can('Monitoring', 'View'))

const assignments = useQuery({
  queryKey: computed(() => licenseKeys.assignments(props.item.id)),
  queryFn: () => licensesApi.assignments(props.item.id),
})
const links = useQuery({
  queryKey: computed(() => licenseKeys.links(props.item.id)),
  queryFn: () => licensesApi.sensorLinks(props.item.id),
})
const selected = ref<LicenseAssignment[]>([])
const linking = ref(false)

async function saveAssignment(a: LicenseAssignment, seats: number | null, note: string | null) {
  await licensesApi.updateAssignment(props.item.id, a.hostId, seats, note)
  await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
}
const number = (v: unknown) => (v === null || v === undefined || v === '' ? null : Number(v))
const text = (v: unknown) => (v === null || v === undefined ? null : String(v).trim() || null)

async function unassign(rows: LicenseAssignment[]) {
  if (!rows.length) return
  const names = rows.length === 1 ? rows[0].hostName : `${rows.length} hosts`
  if (
    !(await ask(
      `Unassign ${props.item.display} from ${names}?`,
      `Its sensor on ${rows.length === 1 ? 'that host' : 'each of them'} goes, with its events. The item stays.`,
      'Unassign',
    ))
  )
    return
  const done = await run(
    () =>
      licensesApi.unassign(
        props.item.id,
        rows.map((r) => r.hostId),
      ),
    { success: `Unassigned from ${names}`, failure: 'Not unassigned', invalidate: [licenseKeys.all] },
  )
  if (done) selected.value = []
}

async function unlink(link: LicenseSensorLink) {
  if (!(await ask(`Unlink ${link.sensorName}?`, `Its page no longer shows ${props.item.display}.`, 'Unlink'))) return
  await run(() => licensesApi.unlink(props.item.id, link.sensorId), {
    success: `${link.sensorName} unlinked`,
    failure: 'Not unlinked',
    invalidate: [licenseKeys.all],
  })
}
</script>

<template>
  <div class="hosts">
    <section class="panel" aria-labelledby="hosts-title">
      <div class="panel-header">
        <span id="hosts-title">Hosts</span>
        <span
          v-if="item.seatsFromHosts && item.seats !== null"
          class="muted seats"
          :class="{ over: item.usedSeats > item.seats }"
        >
          <i v-if="item.usedSeats > item.seats" class="pi pi-exclamation-triangle" aria-hidden="true" />
          {{ item.usedSeats }} of {{ item.seats }} {{ item.seatUnit }} used
        </span>
        <span class="spacer" />
        <template v-if="edit">
          <Button
            v-if="selected.length"
            :label="`Unassign ${selected.length}…`"
            icon="pi pi-minus-circle"
            size="small"
            severity="danger"
            text
            data-test="unassign-selected"
            @click="unassign(selected)"
          />
          <Button
            v-if="item.status !== 'Retired'"
            label="Assign to hosts…"
            icon="pi pi-plus"
            size="small"
            text
            data-test="hosts-assign"
            @click="emit('assign')"
          />
        </template>
      </div>
      <ErrorState
        v-if="assignments.isError.value"
        :error="assignments.error.value"
        what="the hosts"
        @retry="assignments.refetch()"
      />
      <EmptyState
        v-else-if="assignments.data.value && !assignments.data.value.length"
        icon="pi pi-server"
        title="On no host: this item has no sensor. Its owner is told by e-mail."
      />
      <DataTable
        v-else
        v-model:selection="selected"
        :value="assignments.data.value ?? []"
        :loading="assignments.isPending.value"
        data-key="hostId"
        sort-field="hostName"
        :sort-order="1"
        size="small"
        removable-sort
        data-test="assignments"
      >
        <Column v-if="edit" selection-mode="multiple" style="width: 2.5rem" />
        <Column field="hostName" header="Host" sortable>
          <template #body="{ data }">
            <span :class="{ muted: !data.hostEnabled }">
              <RouterLink v-if="monitoring" :to="{ name: 'host', params: { id: data.hostId } }">{{
                data.hostName
              }}</RouterLink>
              <template v-else>{{ data.hostName }}</template>
              <template v-if="!data.hostEnabled"> (disabled)</template>
            </span>
          </template>
        </Column>
        <Column field="locationName" header="Location" sortable>
          <template #body="{ data }">
            <span class="ellipsis">{{ data.locationName ?? '–' }}</span>
          </template>
        </Column>
        <Column field="lastCode" header="State" sortable>
          <template #body="{ data }">
            <span class="state">
              <template v-if="data.sensorId">
                <RouterLink
                  v-if="monitoring"
                  :to="{ name: 'sensor', params: { id: data.sensorId } }"
                  :aria-label="`The sensor on ${data.hostName}`"
                  ><VerdictTag :code="data.lastCode"
                /></RouterLink>
                <VerdictTag v-else :code="data.lastCode" />
                <Tag v-if="data.sensorEnabled === false" value="disabled" severity="secondary" />
                <Tag v-if="data.sensorIgnore" value="ignored" severity="secondary" />
              </template>
              <span v-else class="muted">no sensor</span>
            </span>
          </template>
        </Column>
        <Column field="lastMessage" header="Last message">
          <template #body="{ data }">
            <span class="ellipsis message" :title="data.lastMessage ?? ''">{{ data.lastMessage ?? '–' }}</span>
          </template>
        </Column>
        <Column field="seats" header="Seats" sortable style="width: 7rem">
          <template #body="{ data }">
            <InlineField
              :value="data.seats"
              type="number"
              label="Seats"
              field="seats"
              :readonly="!edit"
              :save="(v) => saveAssignment(data, number(v), data.note ?? null)"
            />
          </template>
        </Column>
        <Column field="note" header="Note">
          <template #body="{ data }">
            <InlineField
              :value="data.note"
              label="Note"
              field="note"
              :readonly="!edit"
              :save="(v) => saveAssignment(data, data.seats, text(v))"
            />
          </template>
        </Column>
        <Column field="assignedAt" header="Assigned" sortable>
          <template #body="{ data }">
            <span class="nowrap"
              ><RelativeTime :value="data.assignedAt" /><span v-if="data.assignedBy" class="muted">
                by {{ data.assignedBy }}</span
              ></span
            >
          </template>
        </Column>
        <Column v-if="edit" style="width: 3rem">
          <template #body="{ data }">
            <Button
              icon="pi pi-times"
              text
              rounded
              size="small"
              severity="secondary"
              :aria-label="`Unassign from ${data.hostName}`"
              @click="unassign([data])"
            />
          </template>
        </Column>
      </DataTable>
    </section>

    <!-- docs/licences-plan.md §8.11: Windows hosts whose installed key ends in this item's keys' last five characters. -->
    <ActivationMatches
      v-if="item.licenseKeyHint || item.productKeyHint"
      :license-id="item.id"
      :retired="item.status === 'Retired'"
    />

    <section class="panel" aria-labelledby="links-title">
      <div class="panel-header">
        <span id="links-title">Linked sensors</span>
        <span class="spacer" />
        <Button
          v-if="edit"
          label="Link sensors…"
          icon="pi pi-link"
          size="small"
          text
          data-test="link-sensors"
          @click="linking = true"
        />
      </div>
      <ErrorState
        v-if="links.isError.value"
        :error="links.error.value"
        what="the linked sensors"
        @retry="links.refetch()"
      />
      <EmptyState
        v-else-if="links.data.value && !links.data.value.length"
        icon="pi pi-link"
        title="No sensor linked."
        text="Link the sensors that watch it, as the line's ping: their pages then show its provider and support."
      />
      <DataTable
        v-else
        :value="links.data.value ?? []"
        :loading="links.isPending.value"
        data-key="sensorId"
        sort-field="hostName"
        :sort-order="1"
        size="small"
        removable-sort
        data-test="sensor-links"
      >
        <Column field="hostName" header="Host" sortable>
          <template #body="{ data }">
            <RouterLink v-if="monitoring" :to="{ name: 'host', params: { id: data.hostId } }">{{
              data.hostName
            }}</RouterLink>
            <template v-else>{{ data.hostName }}</template>
          </template>
        </Column>
        <Column field="sensorName" header="Sensor" sortable>
          <template #body="{ data }">
            <RouterLink v-if="monitoring" :to="{ name: 'sensor', params: { id: data.sensorId } }">{{
              data.sensorName
            }}</RouterLink>
            <template v-else>{{ data.sensorName }}</template>
          </template>
        </Column>
        <Column field="lastCode" header="State" sortable style="width: 6rem">
          <template #body="{ data }"><CodeTag :code="data.lastCode" /></template>
        </Column>
        <Column field="lastMessage" header="Last message">
          <template #body="{ data }">
            <span class="ellipsis message" :title="data.lastMessage ?? ''">{{ data.lastMessage ?? '–' }}</span>
          </template>
        </Column>
        <Column field="note" header="Note">
          <template #body="{ data }">{{ data.note ?? '' }}</template>
        </Column>
        <Column v-if="edit" style="width: 3rem">
          <template #body="{ data }">
            <Button
              icon="pi pi-times"
              text
              rounded
              size="small"
              severity="secondary"
              :aria-label="`Unlink ${data.sensorName}`"
              @click="unlink(data)"
            />
          </template>
        </Column>
      </DataTable>
    </section>

    <LinkSensorsDialog v-if="edit" v-model:visible="linking" :license-id="item.id" :display="item.display" />
  </div>
</template>

<style scoped>
.hosts {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.spacer {
  flex: 1;
}

.seats {
  font-weight: 400;
}

.over {
  color: var(--pnmt-warn);
}

.state {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.message {
  display: block;
  max-width: 24rem;
}
</style>
