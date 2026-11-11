<script setup lang="ts">
import Tab from 'primevue/tab'
import TabList from 'primevue/tablist'
import TabPanel from 'primevue/tabpanel'
import TabPanels from 'primevue/tabpanels'
import Tabs from 'primevue/tabs'
import { computed } from 'vue'
import type { S } from '@/api/resources'
import CopyField from './CopyField.vue'

/**
 * How a script reports to a sensor and reads it back (§3.3, §5.6): the URLs of the frozen sensor
 * API, with curl for each way in. Shown to User and Admin only: the URLs contain the tokens.
 */
const props = defineProps<{ integration: S['SensorIntegration']; type: string }>()

const w = computed(() => props.integration.writeUrl)
const examples = computed(() => {
  const list = [
    {
      key: 'get',
      label: 'GET',
      code: `curl "${w.value}/200/backup%20done"`,
      note: 'The code (200 is OK, from 400 a failure) and a message in the path. A / in the message needs Base64.',
    },
    {
      key: 'post',
      label: 'POST',
      code: `curl --data "backup done, 12.4 GB" "${w.value}/200"`,
      note: 'The message as the body, any length and characters.',
    },
    {
      key: 'base64',
      label: 'Base64',
      code: `curl --data "\\$$(printf '%s' 'disk /var: 42 % used' | base64)" "${w.value}/200"`,
      note: 'A message starting with $ is Base64 of UTF-8 text (standard alphabet), decoded by PNMTD. In a GET path only when the Base64 holds no /.',
    },
  ]
  if (props.type === 'ENCAPSULADED') {
    list.push({
      key: 'json',
      label: 'JSON',
      code: `curl --data '[{"Name":"disk","Code":200,"Message":"42 % used"},{"Name":"raid","Code":500,"Message":"degraded"}]' "${w.value}/200"`,
      note: 'A JSON array of { Name, Code, Message } (case-sensitive); each Name becomes a child sensor, at most 65.',
    })
  }
  if (props.integration.readUrl) {
    const base = props.integration.readUrl.replace(/\/json$/, '')
    list.push({
      key: 'read',
      label: 'Read',
      code: `curl "${base}/json"\ncurl "${base}/raw"        # True;message\ncurl "${base}/property"   # lastEventSuccess = True …`,
      note: 'The last event, for scripts that act on a state.',
    })
  }
  return list
})
</script>

<template>
  <div class="url-panel">
    <div class="field">
      <label>Write URL</label>
      <CopyField
        :value="`${integration.writeUrl}/200/message`"
        :display="`${integration.writeUrl}/{code}/{message}`"
        label="Write URL"
      />
    </div>
    <div v-if="integration.readUrl" class="field">
      <label>Read URL</label>
      <CopyField :value="integration.readUrl" label="Read URL" />
    </div>
    <Tabs value="get" class="examples">
      <TabList>
        <Tab v-for="e in examples" :key="e.key" :value="e.key">{{ e.label }}</Tab>
      </TabList>
      <TabPanels>
        <TabPanel v-for="e in examples" :key="e.key" :value="e.key">
          <p class="note muted">{{ e.note }}</p>
          <CopyField :value="e.code" :label="`${e.label} example`" class="code" />
        </TabPanel>
      </TabPanels>
    </Tabs>
  </div>
</template>

<style scoped>
.url-panel {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.field > label {
  font-weight: 500;
}

.examples :deep(.p-tabpanels) {
  padding: var(--pnmt-space-2) 0 0;
}

.examples :deep(.p-tab) {
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
}

.note {
  margin: 0 0 var(--pnmt-space-2);
  font-size: 0.929rem;
}

.code :deep(.value) {
  white-space: pre-wrap;
}
</style>
