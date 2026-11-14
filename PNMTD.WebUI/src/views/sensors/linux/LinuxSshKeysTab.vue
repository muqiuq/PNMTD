<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { computed, ref, toRef } from 'vue'
import { api, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useCopy } from '@/composables/useCopy'
import AddKeysDialog from '@/views/admin/sshkeys/AddKeysDialog.vue'
import RemoveKeysDialog from '@/views/admin/sshkeys/RemoveKeysDialog.vue'
import type { RemovalSource } from '@/views/admin/sshkeys/sshKeyFormat'
import SectionBar from '../agent/SectionBar.vue'
import { useLinuxSection } from '../agent/useAgentSection'
import { invalidLinesText, optionSeverity, rootLogin, rootWithoutFrom, shortFingerprint, weakKey } from './linuxFormat'

/**
 * A Linux server's SSH keys (docs/linux-sensor-plan.md §6.1, §6.3, §6.5): sshd's key settings and the CA keys it trusts; then per
 * account and file the path (and a followed symlink's target), cluster-wide on Proxmox VE, owner and mode, the file's problems,
 * whether PNMT may remove keys here (from config.yaml, with the reason when not), the lines that aren't keys (by number only),
 * and each key: its registered name and owner (or unregistered), type and bits, fingerprint (with copy, and its logins),
 * comment, options, notes, protected, last use, and whether PNMT may remove it here or why not. For Admins: Register… (prefilled
 * from the keys) and Remove… for the chosen keys. The User role: key comments name people.
 */
const props = defineProps<{ server: S['AgentServerDetail']; canWrite: boolean; isAdmin: boolean }>()
const id = toRef(() => props.server.server.sensorId)
const section = useLinuxSection(id, 'ssh-keys')
const view = useQuery({
  queryKey: computed(() => [...keys.agentServer(id.value), 'ssh-keys-view']),
  queryFn: async () => {
    try {
      return await api.agents.serverSshKeys(id.value)
    } catch (e) {
      if ((e as { status?: number }).status === 404) return null
      throw e
    }
  },
  refetchInterval: 60_000,
})
const data = computed(() => view.data.value ?? null)
const allKeys = computed(() => (data.value?.files ?? []).flatMap((f) => f.keys))
const glance = computed(() => ({
  distinct: new Set(allKeys.value.map((k) => k.fingerprint)).size,
  weak: allKeys.value.filter((k) => k.weakness || weakKey(k)).length,
  rootWithoutFrom: allKeys.value.filter(rootWithoutFrom).length,
  problems: (data.value?.files ?? []).reduce((n, f) => n + f.problems.length, 0),
}))
const search = ref('')
const missing = ref(false)
const query = computed(() => search.value.trim().toLowerCase())
const keyMatches = (k: S['SshKeyServerKey']) =>
  [k.fingerprint, k.comment, k.type, k.registration?.name, k.registration?.owner.name, ...k.options, ...k.notes].some(
    (v) => v?.toLowerCase().includes(query.value),
  )
/** The files with the keys that match the search, root's first; files sshd would read that don't exist only on request. */
const files = computed(() =>
  (data.value?.files ?? [])
    .filter((f) => f.exists || missing.value)
    .map((f) => {
      const fileMatches =
        !query.value || [f.account, f.path, f.target].some((v) => v?.toLowerCase().includes(query.value))
      return {
        file: f,
        keys: fileMatches ? f.keys : f.keys.filter(keyMatches),
        shown: fileMatches || f.keys.some(keyMatches),
      }
    })
    .filter((f) => f.shown)
    .sort(
      (a, b) =>
        Number(b.file.account === 'root') - Number(a.file.account === 'root') ||
        a.file.account.localeCompare(b.file.account),
    ),
)
const absent = computed(() => (data.value?.files ?? []).filter((f) => !f.exists).length)
const copy = useCopy()
const on = (value: boolean) => (value ? 'on' : 'off')
const loginsWith = (fingerprint: string) => ({
  name: 'sensor',
  params: { id: id.value, tab: 'access' },
  query: { key: fingerprint },
})

