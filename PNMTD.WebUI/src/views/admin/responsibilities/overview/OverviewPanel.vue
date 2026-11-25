<script setup lang="ts">
import Button from 'primevue/button'
import { computed, defineComponent, ref, watch } from 'vue'
import { kbLinkIcon, kbLinkRoute } from '@/api/kb'
import {
  personLabel,
  responsibilitiesApi,
  responsibilityKeys,
  standsInLabel,
  type ResponsibilityMap,
} from '@/api/responsibilities'
import { noteNodes } from '@/boards/boardMarkdown'
import ColourDot from '@/components/ColourDot.vue'
import { isColour } from '@/components/colours'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import ColourPicker from '../ColourPicker.vue'
import { nodeId } from './overviewLayout'

/**
 * The overview's side panel (docs/responsibilities-extensions-plan.md §6.4): a responsibility with its people, counts and place, its
 * colour saved at once and *Back to automatic* (Edit); a person with what they are responsible or deputy for; a linked object with
 * the responsibilities that link it, each unlinked here with Edit. No contact details.
 */
const props = defineProps<{ selection: string; map: ResponsibilityMap; edit: boolean }>()
const emit = defineEmits<{
  close: []
  select: [id: string]
  automatic: [id: string]
  'edit-note': [id: string]
  'delete-note': [id: string]
}>()
const { run } = useActions()

const kind = computed(() => props.selection.slice(0, props.selection.indexOf(':')))
const id = computed(() => props.selection.slice(props.selection.indexOf(':') + 1))
const people = computed(() => new Map(props.map.people.map((p) => [p.id, p])))
const byId = computed(() => new Map(props.map.responsibilities.map((r) => [r.id, r])))
const person = (personId: string | null | undefined) => {
  const p = personId ? people.value.get(personId) : undefined
  return p ? { id: p.id, name: p.name, active: p.active } : null
}

// --- a responsibility -------------------------------------------------------------------------------------------------
const r = computed(() => (kind.value === 'responsibility' ? byId.value.get(id.value) : undefined))
const colour = ref<string | null>(null)
watch(
  r,
  (value) => {
    colour.value = value?.color ?? null
  },
  { immediate: true },
)
// Saved at once (§4.2): a swatch, none, or a custom colour once it is a whole #rrggbb.
watch(colour, async (value) => {
  const current = r.value
  if (!current || !props.edit || value === (current.color ?? null) || (value !== null && !isColour(value))) return
  await run(() => responsibilitiesApi.setColor(current.id, value), {
    success: `${current.name}: colour saved`,
    failure: 'Colour not saved',
    invalidate: [responsibilityKeys.all],
  })
})

// --- a person ---------------------------------------------------------------------------------------------------------
const p = computed(() => (kind.value === 'person' ? people.value.get(id.value) : undefined))
const theirs = computed(() => {
  const who = id.value
  const global = props.map.global
  return props.map.responsibilities
    .map((x) => ({
      r: x,
      as:
        x.responsibleId === who
          ? 'responsible'
          : x.deputyId === who
            ? 'deputy'
            : x.globalStandsIn && (global.responsible?.id === who || global.deputy?.id === who)
              ? 'global, standing in'
              : null,
    }))
    .filter((x) => x.as)
})

// --- a note (§7) ------------------------------------------------------------------------------------------------------
const note = computed(() => (kind.value === 'note' ? props.map.notes.find((n) => n.id === id.value) : undefined))
const NoteText = defineComponent({
  props: { markdown: { type: String, required: true } },
  setup: (p) => () => noteNodes(p.markdown),
})

