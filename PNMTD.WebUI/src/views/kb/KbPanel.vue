<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { kbApi, kbKeys } from '@/api/kb'
import { useSession } from '@/stores/session'
import KbPageRow from './KbPageRow.vue'

/**
 * The Knowledge base panel of a location, a host or a sensor (docs/locations-knowledge-plan.md §6.5): the pages linked
 * to it, then, folded, those linked to what it belongs to (via its host, its location and that one's parents; a
 * location's via its parents and its hosts). Archived pages are left out. *New page* starts one linked to it.
 * `bare`: without its own panel frame and title, for a page that frames it (a location's tab); `ownOnly`: without the
 * pages it inherits.
 */
const props = withDefaults(
  defineProps<{
    locationId?: string
    hostId?: string
    sensorId?: string
    /** For the list's filter chip: "pages linked to fw-zrh-01". */
    name?: string
    /** At most this many of its own; the rest behind *All pages…*. */
    limit?: number
    bare?: boolean
    /** Only its own pages: a location's overview, whose tab has the rest. */
    ownOnly?: boolean
  }>(),
  {
    locationId: undefined,
    hostId: undefined,
    sensorId: undefined,
    name: undefined,
    limit: 10,
    bare: false,
    ownOnly: false,
  },
)

const router = useRouter()
const session = useSession()
const query = computed(() => ({ locationId: props.locationId, hostId: props.hostId, sensorId: props.sensorId }))
const related = useQuery({
  queryKey: computed(() => kbKeys.related(query.value)),
  queryFn: () => kbApi.related(query.value),
  refetchInterval: 60_000,
})
const own = computed(() => related.data.value?.pages ?? [])
const via = computed(() => (props.ownOnly ? [] : (related.data.value?.via ?? [])))
const open = ref<Record<number, boolean>>({})

const target = computed(() =>
  props.locationId
    ? { kind: 'location', id: props.locationId, filter: { location: props.locationId } }
    : props.hostId
      ? { kind: 'host', id: props.hostId, filter: { host: props.hostId } }
      : { kind: 'sensor', id: props.sensorId!, filter: { sensor: props.sensorId! } },
)
const allPages = computed(() => ({
  name: 'kb',
  query: { ...target.value.filter, ...(props.name ? { linkedName: props.name } : {}) },
}))

function newPage() {
  void router.push({ name: 'kb-new', query: { link: `${target.value.kind}:${target.value.id}` } })
}

defineExpose({ count: computed(() => own.value.length) })
</script>

<template>
  <section :class="bare ? 'kb-panel bare' : 'kb-panel panel'" aria-label="Knowledge base">
    <div v-if="!bare" class="panel-header">
      <i class="pi pi-book muted" aria-hidden="true" />
      Knowledge base
      <span v-if="own.length" class="muted count">{{ own.length }}</span>
      <Button
        v-if="session.can('KnowledgeBase', 'Edit')"
        label="New page"
        icon="pi pi-plus"
        size="small"
        text
        class="new"
        @click="newPage"
      />
    </div>
    <div :class="{ 'panel-body': !bare }">
      <div v-if="bare && session.can('KnowledgeBase', 'Edit')" class="bare-actions">
        <Button label="New page" icon="pi pi-plus" size="small" @click="newPage" />
      </div>
      <p v-if="related.isSuccess.value && !own.length && !via.length" class="muted none">No pages linked yet.</p>
      <ul v-if="own.length" class="pages">
        <li v-for="p in own.slice(0, limit)" :key="p.id"><KbPageRow :page="p" /></li>
      </ul>
      <RouterLink v-if="own.length > limit" :to="allPages" class="all">All {{ own.length }} pages…</RouterLink>
      <RouterLink
        v-if="own.length || via.length"
        :to="{ name: 'kb-map', query: { focus: `${target.kind}:${target.id}` } }"
        class="all map-link"
        ><i class="pi pi-sitemap" aria-hidden="true" /> Show on the knowledge map</RouterLink
      >
      <div v-for="(group, i) in via" :key="`${group.kind}-${group.id ?? group.name}`" class="via">
        <button type="button" class="via-toggle" :aria-expanded="!!open[i]" @click="open[i] = !open[i]">
          <i :class="open[i] ? 'pi pi-chevron-down' : 'pi pi-chevron-right'" aria-hidden="true" />
          <span>via {{ group.name }}</span>
          <span class="muted">{{ group.pages.length }}</span>
        </button>
        <ul v-if="open[i]" class="pages">
          <li v-for="p in group.pages" :key="p.id"><KbPageRow :page="p" /></li>
        </ul>
      </div>
    </div>
  </section>
</template>

<style scoped>
.panel-header .new {
  margin-left: auto;
}

.count {
  font-weight: 400;
}

.bare-actions {
  margin-bottom: var(--pnmt-space-2);
}

.none {
  margin: 0;
}

.pages {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.all {
  display: inline-block;
  margin-top: var(--pnmt-space-2);
}

.map-link {
  display: block;
}

.via {
  margin-top: var(--pnmt-space-2);
}

.via-toggle {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  border: none;
  background: none;
  padding: 0;
  font: inherit;
  color: var(--pnmt-text);
  cursor: pointer;
}

.via-toggle .pi {
  font-size: 0.75rem;
  color: var(--pnmt-muted);
}

.via .pages {
  margin: var(--pnmt-space-1) 0 0 1.25rem;
}
</style>