// --- the Admin's choice, Register… and Remove… (§6.3, §6.5) ---------------------------------------------------------------
type Chosen = { file: S['SshKeyServerFile']; key: S['SshKeyServerKey'] }
const chosen = ref(new Map<string, Chosen>())
const slot = (f: S['SshKeyServerFile'], k: S['SshKeyServerKey']) => `${f.path}\n${k.line}`
const toggle = (f: S['SshKeyServerFile'], k: S['SshKeyServerKey'], value: boolean) => {
  const next = new Map(chosen.value)
  if (value) next.set(slot(f, k), { file: f, key: k })
  else next.delete(slot(f, k))
  chosen.value = next
}
const chosenList = computed(() => [...chosen.value.values()])
const unregisteredChosen = computed(() => chosenList.value.filter((c) => !c.key.registration))
const registering = ref<string | null>(null)
const registerOpen = computed({ get: () => registering.value !== null, set: (v) => !v && (registering.value = null) })
const removing = ref<RemovalSource | null>(null)
const register = (list: Chosen[]) =>
  (registering.value = [...new Map(list.map((c) => [c.key.fingerprint, c.key.publicKey])).values()].join('\n'))
const remove = (list: Chosen[]) =>
  (removing.value = {
    kind: 'items',
    items: list.map((c) => ({
      sensorId: id.value,
      account: c.file.account,
      path: c.file.path,
      fingerprint: c.key.fingerprint,
    })),
    reason: 'Server',
    title: `Remove keys from ${props.server.server.host.name} / ${props.server.server.name}`,
  })
const clear = () => (chosen.value = new Map())
</script>

