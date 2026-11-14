<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import IconField from 'primevue/iconfield'
import InputIcon from 'primevue/inputicon'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import { computed, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { api, keys } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useSession } from '@/stores/session'
import AttachDialog from './AttachDialog.vue'
import ZabbixInterfaces from './ZabbixInterfaces.vue'
import ZabbixProblemList from './ZabbixProblemList.vue'

/**
 * A Zabbix host's page (docs/audit-redmine-zabbix-plan.md §7.6), asked of Zabbix when opened: its interfaces, groups,
 * templates, tags and description, its open problems, its latest data, its inventory (Zabbix's Details), the links into
 * Zabbix, and the PNMT host it is attached to, or *Attach to PNMT host…*. The mirror's data when Zabbix doesn't answer.
 */
const route = useRoute()
const session = useSession()
const hostId = computed(() => Number(route.params.hostId))
const query = useQuery({
  queryKey: computed(() => [...keys.zabbix, 'host', hostId.value]),
  queryFn: () => api.zabbix.host(hostId.value),
  refetchInterval: 60_000,
})
const page = computed(() => query.data.value)
const host = computed(() => page.value?.host)

const search = ref('')
const debounced = ref('')
let timer: ReturnType<typeof setTimeout> | undefined
watch(search, (v) => {
  clearTimeout(timer)
  timer = setTimeout(() => (debounced.value = v.trim()), 250)
})
const items = useQuery({
  queryKey: computed(() => [...keys.zabbix, 'items', hostId.value, debounced.value]),
  queryFn: () => api.zabbix.items(hostId.value, debounced.value || undefined),
  enabled: computed(() => !!page.value && !page.value.fromMirror),
})

const attaching = ref(false)
const links = computed(() => {
  const l = page.value?.links
  if (!l) return []
  return [
    { label: 'Dashboards', icon: 'pi pi-th-large', url: l.dashboards },
    { label: 'Problems', icon: 'pi pi-exclamation-triangle', url: l.problems },
    { label: 'Latest data', icon: 'pi pi-chart-line', url: l.latest },
    { label: 'Configuration', icon: 'pi pi-cog', url: l.configuration },
  ].map((x) => ({ ...x, target: '_blank' }))
})
</script>

