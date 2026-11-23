<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { boardKeys, boardsApi, type BoardItem } from '@/api/boards'
import { ApiError } from '@/api/client'
import { useNotify } from '@/composables/useNotify'

/**
 * A new board (docs/boards-plan.md §10.2): its name, its columns, and what it starts from: empty, the starting layout (the sensor
 * summary, failing sensors and a clock), or a copy of a board. Then its page.
 */
const props = defineProps<{ boards: BoardItem[] }>()
const visible = defineModel<boolean>('visible', { required: true })
const router = useRouter()
const queryClient = useQueryClient()
const notify = useNotify()

const name = ref('')
const columns = ref(3)
const from = ref<'start' | 'empty' | 'copy'>('start')
const copyOf = ref<string | null>(null)
const errors = ref<Record<string, string | undefined>>({})
const saving = ref(false)
watch(visible, (open) => {
  if (open) {
    name.value = ''
    columns.value = 3
    from.value = 'start'
    copyOf.value = null
    errors.value = {}
  }
})
const starts = computed(() => [
  { value: 'start', label: 'The starting layout' },
  { value: 'empty', label: 'Empty' },
  ...(props.boards.length ? [{ value: 'copy', label: 'A copy of a board' }] : []),
])

async function create() {
  errors.value = {
    name: name.value.trim() ? undefined : 'A name is needed.',
    copyOf: from.value === 'copy' && !copyOf.value ? 'Which board?' : undefined,
  }
  if (errors.value.name || errors.value.copyOf) return
  saving.value = true
  try {
    const created = await boardsApi.create({
      name: name.value.trim(),
      columns: columns.value,
      from: from.value,
      copyOf: copyOf.value,
    })
    await queryClient.invalidateQueries({ queryKey: boardKeys.all })
    visible.value = false
    await router.push({ name: 'board', params: { id: created.id } })
  } catch (e) {
    if (e instanceof ApiError && (Object.keys(e.errors).length || e.code === 'duplicate_name')) {
      errors.value = {
        name: e.field('name') ?? (e.code === 'duplicate_name' ? e.message : undefined),
        copyOf: e.field('copyOf'),
      }
    } else {
      notify.error(e, 'Not created')
    }
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" header="New board" modal :style="{ width: '32rem' }">
    <form id="new-board" class="form" novalidate @submit.prevent="create">
      <div class="field">
        <label for="nb-name">Name</label>
        <InputText id="nb-name" v-model="name" autofocus :invalid="!!errors.name" placeholder="NOC wall" />
        <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
      </div>
      <div class="field">
        <label for="nb-from">Start from</label>
        <Select id="nb-from" v-model="from" :options="starts" option-label="label" option-value="value" />
      </div>
      <div v-if="from === 'copy'" class="field">
        <label for="nb-copy">Board</label>
        <Select
          id="nb-copy"
          v-model="copyOf"
          :options="boards"
          option-label="name"
          option-value="id"
          placeholder="Choose a board"
          :invalid="!!errors.copyOf"
        />
        <small v-if="errors.copyOf" class="field-error">{{ errors.copyOf }}</small>
      </div>
      <div v-else class="field">
        <label>Columns</label>
        <SelectButton v-model="columns" :options="[1, 2, 3, 4]" :allow-empty="false" aria-label="Columns" />
      </div>
      <small class="field-hint"
        >It gets a URL of its own, opened from anywhere until you set networks on its Access tab.</small
      >
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button type="submit" form="new-board" label="Create board" :loading="saving" data-test="create-board" />
    </template>
  </Dialog>
</template>
