<script setup lang="ts">
import SelectButton from 'primevue/selectbutton'
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import TemplateCategories from './TemplateCategories.vue'
import TemplateList from './TemplateList.vue'
import TemplateNames from './TemplateNames.vue'

/**
 * The *Templates* tab (docs/licences-plan.md §8.6): three views in the URL (?show=templates|categories|names): the templates, the
 * categories in their order, and the manufacturers, vendors, cost centres and seat units in use. Everyone with View reads them;
 * changing them is Manage; making an item from a template is Edit.
 */
defineProps<{ manage: boolean; edit: boolean }>()
const route = useRoute()
const router = useRouter()

type Show = 'templates' | 'categories' | 'names'
const views: { value: Show; label: string }[] = [
  { value: 'templates', label: 'Templates' },
  { value: 'categories', label: 'Categories' },
  { value: 'names', label: 'Names' },
]
const show = computed<Show>({
  get: () => views.find((v) => v.value === route.query.show)?.value ?? 'templates',
  // Each view keeps its own search and sort: a new view starts without them.
  set: (value) => void router.replace({ query: value === 'templates' ? {} : { show: value } }),
})
</script>

<template>
  <div class="templates">
    <SelectButton
      v-model="show"
      :options="views"
      option-label="label"
      option-value="value"
      :allow-empty="false"
      size="small"
      aria-label="Show"
      data-test="templates-show"
    />
    <TemplateList v-if="show === 'templates'" :manage="manage" :edit="edit" />
    <TemplateCategories v-else-if="show === 'categories'" :manage="manage" />
    <TemplateNames v-else :manage="manage" />
  </div>
</template>

<style scoped>
.templates {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}
</style>
