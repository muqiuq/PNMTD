<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { shortFingerprint } from '@/views/sensors/linux/linuxFormat'
import { confirmMatches, removalStateLabel, removalStateSeverity, type RemovalSource } from './sshKeyFormat'

/**
 * Remove… (docs/linux-sensor-plan.md §6.5), one dialog for every entry point: PNMTD's preview lists each server, account and file,
 * where PNMT may remove the key and where not (greyed, with the reason and config.yaml's line), and the warnings (an account left
 * without keys, root locked out, a key used in the last 24 hours, a cluster-wide file, a server offline). The Admin confirms by
 * typing the number of removals; then each is sent, or waits for its server, or is refused, as the answer shows. Offboarding a
 * user retires their keys too.
 */
const source = defineModel<RemovalSource | null>('source', { required: true })
const visible = computed({ get: () => source.value !== null, set: (v) => !v && (source.value = null) })
const preview = ref<S['SshKeyRemovalPreview'] | null>(null)
const failed = ref<string | null>(null)
const typed = ref('')
const result = ref<S['SshKeyRemoveResult'] | null>(null)
const retired = ref<number | null>(null)

watch(
  source,
  async (s) => {
    preview.value = null
    failed.value = null
    typed.value = ''
    result.value = null
    retired.value = null
    if (!s) return
    try {
      preview.value =
        s.kind === 'items'
          ? await api.sshKeys.preview(s.items)
          : s.kind === 'key'
            ? await api.sshKeys.removalItems(s.keyId)
            : await api.sshKeys.userRemovalItems(s.userId)
    } catch (e) {
      failed.value = (e as Error).message || 'PNMT could not prepare the removal.'
    }
  },
  { immediate: true },
)

const offboarding = computed(() => source.value?.kind === 'user')
const header = computed(() => {
  const s = source.value
  if (!s) return ''
  if (s.kind === 'user') return `Remove all of ${s.name}'s keys from every server`
  if (s.kind === 'key') return `Remove ${s.name} from servers`
  return s.title ?? 'Remove keys'
})
const ready = computed(() => {
  const p = preview.value
  if (!p || result.value) return false
  if (offboarding.value && p.confirm === 0) return true
  return p.allowed > 0 && confirmMatches(typed.value, p.confirm)
})