// --- a linked object --------------------------------------------------------------------------------------------------
const objectKinds = { host: 'Host', location: 'Location', page: 'Page' } as const
const o = computed(() => {
  const k = objectKinds[kind.value as keyof typeof objectKinds]
  return k ? props.map.objects.find((x) => x.kind === k && x.id === id.value) : undefined
})
const objectName = computed(() =>
  o.value
    ? o.value.kind === 'Page' && o.value.number != null
      ? `KB-${o.value.number} ${o.value.name}`
      : o.value.name
    : '',
)
const unlink = (responsibilityId: string, linkId: string) =>
  run(() => responsibilitiesApi.unlink(responsibilityId, linkId), {
    success: `${objectName.value} unlinked from ${byId.value.get(responsibilityId)?.name ?? 'the responsibility'}`,
    failure: 'Not unlinked',
    invalidate: [responsibilityKeys.all],
  })
</script>

<template>
  <div class="overview-panel">
    <header class="head">
      <div class="title">
        <h2 v-if="r"><ColourDot :colour="r.color" size="0.85rem" /> {{ r.name }}</h2>
        <h2 v-else-if="p">{{ p.name }}</h2>
        <h2 v-else-if="o"><i :class="kbLinkIcon[o.kind]" class="muted" aria-hidden="true" /> {{ objectName }}</h2>
        <h2 v-else-if="note">Note</h2>
        <h2 v-else>Not on the map any more</h2>
        <div v-if="r?.summary" class="muted sub">{{ r.summary }}</div>
        <div v-else-if="p" class="muted sub">{{ p.active ? 'Person' : 'Person, inactive' }}</div>
        <div v-else-if="o" class="muted sub">{{ o.kind }}</div>
      </div>
      <Button icon="pi pi-times" text rounded severity="secondary" aria-label="Close" @click="emit('close')" />
    </header>

    <div v-if="r" class="body">
      <div class="actions">
        <RouterLink :to="{ name: 'responsibility', params: { id: r.id } }" class="button-link">
          <Button label="Open" icon="pi pi-external-link" size="small" severity="secondary" text />
        </RouterLink>
        <RouterLink
          :to="{ name: 'responsibilities', params: { tab: 'links' }, query: { r: r.id } }"
          class="button-link"
        >
          <Button label="Links" icon="pi pi-link" size="small" severity="secondary" text />
        </RouterLink>
      </div>
      <dl class="facts">
        <template v-if="r.globalStandsIn">
          <dt>People</dt>
          <dd class="warn">{{ standsInLabel(map.global) }}</dd>
        </template>
        <template v-else>
          <dt>Responsible</dt>
          <dd>
            <a v-if="r.responsibleId" href="#" @click.prevent="emit('select', nodeId('person', r.responsibleId))">{{
              personLabel(person(r.responsibleId))
            }}</a>
            <span v-else>Nobody</span>
          </dd>
          <dt>Deputy</dt>
          <dd>
            <a v-if="r.deputyId" href="#" @click.prevent="emit('select', nodeId('person', r.deputyId))">{{
              personLabel(person(r.deputyId))
            }}</a>
            <span v-else>Nobody</span>
          </dd>
        </template>
        <dt>Links</dt>
        <dd>{{ r.links }}</dd>
        <dt>Redmine</dt>
        <dd>{{ r.combinations }}</dd>
        <dt>Place</dt>
        <dd data-test="overview-place">
          <template v-if="r.position">
            Saved · {{ r.position.updatedByName ?? 'someone' }}, <RelativeTime :value="r.position.updatedAt" />
            <Button
              v-if="edit"
              label="Back to automatic"
              size="small"
              text
              data-test="overview-automatic"
              @click="emit('automatic', r.id)"
            />
          </template>
          <template v-else>Automatic</template>
        </dd>
      </dl>
      <p v-if="r.attention" class="warn small">
        <i class="pi pi-exclamation-triangle" aria-hidden="true" /> Needs attention: nobody set, a person inactive, or a
        Redmine combination nobody handles.
      </p>
      <div v-if="edit" class="field">
        <label for="overview-colour">Colour</label>
        <ColourPicker v-model="colour" input-id="overview-colour" />
      </div>
    </div>

    <div v-else-if="p" class="body">
      <p v-if="!theirs.length" class="muted">Names no responsibility shown.</p>
      <ul v-else class="list">
        <li v-for="t in theirs" :key="t.r.id">
          <ColourDot :colour="t.r.color" />
          <a href="#" @click.prevent="emit('select', nodeId('responsibility', t.r.id))">{{ t.r.name }}</a>
          <span class="muted small">{{ t.as }}</span>
        </li>
      </ul>
    </div>

    <div v-else-if="note" class="body">
      <div v-if="edit" class="actions">
        <Button
          label="Edit"
          icon="pi pi-pencil"
          size="small"
          data-test="note-edit"
          @click="emit('edit-note', note.id)"
        />
        <Button
          label="Delete…"
          icon="pi pi-trash"
          size="small"
          severity="danger"
          text
          data-test="note-delete"
          @click="emit('delete-note', note.id)"
        />
      </div>
      <div class="note-text" :class="`note-${note.colour.toLowerCase()}`"><NoteText :markdown="note.text" /></div>
      <p class="muted small">
        Written by {{ note.createdByName ?? 'someone' }}, <RelativeTime :value="note.createdAt" />
        <template v-if="note.updatedAt !== note.createdAt">
          · changed by {{ note.updatedByName ?? 'someone' }}, <RelativeTime :value="note.updatedAt"
        /></template>
      </p>
    </div>

    <div v-else-if="o" class="body">
      <div class="actions">
        <RouterLink :to="kbLinkRoute({ kind: o.kind, id: o.id, number: o.number })" class="button-link">
          <Button label="Open" icon="pi pi-external-link" size="small" severity="secondary" text />
        </RouterLink>
      </div>
      <ul class="list">
        <li v-for="l in o.links" :key="l.linkId">
          <ColourDot :colour="byId.get(l.responsibilityId)?.color" />
          <a href="#" @click.prevent="emit('select', nodeId('responsibility', l.responsibilityId))">{{
            byId.get(l.responsibilityId)?.name
          }}</a>
          <Button
            v-if="edit"
            icon="pi pi-times"
            text
            rounded
            size="small"
            severity="secondary"
            :aria-label="`Unlink from ${byId.get(l.responsibilityId)?.name}`"
            @click="unlink(l.responsibilityId, l.linkId)"
          />
        </li>
      </ul>
    </div>
  </div>
