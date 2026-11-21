<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import ListTable from '@/components/ListTable.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import { formatPercent } from '@/format'
import {
  assessedByTag,
  defaultMinutes,
  intervalText,
  percentileText,
  roleLabel,
  safeLink,
  sourceKindLabel,
  sourceKinds,
} from '@/views/sensors/linux/updates/osUpdatesFormat'

/**
 * Admin → Servers → Advisories (docs/linux-os-updates-plan.md §10.4, §6.1, §7): the high-risk check's sources, each with its
 * kind, role, name, URL, switch, interval, state and entries, *Fetch now*, editing and deleting; the coverage per role (a role
 * without a working source is 434); the thresholds from .env, read-only; and the advisory texts with their assessments: code's
 * CVEs and keywords, the AI's claims with their quotes, the verifying model's answers and what code dropped. Servers: Manage.
 * Every text here comes from outside: shown as text, links only when they are http(s).
 */
const { run, done, ask, invalidate, notify } = useActions()
const sources = useQuery({
  queryKey: [...keys.osUpdates, 'sources'],
  queryFn: api.linux.osUpdates.sources,
  refetchInterval: 30_000,
})
const v = computed(() => sources.data.value)
/** What the system sensor OS advisories says: 220 (ok) or 434 (§5.4). */
const healthy = computed(() => !!v.value?.ok)
// The texts with AI on (§10.4), and also while a text source is on: without AI a keyword makes a finding important (D5).
const showTexts = computed(
  () => !!v.value && (v.value.aiEnabled || v.value.sources.some((s) => s.kind === 'Rss' && s.enabled)),
)

// --- a source --------------------------------------------------------------------------------------------------------------
const request = (s: S['OsAdvisorySourceView'], patch: Partial<S['OsAdvisorySourceRequest']> = {}) => ({
  kind: s.kind,
  name: s.name,
  url: s.url,
  enabled: s.enabled,
  intervalMinutes: s.intervalMinutes,
  ...patch,
})
async function toggle(s: S['OsAdvisorySourceView'], enabled: boolean) {
  await run(() => api.linux.osUpdates.updateSource(s.id, request(s, { enabled })), {
    success: enabled ? `${s.name} on` : `${s.name} off`,
    invalidate: [keys.osUpdates],
  })
}
async function fetchNow(s: S['OsAdvisorySourceView']) {
  await done(() => api.linux.osUpdates.fetchSource(s.id), {
    success: `${s.name} is fetched within a minute`,
    invalidate: [keys.osUpdates],
  })
}
async function remove(s: S['OsAdvisorySourceView']) {
  if (
    !(await ask(
      `Delete ${s.name}?`,
      'What it holds goes with it. A role left without a working source turns the OS advisories sensor bad (434).',
    ))
  )
    return
  await done(() => api.linux.osUpdates.deleteSource(s.id), {
    success: `${s.name} deleted`,
    invalidate: [keys.osUpdates],
  })
}
const menu = (s: S['OsAdvisorySourceView']) => [
  { label: 'Edit…', icon: 'pi pi-pencil', command: () => edit(s) },
  { label: 'Delete…', icon: 'pi pi-trash', command: () => remove(s) },
]

const editing = ref(false)
const current = ref<S['OsAdvisorySourceView'] | null>(null)
const form = ref<S['OsAdvisorySourceRequest']>({ kind: 'Rss', name: '', url: '', enabled: true, intervalMinutes: 60 })
const errors = ref<Record<string, string | undefined>>({})
const saving = ref(false)
const kindOptions = sourceKinds.map((k) => ({ label: sourceKindLabel(k), value: k }))
function edit(s: S['OsAdvisorySourceView'] | null) {
  current.value = s
  form.value = s
    ? request(s)
    : { kind: 'Rss', name: '', url: '', enabled: true, intervalMinutes: defaultMinutes('Rss') }
  errors.value = {}
  editing.value = true
}
function kindChanged(kind: S['OsAdvisorySourceKind']) {
  if (!current.value) form.value.intervalMinutes = defaultMinutes(kind)
}
async function save() {
  errors.value = {}
  saving.value = true
  try {
    const body = { ...form.value, name: form.value.name.trim(), url: form.value.url.trim() }
    if (current.value) await api.linux.osUpdates.updateSource(current.value.id, body)
    else await api.linux.osUpdates.createSource(body)
    notify.success(current.value ? 'Source saved' : 'Source added')
    await invalidate([keys.osUpdates])
    editing.value = false
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = {
        kind: e.field('kind'),
        name: e.field('name'),
        url: e.field('url'),
        intervalMinutes: e.field('intervalMinutes'),
      }
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    saving.value = false
  }
}
const stateTag = (
  s: S['OsAdvisorySourceView'],
): { label: string; severity: 'success' | 'warn' | 'danger' | 'secondary' } =>
  !s.enabled
    ? { label: 'off', severity: 'secondary' }
    : s.error && s.stale
      ? { label: 'failing', severity: 'danger' }
      : s.stale
        ? { label: 'stale', severity: 'warn' }
        : s.error
          ? { label: 'last fetch failed', severity: 'warn' }
          : { label: 'fresh', severity: 'success' }

