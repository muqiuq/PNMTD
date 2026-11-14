<script setup lang="ts">
import AutoComplete from 'primevue/autocomplete'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api/resources'
import { ApiError } from '@/api/client'
import { typeIcon } from '@/composables/useLookups'
import { useSession } from '@/stores/session'

/**
 * The global search (§5.4), Ctrl/⌘ K: hosts and sensors by name; for User and Admin also a sensor's
 * exact write or read token, which replaces v0.12's token box on the dashboard. Locations by name
 * with their paths, and pages by number, title, summary and tag; #tag searches tags only
 * (docs/locations-knowledge-plan.md §6.7, §10.4).
 */
interface Suggestion {
  kind: 'host' | 'sensor' | 'token' | 'location' | 'page'
  id: string
  label: string
  detail: string
  icon: string
  /** A page's number: its route is /kb/42. */
  number?: number
}

const router = useRouter()
const session = useSession()
const text = ref('')
const suggestions = ref<Suggestion[]>([])
const field = ref<{ $el?: HTMLElement } | null>(null)
const isMac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform)
const shortcut = computed(() => (isMac ? '⌘K' : 'Ctrl K'))

async function complete(event: { query: string }) {
  const q = event.query.trim()
  if (!q) {
    suggestions.value = []
    return
  }
  const found: Suggestion[] = []
  const lookup =
    session.can('Monitoring', 'Edit') && q.length >= 8 && !q.includes(' ') && !q.startsWith('#')
      ? api.sensors
          .lookup(q)
          .catch((e: unknown) => (e instanceof ApiError && e.status === 404 ? null : Promise.reject(e)))
      : Promise.resolve(null)
  const [result, token] = await Promise.all([api.search(q, 8), lookup])
  if (token) {
    found.push({
      kind: 'token',
      id: token.sensorId,
      label: `${token.host.name} / ${token.sensorName}`,
      detail: `${token.matched} token`,
      icon: 'pi pi-key',
    })
  }
  found.push(
    ...result.hosts.map((h) => ({
      kind: 'host' as const,
      id: h.id,
      label: h.name,
      detail: h.location ?? (h.enabled ? 'host' : 'host, disabled'),
      icon: 'pi pi-server',
    })),
    ...result.sensors.map((s) => ({
      kind: 'sensor' as const,
      id: s.id,
      label: s.name,
      detail: s.hostName,
      icon: typeIcon(s.type),
    })),
    ...result.locations.map((l) => ({
      kind: 'location' as const,
      id: l.id,
      label: l.name,
      detail: l.path === l.name ? 'location' : l.path,
      icon: 'pi pi-map-marker',
    })),
    ...result.pages.map((p) => ({
      kind: 'page' as const,
      id: p.id,
      number: p.number,
      label: `KB-${p.number} · ${p.title}`,
      detail: p.tags.length ? p.tags.map((t) => `#${t}`).join(' ') : 'page',
      icon: p.sensitive ? 'pi pi-lock' : 'pi pi-book',
    })),
  )
  suggestions.value = found
}

async function open(event: { value: Suggestion }) {
  const item = event.value
  text.value = ''
  await router.push(
    item.kind === 'page'
      ? `/kb/${item.number}`
      : {
          name: item.kind === 'host' ? 'host' : item.kind === 'location' ? 'location' : 'sensor',
          params: { id: item.id },
        },
  )
  ;(document.activeElement as HTMLElement | null)?.blur()
}

function onKey(event: KeyboardEvent) {
  if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
    event.preventDefault()
    field.value?.$el?.querySelector('input')?.focus()
  }
}

onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))
</script>

<template>
  <div class="global-search">
    <AutoComplete
      ref="field"
      v-model="text"
      :suggestions="suggestions"
      option-label="label"
      :placeholder="`Search hosts, sensors, locations and pages (${shortcut})`"
      :delay="200"
      :min-length="1"
      fluid
      size="small"
      input-id="global-search"
      :pt="{ pcInputText: { root: { 'aria-label': 'Search hosts, sensors, locations and pages' } } }"
      @complete="complete"
      @option-select="open"
    >
      <template #option="{ option }">
        <div class="suggestion">
          <i :class="option.icon" aria-hidden="true" />
          <span class="label">{{ option.label }}</span>
          <span class="detail muted">{{ option.detail }}</span>
        </div>
      </template>
      <template #empty><span class="muted">Nothing found.</span></template>
    </AutoComplete>
  </div>
</template>

<style scoped>
.global-search {
  width: 100%;
  max-width: 440px;
}

.suggestion {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.suggestion i {
  color: var(--pnmt-muted);
  width: 1rem;
}

.label {
  font-weight: 500;
}

.detail {
  margin-left: auto;
  font-size: 0.857rem;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 50%;
}
</style>
