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
 * API, with curl for each way in. Shown to User and Admin only: the URLs contain the tokens. An Advanced
 * sensor (docs/advanced-sensor-plan.md §4.4) shows its report URL with the token in the header first, and examples
 * in curl, PowerShell and Python instead of /event's.
 */
const props = defineProps<{ integration: S['SensorIntegration']; type: string }>()

const w = computed(() => props.integration.writeUrl)
const isAdvanced = computed(() => props.type === 'ADVANCED' && !!props.integration.reportUrl)
const reportExamples = computed(() => {
  const url = props.integration.reportUrl ?? ''
  const token = props.integration.writeToken
  return [
    {
      key: 'curl',
      label: 'curl',
      code: `curl -H "Authorization: Bearer ${token}" --data @status.json ${url}`,
      note: "A JSON object from a file, the token in the header: it stays out of proxies' access logs. Up to 256 KiB.",
    },
    {
      key: 'inline',
      label: 'Inline',
      code: `curl --data '{"status":"ok","queue":{"length":12}}' ${url}/${token}`,
      note: "The token in the path, for tools that can't set a header.",
    },
    {
      key: 'forward',
      label: 'Forward /health',
      code: `curl -s http://localhost:8080/health | curl -H "Authorization: Bearer ${token}" --data-binary @- ${url}`,
      note: "An app's own health endpoint, from cron: the IETF health format, Spring's and ASP.NET's work as they are.",
    },
    {
      key: 'powershell',
      label: 'PowerShell',
      code: `Invoke-RestMethod -Method Post -Uri ${url} -Headers @{ Authorization = "Bearer ${token}" } \`\n  -ContentType 'application/json' -Body ($status | ConvertTo-Json -Depth 10)`,
      note: 'ConvertTo-Json cuts at depth 2 unless told otherwise.',
    },
    {
      key: 'python',
      label: 'Python',
      code: `requests.post("${url}", json=status, headers={"Authorization": "Bearer ${token}"}, timeout=10)`,
      note: 'The answer is 202 with a preview of the verdict; add ?dryRun=true to try without storing.',
    },
  ]
})
const examples = computed(() => {
  if (isAdvanced.value) {
    return reportExamples.value
  }
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
    <template v-if="isAdvanced">
      <div class="field">
        <label>Report URL</label>
        <CopyField :value="integration.reportUrl!" label="Report URL" />
        <small class="field-hint"
          >POST a JSON object, with <code>Authorization: Bearer</code> and the token below.</small
        >
      </div>
      <div class="field">
        <label>Token</label>
        <CopyField :value="integration.writeToken" label="Token" />
      </div>
    </template>
    <div v-else class="field">
      <label>Write URL</label>
      <CopyField
        :value="`${integration.writeUrl}/200/message`"
        :display="`${integration.writeUrl}/{code}/{message}`"
        label="Write URL"
      />
    </div>
    <div v-if="integration.readUrl && !isAdvanced" class="field">
      <label>Read URL</label>
      <CopyField :value="integration.readUrl" label="Read URL" />
    </div>
    <Tabs :value="isAdvanced ? 'curl' : 'get'" class="examples">
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