<template>
  <div class="tab">
    <SectionBar
      :sensor-id="id"
      section="ssh-keys"
      :view="section.data.value"
      :loading="section.loading.value"
      :error="section.error.value"
      :can-write="canWrite"
      :connected="server.server.connected"
    />
    <template v-if="data">
      <Message v-if="data.truncated" severity="warn" :closable="false" size="small">
        The caps were reached (1 MiB and 1,000 keys per file, 20,000 per server): not every key is listed.
      </Message>
      <Message v-for="n in data.notes" :key="n" severity="info" :closable="false" size="small">{{ n }}</Message>
      <div class="two-columns">
        <section class="panel">
          <div class="panel-header">sshd's key settings</div>
          <div class="panel-body content">
            <Message v-if="!data.sshd?.available" severity="warn" :closable="false" size="small">
              sshd's settings could not be read (sshd -T): the files are those of sshd's defaults.
            </Message>
            <dl v-if="data.sshd" class="facts">
              <dt>AuthorizedKeysFile</dt>
              <dd class="mono small">{{ data.sshd.authorizedKeysFiles.join(' ') || '–' }}</dd>
              <dt>Public keys</dt>
              <dd>{{ on(data.sshd.pubkeyAuthentication) }}</dd>
              <dt>PermitRootLogin</dt>
              <dd>
                <span class="mono">{{ data.sshd.permitRootLogin ?? '–' }}</span>
                <span class="muted small"> · {{ rootLogin(data.sshd.permitRootLogin).text }}</span>
              </dd>
              <dt>StrictModes</dt>
              <dd :class="{ warn: !data.sshd.strictModes }">
                {{ data.sshd.strictModes ? 'on' : 'off: sshd reads key files others can write' }}
              </dd>
              <dt>AuthorizedKeysCommand</dt>
              <dd>
                <template v-if="data.sshd.authorizedKeysCommand">
                  <span class="mono small">{{ data.sshd.authorizedKeysCommand }}</span>
                  <span v-if="data.sshd.authorizedKeysCommandUser" class="muted small">
                    as {{ data.sshd.authorizedKeysCommandUser }}</span
                  >
                  <div class="small warn">Keys it provides (LDAP, SSSD, a vault) can't be listed here.</div>
                </template>
                <span v-else class="muted">none</span>
              </dd>
              <dt>TrustedUserCAKeys</dt>
              <dd>
                <span v-if="data.sshd.trustedUserCaKeys" class="mono small">{{ data.sshd.trustedUserCaKeys }}</span>
                <span v-else class="muted">none</span>
              </dd>
              <template v-if="data.sshd.authorizedPrincipalsFile">
                <dt>AuthorizedPrincipalsFile</dt>
                <dd class="mono small">{{ data.sshd.authorizedPrincipalsFile }}</dd>
              </template>
              <template v-if="data.sshd.matchBlocks">
                <dt>Match blocks</dt>
                <dd class="small">the files are read per account, as sshd decides them</dd>
              </template>
            </dl>
          </div>
        </section>
        <section class="panel">
          <div class="panel-header">
            Keys
            <span class="spacer" />
            <span class="muted small"
              >{{ data.totalKeys }} in {{ data.files.filter((f) => f.exists).length }} files</span
            >
          </div>
          <div class="panel-body content">
            <div class="tags">
              <Tag :value="`${glance.distinct} different`" severity="secondary" />
              <Tag v-if="data.unregistered" :value="`${data.unregistered} unregistered`" severity="warn" />
              <Tag
                v-if="data.retiredAuthorized"
                :value="`${data.retiredAuthorized} retired, still here`"
                severity="danger"
              />
              <Tag v-if="data.weak" :value="`${data.weak} weak`" severity="danger" />
              <Tag
                v-if="glance.rootWithoutFrom"
                :value="`${glance.rootWithoutFrom} of root's without from=`"
                severity="warn"
              />
              <Tag v-if="glance.problems" :value="`${glance.problems} file problems`" severity="warn" />
            </div>
            <div class="small">
              <template v-if="data.removalAllowed">
                PNMT may remove keys here<template v-if="data.removalUsers.length">
                  for {{ data.removalUsers.join(', ') }}</template
                ><template v-else> for every account</template>.
              </template>
              <span v-else class="muted">PNMT may not remove keys here: {{ data.removalRefused }}</span>
            </div>
            <div>
              <div class="small muted">CA keys sshd trusts: certificates they sign get in.</div>
              <ul v-if="data.caKeys.length" class="plain">
                <li v-for="ca in data.caKeys" :key="ca.fingerprint" class="line">
                  {{ ca.type }}<template v-if="ca.bits"> {{ ca.bits }}</template>
                  <span class="mono small" :title="ca.fingerprint">{{ shortFingerprint(ca.fingerprint) }}</span>
                  <Button
                    icon="pi pi-copy"
                    text
                    size="small"
                    severity="secondary"
                    :aria-label="`Copy ${ca.fingerprint}`"
                    @click="copy(ca.fingerprint, 'Fingerprint')"
                  />
                  <span class="muted small">{{ ca.comment }} · {{ ca.source }}</span>
                </li>
              </ul>
              <div v-else class="muted">none</div>
            </div>
          </div>
        </section>
      </div>
      <div class="toolbar">
        <InputText
          v-model="search"
          placeholder="Search names, accounts, files, fingerprints, comments"
          size="small"
          class="search"
        />
        <label v-if="absent" class="check">
          <Checkbox v-model="missing" binary input-id="ssh-missing" />
          <span>files that don't exist ({{ absent }})</span>
        </label>
        <span class="spacer" />
        <template v-if="isAdmin">
          <span v-if="chosenList.length" class="muted small">{{ chosenList.length }} chosen</span>
          <Button
            label="Register…"
            icon="pi pi-key"
            size="small"
            :disabled="!unregisteredChosen.length"
            @click="register(unregisteredChosen)"
          />
          <Button
            label="Remove…"
            icon="pi pi-trash"
            size="small"
            severity="danger"
            outlined
            :disabled="!chosenList.length"
            @click="remove(chosenList)"
          />
        </template>
      </div>
      <p v-if="!files.length" class="muted">
        {{ query ? `No key or file matches “${search.trim()}”.` : 'No key file found.' }}
      </p>
      <section v-for="{ file: f, keys: fileKeys } in files" :key="`${f.account}:${f.path}`" class="panel">
        <div class="panel-header file-header">
          <span class="account">{{ f.account }}</span>
          <span class="mono small">{{ f.path }}</span>
          <span v-if="f.target" class="mono small muted" title="A symlink owned by root, followed"
            >→ {{ f.target }}</span
          >
          <Tag
            v-if="f.clusterWide"
            value="cluster-wide"
            severity="warn"
            title="Proxmox VE's shared file: a removal takes a key from every node of the cluster"
          />
          <Tag v-if="!f.exists" value="doesn't exist" severity="secondary" />
          <Tag
            v-else
            :value="f.removable ? 'PNMT may remove keys' : 'not removable'"
            :severity="f.removable ? 'info' : 'secondary'"
            :title="f.notRemovable ?? undefined"
          />
          <span class="spacer" />
          <span v-if="f.exists" class="muted small mono">{{ f.owner }}:{{ f.group }} {{ f.mode }}</span>
        </div>
        <div v-if="f.exists" class="panel-body content">
          <div class="muted small">
            <template v-if="f.modifiedAt">changed <RelativeTime :value="f.modifiedAt" /></template
            ><template v-if="f.notRemovable"> · not removable: {{ f.notRemovable }}</template>
          </div>
          <Message v-for="p in f.problems" :key="p" severity="warn" :closable="false" size="small">{{ p }}</Message>
          <div v-if="f.invalidLines.length" class="small muted">
            {{ invalidLinesText(f.invalidLines) }} (their text is never sent).
          </div>
        </div>
        <DataTable v-if="f.exists" :value="fileKeys" size="small" data-key="line">
          <template #empty><p class="empty muted">No keys.</p></template>
          <Column v-if="isAdmin" style="width: 3rem">
            <template #body="{ data: k }">
              <Checkbox
                :model-value="chosen.has(slot(f, k))"
                binary
                :aria-label="`Choose the key on line ${k.line} of ${f.path}`"
                @update:model-value="(v: boolean) => toggle(f, k, v)"
              />
            </template>
          </Column>
          <Column header="Line" style="width: 3.5rem">
            <template #body="{ data: k }"
              ><span class="muted mono small">{{ k.line }}</span></template
            >
          </Column>
          <Column header="Key">
            <template #body="{ data: k }">
              <template v-if="k.registration">
                <RouterLink
                  v-if="isAdmin"
                  :to="{ name: 'ssh-keys', params: { tab: 'keys' }, query: { key: k.registration.id } }"
                  class="strong"
                  >{{ k.registration.name }}</RouterLink
                >
                <strong v-else>{{ k.registration.name }}</strong>
                <span class="muted small"> {{ k.registration.owner.name }}</span>
                <Tag v-if="k.registration.state === 'Retired'" value="retired" severity="danger" class="small" />
              </template>
              <Tag v-else value="unregistered" severity="warn" class="small" />
              <div class="small muted">
                {{ k.type.replace(/^ssh-/, '') }} <template v-if="k.bits">{{ k.bits }}</template>
                <template v-if="k.comment"> · {{ k.comment }}</template>
              </div>
            </template>
          </Column>
          <Column header="Fingerprint" style="width: 17rem">
            <template #body="{ data: k }">
              <span class="key">
                <span class="mono small" :title="k.fingerprint">{{ shortFingerprint(k.fingerprint) }}</span>
                <Button
                  v-tooltip.top="'Copy the fingerprint'"
                  icon="pi pi-copy"
                  text
                  size="small"
                  severity="secondary"
                  :aria-label="`Copy ${k.fingerprint}`"
                  @click="copy(k.fingerprint, 'Fingerprint')"
                />
                <RouterLink
                  v-tooltip.top="'Logins with this key'"
                  :to="loginsWith(k.fingerprint)"
                  :aria-label="`Logins with ${k.fingerprint}`"
                  class="logins"
                  ><i class="pi pi-sign-in"
                /></RouterLink>
              </span>
            </template>
          </Column>
          <Column header="Options and notes">
            <template #body="{ data: k }">
              <div class="tags">
                <Tag
                  v-if="k.protected"
                  value="protected"
                  severity="success"
                  class="small"
                  :title="k.protectedBecause ?? undefined"
                />
                <Tag v-for="o in k.options" :key="o" :value="o" :severity="optionSeverity(o)" class="small mono" />
                <Tag
                  v-for="n in k.notes"
                  :key="n"
                  :value="n"
                  :severity="weakKey({ notes: [n] }) ? 'danger' : 'warn'"
                  class="small"
                />
              </div>
            </template>
          </Column>
          <Column header="Last used" style="width: 10rem">
            <template #body="{ data: k }">
              <template v-if="k.lastUsedAt">
                <RelativeTime :value="k.lastUsedAt" />
                <div class="mono small muted">{{ k.lastUsedFrom }}</div>
              </template>
              <span v-else-if="k.firstSeenAt" class="muted small"
                >first seen <RelativeTime :value="k.firstSeenAt"
              /></span>
              <div v-if="!k.canRemove" class="small muted" :title="k.cannotRemove ?? undefined">
                <i class="pi pi-lock" /> PNMT can't remove it
              </div>
            </template>
          </Column>
          <Column v-if="isAdmin" style="width: 6rem">
            <template #body="{ data: k }">
              <span class="row-actions">
                <Button
                  v-if="!k.registration"
                  v-tooltip.top="'Register…'"
                  icon="pi pi-key"
                  text
                  size="small"
                  :aria-label="`Register the key on line ${k.line} of ${f.path}`"
                  @click="register([{ file: f, key: k }])"
                />
                <Button
                  v-tooltip.top="'Remove…'"
                  icon="pi pi-trash"
                  text
                  size="small"
                  severity="danger"
                  :aria-label="`Remove the key on line ${k.line} of ${f.path}`"
                  @click="remove([{ file: f, key: k }])"
                />
              </span>
            </template>
          </Column>
        </DataTable>
      </section>
    </template>
    <AddKeysDialog
      v-if="isAdmin"
      v-model:visible="registerOpen"
      :prefill="registering"
      title="Register keys found on this server"
      @registered="clear"
    />
    <RemoveKeysDialog v-if="isAdmin" v-model:source="removing" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.content {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.toolbar,
.line,
.tags {
  display: flex;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
  align-items: center;
  flex-wrap: wrap;
}

.file-header {
  flex-wrap: wrap;
  padding-top: var(--pnmt-space-1);
  padding-bottom: var(--pnmt-space-1);
}

.account,
.strong {
  font-weight: 600;
}

.search {
  width: 24rem;
  max-width: 100%;
}

.check {
  display: flex;
  gap: var(--pnmt-space-1);
  align-items: center;
}

.spacer {
  flex: 1;
}

.plain {
  list-style: none;
  margin: var(--pnmt-space-1) 0 0;
  padding: 0;
}

.key,
.row-actions {
  display: inline-flex;
  align-items: center;
  white-space: nowrap;
}

.logins {
  color: var(--pnmt-muted);
  padding: 0 var(--pnmt-space-1);
}

.warn {
  color: var(--pnmt-warn);
}

.empty {
  margin: 0;
  padding: var(--pnmt-space-2) 0;
}

.small {
  font-size: 0.857rem;
}
</style>