<template>
  <div class="page">
    <ErrorState
      v-if="query.isError.value && !page"
      :error="query.error.value"
      what="the Zabbix host"
      @retry="query.refetch()"
    />
    <div v-else-if="!page || !host" class="center"><ProgressSpinner style="width: 40px; height: 40px" /></div>
    <template v-else>
      <PageHeader
        :title="host.name"
        :crumbs="[
          { label: 'Zabbix', to: { name: 'zabbix' } },
          { label: 'Hosts', to: { name: 'zabbix', params: { tab: 'hosts' } } },
        ]"
      >
        <template #status>
          <span v-if="host.technicalName !== host.name" class="mono muted">{{ host.technicalName }}</span>
          <Tag v-if="!host.monitored" value="Disabled" severity="secondary" />
          <Tag v-if="host.inMaintenance" value="In maintenance" severity="info" />
          <Tag v-if="host.gone" value="Gone from Zabbix" severity="danger" />
          <ZabbixInterfaces :interfaces="host.interfaces" />
        </template>
        <template #actions>
          <Button
            as="a"
            :href="page.links.open"
            target="_blank"
            rel="noopener noreferrer"
            label="Open in Zabbix"
            icon="pi pi-external-link"
            size="small"
          />
          <MoreMenu :items="links" label="More links into Zabbix" />
        </template>
      </PageHeader>
      <Message v-if="page.fromMirror" severity="warn" icon="pi pi-exclamation-triangle">
        Zabbix didn't answer: this is PNMT's copy of the host. {{ page.error }}
      </Message>

      <div class="layout">
        <div class="main">
          <section class="panel" aria-labelledby="zh-problems-title">
            <div id="zh-problems-title" class="panel-header">
              Problems
              <span v-if="page.problems.length" class="muted count">{{ page.problems.length }}</span>
            </div>
            <div class="panel-body"><ZabbixProblemList :problems="page.problems" /></div>
          </section>

          <section v-if="!page.fromMirror" class="panel" aria-labelledby="zh-items-title">
            <div id="zh-items-title" class="panel-header">
              Latest data
              <IconField class="item-search">
                <InputIcon class="pi pi-search" />
                <InputText v-model="search" size="small" placeholder="Item name…" aria-label="Search items" />
              </IconField>
            </div>
            <div class="panel-body">
              <ErrorState
                v-if="items.isError.value"
                :error="items.error.value"
                what="the latest data"
                @retry="items.refetch()"
              />
              <p v-else-if="items.data.value && !items.data.value.length" class="muted none">No items.</p>
              <table v-else-if="items.data.value" class="items">
                <thead>
                  <tr>
                    <th scope="col">Item</th>
                    <th scope="col">Value</th>
                    <th scope="col">When</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="i in items.data.value" :key="i.id">
                    <td>
                      <a :href="i.graphUrl" target="_blank" rel="noopener noreferrer" :title="i.key">{{ i.name }}</a>
                      <span v-if="i.error" class="error small"> · {{ i.error }}</span>
                    </td>
                    <td class="mono">
                      {{ i.value ?? '–' }}<template v-if="i.units && i.value"> {{ i.units }}</template>
                    </td>
                    <td><RelativeTime :value="i.at" /></td>
                  </tr>
                </tbody>
              </table>
              <p v-if="items.data.value?.length === 200" class="muted small">
                The first 200 by name: search to narrow them.
              </p>
            </div>
          </section>
        </div>

        <aside class="side">
          <section class="panel" aria-labelledby="zh-pnmt-title">
            <div id="zh-pnmt-title" class="panel-header">PNMT</div>
            <div class="panel-body">
              <template v-if="host.pnmtHost">
                <p class="none">
                  Attached to
                  <RouterLink :to="{ name: 'host', params: { id: host.pnmtHost.id } }">{{
                    host.pnmtHost.name
                  }}</RouterLink>
                  as
                  <RouterLink :to="{ name: 'sensor', params: { id: host.sensorId } }">its Zabbix sensor</RouterLink>.
                </p>
              </template>
              <template v-else>
                <p class="muted none">Not attached to a PNMT host.</p>
                <Button
                  v-if="session.can('Monitoring', 'Edit')"
                  label="Attach to PNMT host…"
                  icon="pi pi-link"
                  size="small"
                  class="attach"
                  @click="attaching = true"
                />
              </template>
            </div>
          </section>

          <section class="panel" aria-labelledby="zh-interfaces-title">
            <div id="zh-interfaces-title" class="panel-header">Interfaces</div>
            <div class="panel-body">
              <ZabbixInterfaces :interfaces="host.interfaces" detailed />
              <p v-if="!host.interfaces.length" class="muted none">None.</p>
            </div>
          </section>

          <section class="panel" aria-labelledby="zh-about-title">
            <div id="zh-about-title" class="panel-header">About</div>
            <dl class="panel-body facts">
              <dt>Groups</dt>
              <dd>{{ host.groups.join(', ') || '–' }}</dd>
              <template v-if="page.templates.length">
                <dt>Templates</dt>
                <dd>{{ page.templates.join(', ') }}</dd>
              </template>
              <template v-if="page.tags.length">
                <dt>Tags</dt>
                <dd>{{ page.tags.map((t) => (t.value ? `${t.tag}: ${t.value}` : t.tag)).join(', ') }}</dd>
              </template>
              <template v-if="page.description">
                <dt>Description</dt>
                <dd class="wrap">{{ page.description }}</dd>
              </template>
              <dt>Zabbix id</dt>
              <dd class="mono">{{ host.id }}</dd>
            </dl>
          </section>

          <section v-if="page.inventory?.length" class="panel" aria-labelledby="zh-inventory-title">
            <div id="zh-inventory-title" class="panel-header">Inventory</div>
            <dl class="panel-body facts">
              <template v-for="f in page.inventory" :key="f.name">
                <dt>{{ f.name }}</dt>
                <dd class="wrap">{{ f.value }}</dd>
              </template>
            </dl>
          </section>
        </aside>
      </div>
      <AttachDialog v-model:visible="attaching" :host="host" @attached="query.refetch()" />
    </template>
  </div>
</template>

<style scoped>
.layout {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 21rem;
  gap: var(--pnmt-space-3);
  align-items: start;
}

@media (max-width: 999px) {
  .layout {
    grid-template-columns: minmax(0, 1fr);
  }
}

.main,
.side {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  min-width: 0;
}

.count {
  font-weight: 400;
}

.item-search {
  margin-left: auto;
  font-weight: 400;
}

.none {
  margin: 0;
}

.attach {
  margin-top: var(--pnmt-space-2);
}

.items {
  border-collapse: collapse;
  width: 100%;
}

.items th,
.items td {
  text-align: left;
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
}

.wrap {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.error {
  color: var(--pnmt-fail);
}

.small {
  font-size: 0.85em;
}
</style>
