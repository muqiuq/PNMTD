<script setup lang="ts">
import Tab from 'primevue/tab'
import TabList from 'primevue/tablist'
import TabPanel from 'primevue/tabpanel'
import TabPanels from 'primevue/tabpanels'
import Tabs from 'primevue/tabs'
import { computed } from 'vue'
import type { S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import { codeSnippets } from './protocolCode'

/** The code to start from (protocolCode.ts), in tabs: Python, C, shell. */
const props = defineProps<{ examples: S['UdpProtocolExamples'] }>()

const snippets = computed(() => codeSnippets(props.examples))
</script>

<template>
  <Tabs value="python" class="code-tabs">
    <TabList>
      <Tab v-for="snippet in snippets" :key="snippet.id" :value="snippet.id">{{ snippet.label }}</Tab>
    </TabList>
    <TabPanels>
      <TabPanel v-for="snippet in snippets" :key="snippet.id" :value="snippet.id">
        <CopyField :value="snippet.code" :label="`${snippet.label} example`" class="code" />
      </TabPanel>
    </TabPanels>
  </Tabs>
</template>

<style scoped>
.code-tabs :deep(.p-tabpanels) {
  padding: var(--pnmt-space-2) 0 0;
}

.code-tabs :deep(.p-tab) {
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
}

.code :deep(.value) {
  white-space: pre;
  overflow-x: auto;
  font-size: 0.857rem;
  padding: var(--pnmt-space-2) 0;
}
</style>
