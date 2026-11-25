<script setup lang="ts">
import Select from 'primevue/select'
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useSession } from '@/stores/session'
import LinksTable from './LinksTable.vue'
import { linksFromQuery, linksToQuery, type LinksState, type LinksView } from './linksState'
import UnlinkedTable from './UnlinkedTable.vue'

/**
 * The Links tab (docs/responsibilities-extensions-plan.md §5): every link between a responsibility and a page, location or host, or
 * the hosts and locations no responsibility links itself. The view and its filters are in the URL. With Edit, many links change
 * at once. Hosts and locations without a responsibility need the right to list them (Monitoring: View, Locations: View) or Edit.
 */
const props = defineProps<{ edit: boolean }>()
const route = useRoute()
const router = useRouter()
const session = useSession()

const choices = computed(() => [
  { label: 'Links', value: 'links' as LinksView },
  ...(props.edit || session.can('Monitoring', 'View')
    ? [{ label: 'Hosts without a responsibility', value: 'hosts' as LinksView }]
    : []),
  ...(props.edit || session.can('Locations', 'View')
    ? [{ label: 'Locations without a responsibility', value: 'locations' as LinksView }]
    : []),
])
const initial = linksFromQuery(route.query)
const state = ref<LinksState>(
  choices.value.some((c) => c.value === initial.show) ? initial : { ...initial, show: 'links' },
)
const shown = ref<LinksView>(state.value.show)

/** Another view: its table starts on page 1 with its own sort, so the URL starts over before it is shown. */
async function choose(view: LinksView) {
  state.value = { ...state.value, show: view }
  const query = Object.fromEntries(
    Object.entries(linksToQuery(state.value)).filter((e): e is [string, string] => e[1] !== undefined),
  )
  await router.replace({ query })
  shown.value = view
}
</script>

<template>
  <div class="links-tab">
    <div v-if="choices.length > 1" class="show">
      <span class="muted">Show</span>
      <Select
        :model-value="state.show"
        :options="choices"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Show"
        data-test="links-show"
        @update:model-value="choose"
      />
    </div>
    <LinksTable v-if="shown === 'links'" v-model:state="state" :edit="edit" />
    <UnlinkedTable
      v-else
      :key="shown"
      v-model:state="state"
      :kind="shown === 'hosts' ? 'Host' : 'Location'"
      :edit="edit"
    />
  </div>
</template>

<style scoped>
.show {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  margin-bottom: var(--pnmt-space-3);
}
</style>