</template>

<style scoped>
.overview-panel {
  display: flex;
  flex-direction: column;
  height: 100%;
}

.head {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-3);
  padding: var(--pnmt-space-4) var(--pnmt-space-3) var(--pnmt-space-3) var(--pnmt-space-4);
  border-bottom: 1px solid var(--pnmt-border);
}

.title {
  flex: 1;
  min-width: 0;
}

.title h2 {
  margin: 0;
  font-size: 1.15rem;
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  overflow-wrap: anywhere;
}

.sub {
  margin-top: var(--pnmt-space-1);
  font-size: 0.857rem;
}

.body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-4);
  padding: var(--pnmt-space-4);
}

.actions {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
}

.button-link:hover {
  text-decoration: none;
}

.facts {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: var(--pnmt-space-2) var(--pnmt-space-3);
  margin: 0;
  align-items: baseline;
}

.facts dt {
  color: var(--pnmt-muted);
}

.facts dd {
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

.list li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.warn {
  color: var(--pnmt-warn);
}

.small {
  font-size: 0.857rem;
}

.note-text {
  padding: var(--pnmt-space-3);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-note-yellow);
  overflow-wrap: anywhere;
}

.note-text.note-green {
  background: var(--pnmt-note-green);
}

.note-text.note-blue {
  background: var(--pnmt-note-blue);
}

.note-text.note-pink {
  background: var(--pnmt-note-pink);
}

.note-text.note-purple {
  background: var(--pnmt-note-purple);
}

.note-text.note-grey {
  background: var(--pnmt-note-grey);
}

.note-text :deep(p:first-child) {
  margin-top: 0;
}

.note-text :deep(p:last-child) {
  margin-bottom: 0;
}

.field {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}
</style>
