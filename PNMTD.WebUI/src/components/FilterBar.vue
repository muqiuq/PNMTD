<script setup lang="ts">
import Button from 'primevue/button'
import IconField from 'primevue/iconfield'
import InputIcon from 'primevue/inputicon'
import InputText from 'primevue/inputtext'
import { ref } from 'vue'
import { useMedia } from '@/composables/useMedia'

/** Search plus filters (§5.3); on a phone the filters fold into a button. */
const search = defineModel<string>('search', { default: '' })
defineProps<{ placeholder?: string; active?: number }>()
const phone = useMedia('(max-width: 599px)')
const open = ref(false)
</script>

<template>
  <div class="filter-bar">
    <IconField class="search">
      <InputIcon class="pi pi-search" />
      <InputText
        v-model="search"
        size="small"
        :placeholder="placeholder ?? 'Search…'"
        :aria-label="placeholder ?? 'Search'"
        fluid
      />
    </IconField>
    <template v-if="$slots.default">
      <Button
        v-if="phone"
        :label="active ? `Filters (${active})` : 'Filters'"
        icon="pi pi-filter"
        size="small"
        severity="secondary"
        outlined
        :aria-expanded="open"
        @click="open = !open"
      />
      <div v-if="!phone || open" class="filters"><slot /></div>
    </template>
    <div v-if="$slots.end" class="end"><slot name="end" /></div>
  </div>
</template>

<style scoped>
.filter-bar {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.search {
  width: 16rem;
}

.filters {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.end {
  margin-left: auto;
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

@media (max-width: 599px) {
  .search {
    flex: 1 1 auto;
    width: auto;
  }

  .filters {
    flex-basis: 100%;
  }
}
</style>
