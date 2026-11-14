<script setup lang="ts">
import Button from 'primevue/button'
import ProgressSpinner from 'primevue/progressspinner'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import type { MenuItem } from 'primevue/menuitem'
import { computed, nextTick, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import TabNav from '@/components/TabNav.vue'
import { useActions } from '@/composables/useActions'
import { downloadText, fileSlug } from '@/format/markdown'
import { useSession } from '@/stores/session'
import HostsPanel from '@/views/dashboard/HostsPanel.vue'
import AddHostsDialog from './AddHostsDialog.vue'
import FieldCard from './FieldCard.vue'
import LocationDialog from './LocationDialog.vue'
import LocationHistory from './LocationHistory.vue'
import MoveDialog from './MoveDialog.vue'
import { grouped, isEmpty, locationMarkdown } from './fieldValues'
import { useLocationFields } from './useLocations'

/**
 * A location's page (docs/locations-knowledge-plan.md §3.4), a small dashboard: the path as a breadcrumb, the state and
 * the hosts counted with sub-locations'. *Overview*: on the left its sub-locations, then its hosts in the dashboard's
 * panel (each naming its sub-location); on the right the fields marked *Overview*, in their order, then the linked
 * pages. On a phone the information comes first. *Information*: every field, by group. *Knowledge base*: the pages.
 * *History*. It refreshes like the dashboard.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const queryClient = useQueryClient()
const { ask, done } = useActions()

const id = computed(() => String(route.params.id))
const tab = computed(() => (typeof route.params.tab === 'string' && route.params.tab ? route.params.tab : 'overview'))
const query = useQuery({
  queryKey: computed(() => keys.location(id.value)),
  queryFn: () => api.locations.get(id.value),
  refetchInterval: 30_000,
})
const location = computed(() => query.data.value)
const { query: fieldsQuery, active: fields } = useLocationFields()
const users = computed(() => fieldsQuery.data.value?.users ?? null)
const canWrite = computed(() => session.can('User'))

const values = computed(() => new Map((location.value?.values ?? []).map((v) => [v.fieldId, v])))
const valueOf = (fieldId: string) => values.value.get(fieldId) ?? null
const filled = (fieldId: string) => !isEmpty(valueOf(fieldId)?.value)
// Empty fields: *Not filled in* for Users, hidden for Viewers (§3.4).
const overviewFields = computed(() => fields.value.filter((f) => f.overview && (canWrite.value || filled(f.id))))
const informationGroups = computed(() => grouped(fields.value.filter((f) => canWrite.value || filled(f.id))))
const emptyCount = computed(() => fields.value.filter((f) => !filled(f.id)).length)
const files = computed(() => location.value?.files ?? [])

const tabs = computed(() => [
  { key: 'overview', label: 'Overview', to: { name: 'location', params: { id: id.value } } },
  { key: 'information', label: 'Information', to: { name: 'location', params: { id: id.value, tab: 'information' } } },
  { key: 'pages', label: 'Knowledge base', to: { name: 'location', params: { id: id.value, tab: 'pages' } } },
  { key: 'history', label: 'History', to: { name: 'location', params: { id: id.value, tab: 'history' } } },
])
const crumbs = computed(() => [
  { label: 'Locations', to: { name: 'locations' } },
  ...(location.value?.ancestors ?? []).map((a) => ({ label: a.name, to: { name: 'location', params: { id: a.id } } })),
])

const renaming = ref(false)
const moving = ref(false)
const addingSub = ref(false)
const addingHosts = ref(false)

async function refresh() {
  await queryClient.invalidateQueries({ queryKey: keys.locations })
}

async function removeHost(host: S['DashboardHost']) {
  await done(() => api.hosts.setLocation({ ids: [host.id], locationId: null }), {
    success: `${host.name} removed from ${host.assignedLocation?.name ?? 'the location'}`,
    failure: 'Not removed',
    invalidate: [keys.locations, keys.hosts],
  })
}
const hostMenu = (host: S['DashboardHost']): MenuItem[] => [
  {
    label:
      host.assignedLocation && host.assignedLocation.id !== id.value
        ? `Remove from ${host.assignedLocation.name}`
        : 'Remove from location',
    icon: 'pi pi-times',
    command: () => removeHost(host),
  },
]

function download() {
  const l = location.value!
  downloadText(`${fileSlug(l.path) || 'location'}.md`, locationMarkdown(l, fields.value))
}

async function remove() {
  const l = location.value!
  const ok = await ask(
    `Delete ${l.name}?`,
    `Its ${l.hostCounts.own} host${l.hostCounts.own === 1 ? '' : 's'} keep their location texts and have no location afterwards. Its values and history are deleted with it. This cannot be undone.`,
  )
  if (
    ok &&
    (await done(() => api.locations.remove(l.id), {
      success: `${l.name} deleted`,
      invalidate: [keys.locations, keys.hosts],
    }))
  ) {
    await router.replace(l.parentId ? { name: 'location', params: { id: l.parentId } } : { name: 'locations' })
  }
}

const more = computed<MenuItem[]>(() => [
  ...(canWrite.value
    ? [
        { label: 'Rename…', icon: 'pi pi-pencil', command: () => (renaming.value = true) },
        { label: 'Move…', icon: 'pi pi-arrows-alt', command: () => (moving.value = true) },
      ]
    : []),
  { label: 'Download as Markdown', icon: 'pi pi-download', command: download },
  { label: 'Print', icon: 'pi pi-print', command: () => window.print() },
  ...(session.can('Admin')
    ? [{ separator: true }, { label: 'Delete…', icon: 'pi pi-trash', class: 'danger', command: remove }]
    : []),
])

/** *Fill in*: to the first empty field of the Information tab. */
async function fillIn() {
  if (tab.value !== 'information') {
    await router.push({ name: 'location', params: { id: id.value, tab: 'information' } })
  }
  await nextTick()
  document.querySelector<HTMLElement>('.information .field-card.empty')?.scrollIntoView({ block: 'center' })
}
</script>

<template>
  <div class="page location-page">
    <ErrorState
      v-if="query.isError.value && !location"
      :error="query.error.value"
      what="the location"
      @retry="query.refetch()"
    />
    <div v-else-if="!location" class="center"><ProgressSpinner style="width: 40px; height: 40px" /></div>
    <template v-else>
      <PageHeader :title="location.name" :crumbs="crumbs">
        <template #status>
          <StatusBadge
            :status="location.state"
            :label="location.hostCounts.failing ? `${location.hostCounts.failing} failing` : undefined"
          />
          <span class="muted"
            >{{ location.hostCounts.total }} host{{ location.hostCounts.total === 1 ? '' : 's' }}</span
          >
          <span v-if="location.description" class="muted description">{{ location.description }}</span>
        </template>
        <template #actions>
          <MoreMenu :items="more" label="More actions for the location" />
        </template>
      </PageHeader>
      <TabNav :items="tabs" :active="tab" label="Location" />

      <div v-if="tab === 'overview'" class="overview">
        <div class="left">
          <section class="panel" aria-labelledby="subs-title">
            <header id="subs-title" class="panel-header">
              Sub-locations ({{ location.subLocations.length }})
              <Button
                v-if="canWrite"
                label="Sub-location"
                icon="pi pi-plus"
                size="small"
                text
                class="header-action"
                :disabled="location.depth >= 6"
                :title="location.depth >= 6 ? 'Locations nest at most 6 levels deep' : undefined"
                @click="addingSub = true"
              />
            </header>
            <ul v-if="location.subLocations.length" class="subs">
              <li v-for="sub in location.subLocations" :key="sub.id">
                <RouterLink :to="{ name: 'location', params: { id: sub.id } }" class="sub">
                  <StatusBadge
                    :status="sub.state"
                    icon-only
                    :label="sub.hosts.failing ? `${sub.hosts.failing} failing` : undefined"
                  />
                  <span class="name ellipsis">{{ sub.name }}</span>
                  <span class="muted ellipsis">{{ sub.description }}</span>
                  <span class="muted count">{{ sub.hosts.total }} host{{ sub.hosts.total === 1 ? '' : 's' }}</span>
                </RouterLink>
              </li>
            </ul>
            <div v-else class="panel-body muted">None.</div>
          </section>
          <HostsPanel
            :hosts="location.hosts"
            :title="`Hosts (${location.hostCounts.total})`"
            :base="{ id: location.id, path: location.path }"
            :row-menu="canWrite ? hostMenu : null"
            class="hosts"
          >
            <template #actions>
              <Button
                v-if="canWrite"
                label="Add hosts…"
                icon="pi pi-plus"
                size="small"
                text
                @click="addingHosts = true"
              />
            </template>
          </HostsPanel>
        </div>
        <div class="right">
          <section class="panel" aria-label="Information">
            <EmptyState
              v-if="!overviewFields.length"
              icon="pi pi-info-circle"
              title="Nothing on the overview yet."
              :text="session.can('Admin') ? 'Admin → Location fields marks fields for the overview.' : undefined"
            />
            <FieldCard
              v-for="field in overviewFields"
              :key="field.id"
              :field="field"
              :value="valueOf(field.id)"
              :location-id="location.id"
              :can-write="canWrite"
              :users="users"
              :files="files"
              @saved="refresh"
            />
          </section>
          <!-- The knowledge base: the pages linked to the location, at most 10, with All pages… (§3.4, §6.5). -->
        </div>
      </div>

      <div v-else-if="tab === 'information'" class="information">
        <div v-if="canWrite && emptyCount" class="fill-in">
          <Button :label="`Fill in (${emptyCount} empty)`" icon="pi pi-arrow-down" size="small" text @click="fillIn" />
        </div>
        <EmptyState
          v-if="!informationGroups.length"
          icon="pi pi-info-circle"
          title="No information yet."
          class="panel"
        />
        <section
          v-for="group in informationGroups"
          :key="group.group ?? ''"
          class="panel"
          :aria-label="group.group ?? 'Fields'"
        >
          <header v-if="group.group" class="panel-header">{{ group.group }}</header>
          <FieldCard
            v-for="field in group.fields"
            :key="field.id"
            :field="field"
            :value="valueOf(field.id)"
            :location-id="location.id"
            :can-write="canWrite"
            :users="users"
            :files="files"
            @saved="refresh"
          />
        </section>
      </div>

      <div v-else-if="tab === 'pages'" class="pages">
        <!-- The knowledge base's panel for this location (§3.4, §6.5): its pages, then folded those of its ancestors and hosts. -->
        <EmptyState icon="pi pi-book" title="The pages linked to this location appear here." class="panel" />
      </div>

      <LocationHistory v-else-if="tab === 'history'" :location="location" :fields="fields" :can-write="canWrite" />

      <LocationDialog v-model:visible="renaming" :location="location" @saved="refresh" />
      <LocationDialog
        v-model:visible="addingSub"
        :parent-id="location.id"
        @saved="(d) => router.push({ name: 'location', params: { id: d.id } })"
      />
      <MoveDialog v-model:visible="moving" :location="location" @moved="refresh" />
      <AddHostsDialog v-model:visible="addingHosts" :location="location" />
    </template>
  </div>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.description {
  max-width: 32rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.overview {
  display: grid;
  grid-template-columns: minmax(0, 7fr) minmax(0, 5fr);
  gap: var(--pnmt-space-3);
  align-items: start;
}

.left,
.right,
.information,
.pages {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  min-width: 0;
}

.header-action {
  margin-left: auto;
}

.subs {
  list-style: none;
  margin: 0;
  padding: var(--pnmt-space-1) 0;
}

.sub {
  display: grid;
  grid-template-columns: 1.25rem minmax(5rem, auto) minmax(0, 1fr) auto;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-height: 28px;
  padding: 0 var(--pnmt-space-3);
  color: var(--pnmt-text);
}

.sub:hover {
  background: var(--p-content-hover-background);
  text-decoration: none;
}

.sub .name {
  font-weight: 500;
}

.count {
  white-space: nowrap;
  font-size: 0.929rem;
}

.hosts {
  max-height: 70vh;
}

.fill-in {
  display: flex;
  justify-content: flex-end;
}

/* On a phone, the information first: that is what one opens a location for on site (§3.4). */
@media (max-width: 959px) {
  .overview {
    grid-template-columns: minmax(0, 1fr);
  }

  .right {
    order: -1;
  }
}

@media print {
  .overview {
    grid-template-columns: minmax(0, 1fr);
  }

  .hosts {
    max-height: none;
  }
}
</style>
