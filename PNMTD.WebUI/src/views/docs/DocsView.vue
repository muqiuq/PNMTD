<script setup lang="ts">
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import IconField from 'primevue/iconfield'
import InputIcon from 'primevue/inputicon'
import InputText from 'primevue/inputtext'
import { computed, ref } from 'vue'
import CodeTag from '@/components/CodeTag.vue'
import CopyField from '@/components/CopyField.vue'
import PageHeader from '@/components/PageHeader.vue'
import { humanize, typeIcon, typeLabel, useRuleTypes, useSensorTypes, useStatusCodes } from '@/composables/useLookups'
import { useSession } from '@/stores/session'
import { recipientHint } from '../rules/ruleHelp'

/**
 * The documentation (§5.6), from the API so it never drifts: the sensor API with examples for this
 * instance's address (§3.3), the status codes, the sensor types and the notification rule types.
 */
const session = useSession()
const codes = useStatusCodes()
const types = useSensorTypes()
const ruleTypes = useRuleTypes()
const search = ref('')

const base = computed(() => (session.info?.publicBaseUrl || location.origin).replace(/\/$/, ''))
const has = (...texts: (string | null | undefined)[]) => {
  const q = search.value.trim().toLowerCase()
  return !q || texts.some((t) => (t ?? '').toLowerCase().includes(q))
}

const api = computed(() => [
  {
    title: 'Report an event (GET)',
    code: `curl "${base.value}/event/<write token>/200/backup%20done"`,
    text: 'The code (0–399 is success, from 400 a failure) and a message in the path. 200 with the event id as JSON.',
  },
  {
    title: 'Report an event (POST)',
    code: `curl --data "backup done, 12.4 GB" "${base.value}/event/<write token>/200"`,
    text: 'The body is the message, as raw UTF-8 text, trimmed. Any Content-Type.',
  },
  {
    title: 'Messages with special characters',
    code: `curl --data "\\$$(printf '%s' 'disk /var: 42 %' | base64)" "${base.value}/event/<write token>/200"`,
    text: 'A message starting with $ is Base64 of UTF-8 text (standard alphabet) and is decoded.',
  },
  {
    title: 'Several values at once (Encapsulated sensors)',
    code: `curl --data '[{"Name":"disk","Code":200,"Message":"42 % used"}]' "${base.value}/event/<write token>/200"`,
    text: 'A JSON array of { Name, Code, Message }, names case-sensitive. Each Name becomes a child sensor (at most 65); the code in the URL is ignored.',
  },
  {
    title: 'Read the state',
    code: `curl "${base.value}/read/<read token>/json"`,
    text: 'json (the default), raw ("True;message") or property ("lastEventSuccess = True"). Plain text; with Accept: application/json the JSON comes as a JSON string.',
  },
  {
    title: 'Answers',
    code: "200  the event was stored\n404  unknown token or disabled sensor\n400  the code is not a number\n425  less than 5 seconds since the sensor's last event\n500  invalid Base64, regex or JSON",
    text: 'Identical events in a row are not stored again; only the time of the last one moves. 2000 events are kept per sensor.',
  },
])
const visibleApi = computed(() => api.value.filter((a) => has(a.title, a.text, a.code)))
const visibleCodes = computed(() => codes.list.value.filter((c) => has(String(c.code), c.name, c.description)))
const visibleTypes = computed(() => types.list.value.filter((t) => has(t.type, t.description, t.parameters)))
const visibleRules = computed(() => ruleTypes.list.value.filter((r) => has(r.type, r.description)))
</script>

<template>
  <div class="page docs">
    <PageHeader title="Documentation">
      <template #actions>
        <IconField>
          <InputIcon class="pi pi-search" />
          <InputText
            v-model="search"
            size="small"
            placeholder="Search the documentation"
            aria-label="Search the documentation"
          />
        </IconField>
      </template>
    </PageHeader>

    <section v-if="visibleApi.length" class="panel" aria-labelledby="api-title">
      <div id="api-title" class="panel-header">Sensor API</div>
      <div class="panel-body api">
        <p class="muted intro">
          Scripts and devices report with a sensor's write token, and read its state with the read token; both are on
          the sensor's page. No sign-in is needed: the token is the secret.
        </p>
        <article v-for="a in visibleApi" :key="a.title" class="example">
          <h3>{{ a.title }}</h3>
          <p>{{ a.text }}</p>
          <CopyField :value="a.code" :label="a.title" class="code" />
        </article>
      </div>
    </section>

    <section v-if="visibleCodes.length" class="panel" aria-labelledby="codes-title">
      <div id="codes-title" class="panel-header">Status codes</div>
      <DataTable :value="visibleCodes" size="small" data-key="code">
        <Column header="Code" style="width: 5rem">
          <template #body="{ data }"><CodeTag :code="data.code" /></template>
        </Column>
        <Column header="Name" style="width: 16rem">
          <template #body="{ data }"
            >{{ humanize(data.name) }} <span class="muted mono small">{{ data.name }}</span></template
          >
        </Column>
        <Column field="description" header="Meaning" />
      </DataTable>
    </section>

    <section v-if="visibleTypes.length" class="panel" aria-labelledby="types-title">
      <div id="types-title" class="panel-header">Sensor types</div>
      <DataTable :value="visibleTypes" size="small" data-key="type">
        <Column header="Type" style="width: 16rem">
          <template #body="{ data }">
            <span class="type"><i :class="typeIcon(data.type)" class="muted" /> {{ typeLabel(data.type) }}</span>
          </template>
        </Column>
        <Column field="description" header="What it does" />
        <Column header="Parameters" style="width: 16rem">
          <template #body="{ data }"
            ><span class="muted">{{ data.parameters ?? '–' }}</span></template
          >
        </Column>
      </DataTable>
    </section>

    <section v-if="visibleRules.length" class="panel" aria-labelledby="rules-title">
      <div id="rules-title" class="panel-header">Notification rule types</div>
      <DataTable :value="visibleRules" size="small" data-key="type">
        <Column header="Sends" style="width: 22rem">
          <template #body="{ data }">{{ humanize(data.type) }}</template>
        </Column>
        <Column field="description" header="When" />
      </DataTable>
      <p class="panel-body muted recipients">Recipients: {{ recipientHint }}</p>
    </section>
  </div>
</template>

<style scoped>
.intro {
  margin: 0 0 var(--pnmt-space-3);
}

.api {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(26rem, 1fr));
  gap: var(--pnmt-space-4);
}

.api .intro {
  grid-column: 1 / -1;
}

.example h3 {
  margin-bottom: var(--pnmt-space-1);
}

.example p {
  margin: 0 0 var(--pnmt-space-2);
  color: var(--pnmt-muted);
}

.code :deep(.value) {
  white-space: pre-wrap;
}

.type {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.small {
  font-size: 0.786rem;
}

.recipients {
  margin: 0;
  border-top: 1px solid var(--pnmt-border);
}

@media (max-width: 599px) {
  .api {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