const { run, busy } = useActions()
async function submit() {
  const s = source.value!
  const p = preview.value!
  const invalidate = [keys.sshKeys, keys.agents, keys.users]
  if (s.kind === 'user') {
    const r = await run(() => api.sshKeys.offboard(s.userId, p.confirm), {
      success: `${s.name}'s keys retired`,
      failure: 'Not done',
      invalidate,
    })
    if (r) {
      retired.value = r.retired
      result.value = r.removal
    }
    return
  }
  const r = await run(
    () => api.sshKeys.removeKeys({ items: p.items.map((i) => i.item), reason: s.reason, confirm: p.confirm }),
    { failure: 'Not removed', invalidate },
  )
  if (r) result.value = r
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="header"
    modal
    :style="{ width: '64rem' }"
    :breakpoints="{ '1100px': '96vw' }"
  >
    <div class="content">
      <Message v-if="failed" severity="error" :closable="false">{{ failed }}</Message>
      <div v-else-if="!preview" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
      <template v-else-if="result">
        <p>
          <strong>{{ result.sent }}</strong> sent, <strong>{{ result.pending }}</strong> waiting for their server,
          <strong>{{ result.refused }}</strong> refused<template v-if="retired !== null">
            · {{ retired }} {{ retired === 1 ? 'key' : 'keys' }} retired</template
          >.
        </p>
        <DataTable :value="result.items" size="small" data-key="id">
          <Column header="Server">
            <template #body="{ data }">{{ data.serverName }}</template>
          </Column>
          <Column header="Account and file">
            <template #body="{ data }">
              <strong>{{ data.account }}</strong>
              <div class="mono small muted">{{ data.path }}</div>
            </template>
          </Column>
          <Column header="Key">
            <template #body="{ data }">
              {{ data.keyName ?? shortFingerprint(data.fingerprint) }}
            </template>
          </Column>
          <Column header="State" style="width: 22rem">
            <template #body="{ data }">
              <Tag :value="removalStateLabel(data.state)" :severity="removalStateSeverity(data.state)" />
              <div v-if="data.message" class="small">{{ data.message }}</div>
            </template>
          </Column>
        </DataTable>
        <p class="small muted">
          A server that is offline gets its removals when it connects, within 7 days. The results arrive on
          <RouterLink
            :to="{ name: 'ssh-keys', params: { tab: 'removals' }, query: { batchId: result.batchId } }"
            @click="visible = false"
            >Removals</RouterLink
          >.
        </p>
      </template>
      <template v-else>
        <p v-if="!preview.items.length" class="muted">
          {{ offboarding ? 'None of their keys is authorized on a server.' : 'Nothing to remove.' }}
        </p>
        <template v-else>
          <p>
            <strong>{{ preview.items.length }}</strong>
            {{ preview.items.length === 1 ? 'removal' : 'removals' }}: <strong>{{ preview.allowed }}</strong> allowed,
            <strong>{{ preview.refused }}</strong> refused by the server's rights.
          </p>
          <Message v-for="w in preview.warnings" :key="w" severity="warn" :closable="false" size="small">{{
            w
          }}</Message>
          <DataTable
            :value="preview.items"
            size="small"
            :row-class="(i) => (i.allowed ? '' : 'row-muted')"
            scrollable
            scroll-height="24rem"
          >
            <Column header="Server" style="width: 10rem">
              <template #body="{ data }">
                {{ data.serverName }}
                <div><Tag v-if="!data.connected" value="offline" severity="secondary" class="small" /></div>
              </template>
            </Column>
            <Column header="Account and file">
              <template #body="{ data }">
                <strong>{{ data.item.account }}</strong>
                <div class="mono small muted">{{ data.item.path }}</div>
                <Tag
                  v-if="data.clusterWide"
                  :value="`cluster-wide: ${data.clusterNodes.join(', ')}`"
                  severity="warn"
                  class="small"
                />
              </template>
            </Column>
            <Column header="Key" style="width: 14rem">
              <template #body="{ data }">
                {{ data.keyName }}
                <div class="muted small">
                  <template v-if="data.keyOwner">{{ data.keyOwner }} · </template
                  ><span class="mono" :title="data.item.fingerprint">{{
                    shortFingerprint(data.item.fingerprint)
                  }}</span>
                </div>
                <div v-if="data.lastUsedAt" class="muted small">used <RelativeTime :value="data.lastUsedAt" /></div>
              </template>
            </Column>
            <Column header="" style="width: 20rem">
              <template #body="{ data }">
                <Tag v-if="data.allowed" value="allowed" severity="success" class="small" />
                <template v-else>
                  <Tag value="refused" severity="secondary" class="small" />
                  <div class="small">{{ data.refused }}</div>
                </template>
                <div v-for="w in data.warnings" :key="w" class="small warn">{{ w }}</div>
              </template>
            </Column>
          </DataTable>
        </template>
        <ul class="notes small muted">
          <li v-if="offboarding">Their keys are retired too: if one appears again anywhere, 465 says so.</li>
          <li>Sessions that already use a key stay open: removing it doesn't end them.</li>
          <li>
            Each server keeps a backup of the file; restoring is local only, as root:
            <code>pnmt-agent keys restore</code>.
          </li>
        </ul>
        <div v-if="preview.confirm > 0 && preview.allowed > 0" class="field confirm">
          <label for="remove-confirm"
            >Type <strong>{{ preview.confirm }}</strong>, the number of removals, to confirm</label
          >
          <InputText id="remove-confirm" v-model="typed" inputmode="numeric" autocomplete="off" class="confirm-input" />
        </div>
        <Message v-else-if="preview.items.length && !preview.allowed" severity="info" :closable="false" size="small">
          PNMT may remove none of them: each server's config.yaml decides, as each row says.
        </Message>
      </template>
    </div>
    <template #footer>
      <template v-if="result">
        <Button label="Done" @click="visible = false" />
      </template>
      <template v-else>
        <Button label="Cancel" severity="secondary" text @click="visible = false" />
        <Button
          :label="
            offboarding && preview?.confirm === 0
              ? 'Retire their keys'
              : `Remove ${preview?.confirm ?? ''} ${preview?.confirm === 1 ? 'key' : 'keys'}`
          "
          icon="pi pi-trash"
          severity="danger"
          :loading="busy"
          :disabled="!ready"
          @click="submit"
        />
      </template>
    </template>
  </Dialog>
</template>

<style scoped>
.content {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.content p {
  margin: 0;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-4);
}

.notes {
  margin: 0;
  padding-left: var(--pnmt-space-4);
}

.confirm-input {
  width: 8rem;
}

.warn {
  color: var(--pnmt-warn);
}

.small {
  font-size: 0.857rem;
}
</style>
