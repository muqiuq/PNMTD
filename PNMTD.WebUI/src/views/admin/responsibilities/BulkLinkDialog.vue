<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { kbLinkIcon } from '@/api/kb'
import {
  bulkSummary,
  linkObjectLabel,
  responsibilitiesApi,
  responsibilityKeys,
  type ResponsibilityLinkRow,
} from '@/api/responsibilities'
import ColourDot from '@/components/ColourDot.vue'
import { useNotify } from '@/composables/useNotify'
import LinkPicker, { type LinkTarget } from '@/views/kb/LinkPicker.vue'

/**
 * Many links at once (docs/responsibilities-extensions-plan.md §5.1): *Link…* picks a responsibility and objects; *Link to…* links
 * the hosts or locations selected; *Move to…* and *Also link to…* take the links selected to another responsibility. Those linked
 * there already are skipped and named in the toast. All of a responsibility's links must fit its limit, or nothing is linked.
 */
export type BulkMode = 'link' | 'link-selected' | 'move' | 'copy'
type Option = { value: string; label: string; color: string | null }
type Target = { kind: 'Page' | 'Location' | 'Host'; id: string; name: string }

const props = withDefaults(
  defineProps<{
    mode: BulkMode
    responsibilities: Option[]
    /** move, copy: the links selected. */
    links?: ResponsibilityLinkRow[]
    /** link-selected: the hosts or locations selected. */
    objects?: Target[]
  }>(),
  { links: () => [], objects: () => [] },
)
const visible = defineModel<boolean>('visible', { required: true })
const emit = defineEmits<{ done: [] }>()
const queryClient = useQueryClient()
const notify = useNotify()

const target = ref<string | null>(null)
const picked = ref<Target[]>([])
const saving = ref(false)
watch(visible, (open) => {
  if (open) {
    target.value = null
    picked.value = []
  }
})

const count = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`
const header = computed(() => {
  switch (props.mode) {
    case 'link':
      return 'Link to a responsibility'
    case 'link-selected':
      return `Link ${count(props.objects.length, props.objects[0]?.kind === 'Location' ? 'location' : 'host', props.objects[0]?.kind === 'Location' ? 'locations' : 'hosts')} to…`
    case 'move':
      return `Move ${count(props.links.length, 'link', 'links')} to…`
    default:
      return `Also link ${count(props.links.length, 'object', 'objects')} to…`
  }
})
const explanation = computed(() =>
  props.mode === 'move'
    ? 'They leave the responsibilities they are on now.'
    : props.mode === 'copy'
      ? 'They stay where they are, and are linked to this one too.'
      : null,
)
const ready = computed(() => !!target.value && (props.mode !== 'link' || picked.value.length > 0))
const targetName = computed(() => props.responsibilities.find((r) => r.value === target.value)?.label ?? '')

function pick(t: LinkTarget) {
  if (t.kind === 'Sensor' || picked.value.some((p) => p.kind === t.kind && p.id === t.id)) return
  picked.value = [...picked.value, { kind: t.kind, id: t.id, name: linkObjectLabel(t) }]
}

async function submit() {
  if (!ready.value) return
  saving.value = true
  try {
    const to = target.value!
    const result =
      props.mode === 'move' || props.mode === 'copy'
        ? await responsibilitiesApi.moveLinks(
            props.links.map((l) => l.id),
            to,
            props.mode === 'copy',
          )
        : await responsibilitiesApi.linkMany(
            to,
            (props.mode === 'link' ? picked.value : props.objects).map((o) => ({ kind: o.kind, id: o.id })),
          )
    notify.success(bulkSummary(result, targetName.value, props.mode === 'move'))
    await queryClient.invalidateQueries({ queryKey: responsibilityKeys.all })
    visible.value = false
    emit('done')
  } catch (e) {
    notify.error(e, props.mode === 'move' ? 'Not moved' : 'Not linked')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="header" modal :style="{ width: '32rem' }">
    <form id="bulk-link" class="form" novalidate @submit.prevent="submit">
      <div class="field">
        <label for="bulk-target">Responsibility</label>
        <Select
          v-model="target"
          input-id="bulk-target"
          :options="responsibilities"
          option-label="label"
          option-value="value"
          filter
          placeholder="Choose a responsibility"
          data-test="bulk-target"
        >
          <template #option="{ option }">
            <span class="option"><ColourDot :colour="option.color" />{{ option.label }}</span>
          </template>
        </Select>
        <small v-if="explanation" class="field-hint">{{ explanation }}</small>
      </div>
      <div v-if="mode === 'link'" class="field">
        <span class="label">Pages, locations and hosts</span>
        <ul v-if="picked.length" class="chips">
          <li v-for="p in picked" :key="`${p.kind}:${p.id}`">
            <i :class="kbLinkIcon[p.kind]" class="muted" aria-hidden="true" />
            <span>{{ p.name }}</span>
            <Button
              icon="pi pi-times"
              text
              rounded
              size="small"
              severity="secondary"
              :aria-label="`Leave out ${p.name}`"
              @click="picked = picked.filter((x) => x !== p)"
            />
          </li>
        </ul>
        <LinkPicker
          :kinds="['Page', 'Location', 'Host']"
          :exclude="picked"
          :search="responsibilitiesApi.targets"
          @pick="pick"
        />
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="bulk-link"
        :label="mode === 'move' ? 'Move' : 'Link'"
        :disabled="!ready"
        :loading="saving"
        data-test="bulk-submit"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.option {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.label {
  font-weight: 600;
}

.chips {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.chips li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
