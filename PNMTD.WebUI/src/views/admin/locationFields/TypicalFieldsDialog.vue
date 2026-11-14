<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { typeIcon, typeLabel } from '@/views/locations/fieldValues'

/**
 * *Add typical fields…* (docs/locations-knowledge-plan.md §4.4): a checklist of fields many teams keep per location, each
 * created as a normal field that can be changed afterwards. Those whose key exists already are shown as added.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ typical: S['LocationFieldTemplate'][] }>()

const picked = ref<Record<string, boolean>>({})
watch(visible, (open) => open && (picked.value = {}))
const chosen = computed(() => props.typical.filter((t) => !t.exists && picked.value[t.key]))

const { invalidate, notify, busy } = useActions()
async function add() {
  busy.value = true
  let added = 0
  try {
    for (const t of chosen.value) {
      await api.locationFields.create({
        label: t.label,
        type: t.type,
        help: t.help,
        overview: t.overview,
        inherited: t.inherited,
        sensitive: t.sensitive,
        options: null,
      })
      added++
    }
    notify.success(`${added} field${added === 1 ? '' : 's'} added`)
    visible.value = false
  } catch (e) {
    notify.error(e, added ? `${added} added, then it failed` : 'Not added')
  } finally {
    busy.value = false
    await invalidate([keys.locationFields, keys.locations])
  }
}

const traits = (t: S['LocationFieldTemplate']) =>
  [typeLabel(t.type), t.overview && 'on the overview', t.inherited && 'inherited', t.sensitive && 'sensitive']
    .filter(Boolean)
    .join(' · ')
</script>

<template>
  <Dialog
    v-model:visible="visible"
    header="Add typical fields"
    modal
    :style="{ width: '34rem' }"
    :breakpoints="{ '599px': '95vw' }"
  >
    <p class="muted intro">Each is a normal field afterwards: rename it, change it, or archive it.</p>
    <ul class="typical">
      <li v-for="t in typical" :key="t.key" :class="{ exists: t.exists }">
        <Checkbox v-model="picked[t.key]" binary :input-id="`typical-${t.key}`" :disabled="t.exists" />
        <label :for="`typical-${t.key}`">
          <i :class="typeIcon(t.type)" class="muted" aria-hidden="true" />
          <span class="strong">{{ t.label }}</span>
          <span class="muted small">{{ t.exists ? 'added already' : traits(t) }}</span>
        </label>
      </li>
    </ul>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        :label="chosen.length ? `Add ${chosen.length} field${chosen.length === 1 ? '' : 's'}` : 'Add'"
        :disabled="!chosen.length"
        :loading="busy"
        @click="add"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.intro {
  margin-top: 0;
}

.typical {
  list-style: none;
  margin: 0;
  padding: 0;
}

.typical li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-1) 0;
}

.typical li.exists label {
  opacity: 0.6;
}

.typical label {
  display: flex;
  align-items: baseline;
  gap: var(--pnmt-space-2);
  flex: 1;
  min-width: 0;
  cursor: pointer;
}

.strong {
  font-weight: 500;
}

.small {
  font-size: 0.857rem;
}
</style>
