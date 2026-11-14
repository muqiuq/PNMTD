<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import { computed, ref } from 'vue'
import { api, keys, type RedmineTarget } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'
import LinkTicketDialog from './LinkTicketDialog.vue'
import NewInRedmine from './NewInRedmine.vue'
import TicketRow from './TicketRow.vue'
import { useRelated } from './useRelated'

/**
 * The Tickets panel of a page, a location, a host or a sensor (docs/audit-redmine-zabbix-plan.md §6.6), from PNMT's copy:
 * its own tickets, open first, the closed ones folded; then, folded, those of what it belongs to. *Link ticket…* and
 * *New in Redmine* for Redmine's Link. Hidden without Redmine's View, or when Redmine isn't configured.
 */
const props = withDefaults(
  defineProps<{
    pageId?: string
    locationId?: string
    hostId?: string
    sensorId?: string
    name: string
    /** Where the object is in PNMT, for *New in Redmine*'s description. */
    path: string
    /** A location's Tickets tab: its sub-locations' and hosts' too. */
    deep?: boolean
    ownOnly?: boolean
    bare?: boolean
  }>(),
  {
    pageId: undefined,
    locationId: undefined,
    hostId: undefined,
    sensorId: undefined,
    deep: false,
    ownOnly: false,
    bare: false,
  },
)
const session = useSession()
const queryClient = useQueryClient()
const { ask, done } = useActions()

const target = computed<RedmineTarget>(() => ({
  pageId: props.pageId,
  locationId: props.locationId,
  hostId: props.hostId,
  sensorId: props.sensorId,
}))
const { shown, query: related } = useRelated(target, () => props.deep)
const own = computed(() => related.data.value?.own ?? [])
const openOwn = computed(() => own.value.filter((t) => !t.ticket.isClosed))
const closedOwn = computed(() => own.value.filter((t) => t.ticket.isClosed))
const via = computed(() => (props.ownOnly ? [] : (related.data.value?.via ?? [])))
const showClosed = ref(false)
const open = ref<Record<number, boolean>>({})
const linking = ref(false)
const stale = computed(() => {
  const asOf = related.data.value?.asOf
  return asOf ? Date.now() - Date.parse(asOf) > 25 * 60_000 : false
})

async function unlink(id: string, number: number) {
  if (!(await ask(`Unlink #${number} from ${props.name}?`, 'The ticket stays in Redmine.', 'Unlink'))) return
  if (await done(() => api.redmine.unlink(id), { success: `#${number} unlinked` })) {
    await queryClient.invalidateQueries({ queryKey: keys.redmine })
  }
}
</script>

<template>
  <section v-if="shown" id="tickets" :class="bare ? 'tickets bare' : 'tickets panel'" aria-label="Tickets">
    <div v-if="!bare" class="panel-header">
      <i class="pi pi-ticket muted" aria-hidden="true" />
      Tickets
      <span v-if="openOwn.length" class="muted count">{{ openOwn.length }} open</span>
      <span class="actions">
        <template v-if="session.can('Redmine', 'Link')">
          <Button label="Link ticket…" icon="pi pi-link" size="small" text @click="linking = true" />
          <NewInRedmine :subject="name" :path="path" />
        </template>
      </span>
    </div>
    <div :class="{ 'panel-body': !bare }">
      <div v-if="bare && session.can('Redmine', 'Link')" class="bare-actions">
        <Button label="Link ticket…" icon="pi pi-link" size="small" @click="linking = true" />
        <NewInRedmine :subject="name" :path="path" />
      </div>
      <p v-if="related.isSuccess.value && !own.length && !via.length" class="muted none">No tickets linked yet.</p>
      <ul v-if="openOwn.length" class="list">
        <li v-for="t in openOwn" :key="t.linkId">
          <TicketRow :ticket="t.ticket" :note="t.note">
            <template #actions>
              <Button
                v-if="session.can('Redmine', 'Link')"
                icon="pi pi-times"
                text
                rounded
                size="small"
                severity="secondary"
                :aria-label="`Unlink #${t.ticket.id}`"
                @click="unlink(t.linkId, t.ticket.id)"
              />
            </template>
          </TicketRow>
        </li>
      </ul>
      <template v-if="closedOwn.length">
        <button type="button" class="toggle" :aria-expanded="showClosed" @click="showClosed = !showClosed">
          <i :class="showClosed ? 'pi pi-chevron-down' : 'pi pi-chevron-right'" aria-hidden="true" />
          {{ closedOwn.length }} closed
        </button>
        <ul v-if="showClosed" class="list">
          <li v-for="t in closedOwn" :key="t.linkId"><TicketRow :ticket="t.ticket" :note="t.note" /></li>
        </ul>
      </template>
      <div v-for="(group, i) in via" :key="`${group.kind}-${group.id ?? group.name}`" class="via">
        <button type="button" class="toggle" :aria-expanded="!!open[i]" @click="open[i] = !open[i]">
          <i :class="open[i] ? 'pi pi-chevron-down' : 'pi pi-chevron-right'" aria-hidden="true" />
          <span>via {{ group.name }}</span>
          <span class="muted">{{ group.tickets.filter((t) => !t.ticket.isClosed).length }} open</span>
        </button>
        <ul v-if="open[i]" class="list">
          <li v-for="t in group.tickets" :key="t.linkId"><TicketRow :ticket="t.ticket" :note="t.note" /></li>
        </ul>
      </div>
      <p v-if="stale && related.data.value?.asOf" class="muted asof">
        As of <RelativeTime :value="related.data.value.asOf" />: Redmine didn't answer since.
      </p>
    </div>
    <LinkTicketDialog v-model:visible="linking" :target="target" :name="name" />
  </section>
</template>

<style scoped>
.panel-header .actions {
  margin-left: auto;
  display: inline-flex;
}

.count {
  font-weight: 400;
}

.bare-actions {
  display: flex;
  gap: var(--pnmt-space-2);
  margin-bottom: var(--pnmt-space-2);
}

.none,
.asof {
  margin: 0;
}

.list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.toggle {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  background: none;
  border: none;
  padding: var(--pnmt-space-2) 0;
  color: inherit;
  cursor: pointer;
}

.via {
  margin-top: var(--pnmt-space-1);
}
</style>
