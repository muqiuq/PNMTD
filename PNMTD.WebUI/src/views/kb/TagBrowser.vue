<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import SelectButton from 'primevue/selectbutton'
import { computed, ref } from 'vue'
import { kbApi, kbKeys } from '@/api/kb'

/**
 * The tag browser at the side of the list (docs/locations-knowledge-plan.md §6.6): every tag with its pages, by count or
 * by name. A click adds the tag to the filter, or takes it out.
 */
const props = defineProps<{ selected: string[] }>()
const emit = defineEmits<{ toggle: [tag: string] }>()

const tags = useQuery({ queryKey: kbKeys.tags, queryFn: () => kbApi.tags(), staleTime: 60_000 })
const order = ref<'count' | 'name'>('count')
const orders = [
  { label: 'By count', value: 'count' },
  { label: 'By name', value: 'name' },
]
const all = ref(false)
const Shown = 40
const sorted = computed(() => {
  const list = (tags.data.value ?? []).filter((t) => t.pages > 0 || props.selected.includes(t.name))
  return order.value === 'name' ? [...list].sort((a, b) => a.name.localeCompare(b.name)) : list
})
const visible = computed(() => (all.value ? sorted.value : sorted.value.slice(0, Shown)))
</script>

<template>
  <section class="panel tag-browser" aria-labelledby="tag-browser-title">
    <div id="tag-browser-title" class="panel-header">
      Tags
      <SelectButton
        v-model="order"
        :options="orders"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Order of the tags"
        class="order"
      />
    </div>
    <div class="panel-body">
      <p v-if="tags.isSuccess.value && !sorted.length" class="muted">No tags yet.</p>
      <ul class="tags">
        <li v-for="tag in visible" :key="tag.id">
          <button
            type="button"
            class="tag"
            :class="{ on: selected.includes(tag.name) }"
            :aria-pressed="selected.includes(tag.name)"
            @click="emit('toggle', tag.name)"
          >
            <span>{{ tag.name }}</span>
            <span class="count">{{ tag.pages }}</span>
          </button>
        </li>
      </ul>
      <Button
        v-if="sorted.length > Shown"
        :label="all ? 'Fewer' : `All ${sorted.length} tags`"
        text
        size="small"
        @click="all = !all"
      />
    </div>
  </section>
</template>

<style scoped>
.panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--pnmt-space-2);
}

.order :deep(.p-togglebutton) {
  padding: 0.1rem 0.5rem;
  font-size: 0.786rem;
}

.tags {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
}

.tag {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  border: 1px solid var(--pnmt-border);
  border-radius: 999px;
  background: var(--pnmt-panel);
  color: var(--pnmt-text);
  padding: 0.1rem 0.55rem;
  font: inherit;
  font-size: 0.857rem;
  cursor: pointer;
}

.tag:hover {
  border-color: var(--p-primary-color);
}

.tag.on {
  background: var(--p-primary-color);
  border-color: var(--p-primary-color);
  color: var(--p-primary-contrast-color);
}

.count {
  font-variant-numeric: tabular-nums;
  opacity: 0.7;
}
</style>