// --- the advisory texts (§7) ------------------------------------------------------------------------------------------------
const items = useServerTable<S['OsAdvisoryItemView']>({
  key: [...keys.osUpdates, 'advisories'],
  fetch: (q) => api.linux.osUpdates.advisories(q),
  pageSize: 25,
  enabled: showTexts,
  refetchInterval: 60_000,
})
const expanded = ref<Record<string, boolean>>({})
function toggleItem(i: S['OsAdvisoryItemView']) {
  const next = { ...expanded.value }
  if (next[i.id]) delete next[i.id]
  else next[i.id] = true
  expanded.value = next
}
const flags = (c: S['OsAdvisoryClaim']) =>
  [
    c.exploitedInTheWild ? 'exploited in the wild' : null,
    c.zeroDay ? 'zero-day' : null,
    c.remoteWithoutCredentials ? 'remote, without credentials' : null,
  ].filter((f): f is string => !!f)
</script>

<template>
  <div class="tab">
    <ErrorState
      v-if="sources.isError.value && !v"
      :error="sources.error.value"
      what="the advisory sources"
      @retry="sources.refetch()"
    />
    <div v-else-if="!v" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <template v-else>
      <Message v-if="!v.checkEnabled" severity="secondary" :closable="false" size="small">
        The high-risk check is off: <code>OsUpdates__Advisories__Enabled=true</code> in .env turns it on. The sources
        below are fetched only while it is on.
      </Message>
      <Message v-else :severity="healthy ? 'success' : 'warn'" :closable="false" size="small">{{ v.state }}</Message>

      <section class="panel" aria-labelledby="coverage-title">
        <div id="coverage-title" class="panel-header">Coverage</div>
        <div class="panel-body">
          <dl class="facts">
            <template v-for="r in v.roles" :key="r.role">
              <dt>{{ r.label }}</dt>
              <dd class="line">
                <template v-if="r.working.length">
                  <span v-for="name in r.working" :key="name" class="working"
                    ><i class="pi pi-check ok" aria-hidden="true" /> {{ name }}</span
                  >
                </template>
                <Tag
                  v-else
                  :value="r.role === 'changelogs' ? 'none received yet' : 'no working source'"
                  :severity="r.role === 'changelogs' ? 'secondary' : 'danger'"
                />
              </dd>
            </template>
          </dl>
          <small class="field-hint">
            Sources only add evidence: the results are a union, so one source failing or lying can't hide what another
            shows. A role without a working source turns the OS advisories sensor bad (434).
          </small>
        </div>
      </section>

      <section class="panel" aria-labelledby="sources-title">
        <div class="panel-header">
          <span id="sources-title">Sources</span>
          <span class="spacer" />
          <Button label="Add source…" icon="pi pi-plus" size="small" @click="edit(null)" />
        </div>
        <DataTable :value="v.sources" size="small" data-key="id">
          <template #empty><p class="empty muted">No source: add one for each role.</p></template>
          <Column header="On" style="width: 4rem">
            <template #body="{ data: s }">
              <ToggleSwitch
                :model-value="s.enabled"
                :aria-label="`${s.name} on`"
                @update:model-value="(on: boolean) => toggle(s, on)"
              />
            </template>
          </Column>
          <Column header="Source">
            <template #body="{ data: s }">
              <strong>{{ s.name }}</strong>
              <div class="muted small">{{ sourceKindLabel(s.kind) }} · {{ roleLabel(s.role) }}</div>
              <div class="mono small url" :title="s.url">{{ s.url }}</div>
            </template>
          </Column>
          <Column header="Every" style="width: 6rem">
            <template #body="{ data: s }"
              ><span class="small">{{ intervalText(s.intervalMinutes) }}</span></template
            >
          </Column>
          <Column header="State" style="width: 16rem">
            <template #body="{ data: s }">
              <Tag :value="stateTag(s).label" :severity="stateTag(s).severity" />
              <div v-if="s.error" class="small bad error">{{ s.error }}</div>
              <div class="small muted">
                <template v-if="s.lastSuccessAt">last success <RelativeTime :value="s.lastSuccessAt" /></template>
                <template v-else>never fetched</template
                ><template v-if="s.lastFetchAt && s.lastFetchAt !== s.lastSuccessAt">
                  · tried <RelativeTime :value="s.lastFetchAt"
                /></template>
              </div>
            </template>
          </Column>
          <Column header="Entries" style="width: 6rem">
            <template #body="{ data: s }"
              ><span class="small">{{ s.entries.toLocaleString() }}</span></template
            >
          </Column>
          <Column style="width: 9rem">
            <template #body="{ data: s }">
              <span class="actions">
                <Button
                  label="Fetch now"
                  size="small"
                  text
                  :disabled="!s.enabled || !v.checkEnabled"
                  @click="fetchNow(s)"
                />
                <MoreMenu :items="menu(s)" :label="`More for ${s.name}`" />
              </span>
            </template>
          </Column>
        </DataTable>
      </section>

      <section class="panel" aria-labelledby="thresholds-title">
        <div id="thresholds-title" class="panel-header">Thresholds</div>
        <div class="panel-body">
          <dl class="facts">
            <dt>High risk</dt>
            <dd>
              EPSS percentile from {{ percentileText(v.highRiskEpssPercentile) }}, or in CISA KEV, or rated critical by
              the distribution, or a verified AI claim
            </dd>
            <dt>Important</dt>
            <dd>EPSS percentile from {{ percentileText(v.importantEpssPercentile) }}, or the tracker's urgency</dd>
            <dt>Stale</dt>
            <dd>a source not fetched for {{ v.staleHours }} h (434)</dd>
            <dt>Acknowledging</dt>
            <dd>a high-risk finding for at most {{ v.acknowledgeMaxDays }} days</dd>
            <dt>Private addresses</dt>
            <dd>{{ v.allowPrivateAddresses ? 'allowed, for a local mirror' : 'refused' }}</dd>
            <dt>AI for texts</dt>
            <dd>{{ v.aiEnabled ? 'on: it can only raise a rating, never lower or clear one' : 'off' }}</dd>
          </dl>
          <small class="field-hint">From .env (OsUpdates__Advisories__…), read-only here.</small>
        </div>
      </section>

      <section v-if="showTexts" class="panel" aria-labelledby="texts-title">
        <div id="texts-title" class="panel-header">Advisory texts</div>
        <p class="panel-body small muted intro">
          From the RSS and Atom sources. Code extracts the CVEs and keywords; with AI on, a claim counts only when its
          quote and CVE are in the text word for word and the verifying model agrees. The models see only these public
          texts, never server names or package lists.
        </p>
        <ListTable
          v-model:expanded="expanded"
          :table="items"
          what="advisory texts"
          selectable
          empty-text="No advisory text yet."
          class="texts"
          @select="toggleItem"
        >
          <Column header="Seen" style="width: 8rem">
            <template #body="{ data: i }"><RelativeTime :value="i.publishedAt ?? i.firstSeenAt" /></template>
          </Column>
          <Column header="Title">
            <template #body="{ data: i }">
              <a
                v-if="safeLink(i.link)"
                :href="safeLink(i.link)!"
                target="_blank"
                rel="noopener noreferrer"
                @click.stop
                >{{ i.title }}</a
              >
              <span v-else>{{ i.title }}</span>
              <div class="muted small">{{ i.sourceName }}</div>
            </template>
          </Column>
          <Column header="CVEs and keywords" style="width: 16rem">
            <template #body="{ data: i }">
              <div class="mono small">
                {{ i.cves.slice(0, 3).join(', ')
                }}<template v-if="i.cves.length > 3"> +{{ i.cves.length - 3 }}</template>
              </div>
              <div class="small muted">{{ i.keywords.join(', ') }}</div>
            </template>
          </Column>
          <Column header="Assessment" style="width: 11rem">
            <template #body="{ data: i }">
              <Tag :value="assessedByTag(i.assessedBy).label" :severity="assessedByTag(i.assessedBy).severity" />
            </template>
          </Column>
          <template #expansion="{ data: i }">
            <div class="assessment">
              <p v-if="!i.assessment" class="small muted">No assessment beyond code's CVEs and keywords.</p>
              <template v-else>
                <p v-if="i.assessment.summary" class="summary">{{ i.assessment.summary }}</p>
                <p v-if="i.assessment.injectionProbability != null" class="small muted">
                  The guard: {{ formatPercent(i.assessment.injectionProbability) }} likely an attempt to instruct an AI.
                </p>
                <div v-for="c in i.assessment.claims" :key="c.cve" class="claim">
                  <div class="line">
                    <span class="mono cve">{{ c.cve }}</span>
                    <Tag v-for="f in flags(c)" :key="f" :value="f" severity="warn" />
                    <span v-if="c.packages.length" class="small muted">{{ c.packages.join(', ') }}</span>
                    <span class="small">verifier: {{ i.assessment.verdicts[c.cve] ?? '–' }}</span>
                  </div>
                  <blockquote class="quote">{{ c.quote }}</blockquote>
                </div>
                <div v-if="i.assessment.dropped.length" class="small">
                  Dropped by code:
                  <ul class="dropped">
                    <li v-for="(d, n) in i.assessment.dropped" :key="n">{{ d }}</li>
                  </ul>
                </div>
              </template>
            </div>
          </template>
          <template #mobile="{ item }">
            <div class="small">{{ item.title }}</div>
            <div class="small muted">
              {{ item.sourceName }} ·
              <Tag :value="assessedByTag(item.assessedBy).label" :severity="assessedByTag(item.assessedBy).severity" />
            </div>
          </template>
        </ListTable>
      </section>
    </template>

    <Dialog
      v-model:visible="editing"
      :header="current ? `Edit ${current.name}` : 'Add a source'"
      modal
      :style="{ width: '34rem' }"
      :breakpoints="{ '600px': '95vw' }"
    >
      <form id="advisory-source-form" class="form" novalidate @submit.prevent="save">
        <div class="field">
          <label for="source-kind">Kind</label>
          <Select
            v-model="form.kind"
            input-id="source-kind"
            :options="kindOptions"
            option-label="label"
            option-value="value"
            :disabled="!!current"
            @update:model-value="kindChanged"
          />
          <small v-if="current" class="field-hint">A source's kind can't change: add a new source instead.</small>
          <small v-if="errors.kind" class="field-error">{{ errors.kind }}</small>
        </div>
        <div class="field">
          <label for="source-name">Name</label>
          <InputText id="source-name" v-model="form.name" maxlength="200" />
          <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
        </div>
        <div class="field">
          <label for="source-url">URL</label>
          <InputText id="source-url" v-model="form.url" maxlength="1000" class="mono" />
          <small class="field-hint"
            >https, or a local mirror (private addresses need OsUpdates__Advisories__AllowPrivateAddresses).</small
          >
          <small v-if="errors.url" class="field-error">{{ errors.url }}</small>
        </div>
        <div class="field">
          <label for="source-interval">Every (minutes)</label>
          <InputNumber v-model="form.intervalMinutes" input-id="source-interval" :min="5" :max="10080" />
          <small v-if="errors.intervalMinutes" class="field-error">{{ errors.intervalMinutes }}</small>
        </div>
        <label class="check"><Checkbox v-model="form.enabled" binary /> On</label>
      </form>
      <template #footer>
        <Button label="Cancel" text severity="secondary" @click="editing = false" />
        <Button type="submit" form="advisory-source-form" label="Save" :loading="saving" />
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-4);
}

.spacer {
  flex: 1;
}

.line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1) var(--pnmt-space-3);
}

.working i {
  font-size: 0.75rem;
}

.ok {
  color: var(--pnmt-ok);
}

.bad {
  color: var(--pnmt-fail);
}

.url,
.error {
  overflow-wrap: anywhere;
}

.actions {
  display: inline-flex;
  align-items: center;
}

.empty,
.intro {
  margin: 0;
}

.empty {
  padding: var(--pnmt-space-3) 0;
}

.texts {
  border: 0;
  border-top: 1px solid var(--pnmt-border);
  border-radius: 0 0 var(--p-border-radius-md) var(--p-border-radius-md);
}

.assessment {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-1) 0;
}

.assessment p {
  margin: 0;
}

.claim {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.cve {
  font-weight: 600;
}

.quote {
  margin: 0;
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
  border-left: 3px solid var(--pnmt-border);
  color: var(--pnmt-muted);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.dropped {
  margin: var(--pnmt-space-1) 0 0;
  padding-left: var(--pnmt-space-4);
}

.check {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.small {
  font-size: 0.857rem;
}
</style>
