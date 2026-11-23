<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Message from 'primevue/message'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref } from 'vue'
import { boardKeys, boardsApi, type BoardDetail, type BoardNetworkRequest } from '@/api/boards'
import QrCode from '@/components/QrCode.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useNotify } from '@/composables/useNotify'
import NetworksEditor from '@/views/connectors/NetworksEditor.vue'

/**
 * Access (docs/boards-plan.md §5, §10.3): on or off; the URL, masked, shown (audited) with Copy and a QR code, and renewed; the networks
 * it opens from without sign-in, with the address PNMT sees for this browser; and which screens showed it.
 */
const props = defineProps<{ board: BoardDetail; manage: boolean }>()
const enabled = defineModel<boolean>('enabled', { required: true })
const networks = defineModel<BoardNetworkRequest[]>('networks', { required: true })
const notify = useNotify()
const { ask } = useActions()
const address = useQuery({ queryKey: boardKeys.address(), queryFn: boardsApi.address, staleTime: 60_000 })
const url = ref<string | null>(null)
const masked = computed(() => `…/pnmt-board/pnmb_••••${props.board.tokenHint ?? ''}`)

async function show() {
  try {
    url.value = (await boardsApi.url(props.board.id)).url
  } catch (e) {
    notify.error(e, 'The URL can’t be shown')
  }
}

async function copy() {
  if (!url.value) await show()
  if (url.value) {
    await navigator.clipboard.writeText(url.value)
    notify.success('URL copied')
  }
}

async function renew() {
  if (
    !(await ask(
      'Renew the URL?',
      'The old URL stops at once: every screen that shows this board needs the new one.',
      'Renew',
    ))
  )
    return
  try {
    url.value = (await boardsApi.renew(props.board.id)).url
    notify.success('A new URL', 'The old one stopped.')
  } catch (e) {
    notify.error(e, 'Not renewed')
  }
}
</script>

<template>
  <div class="form access">
    <label class="switch"><ToggleSwitch v-model="enabled" :disabled="!manage" /> On</label>
    <Message severity="warn" :closable="false" size="small"
      >Anyone who opens this URL from these networks sees everything this board shows.</Message
    >

    <div v-if="manage" class="field">
      <label>URL</label>
      <div class="url">
        <code class="mono" data-test="board-url">{{ url ?? masked }}</code>
        <Button v-if="!url" label="Show" size="small" severity="secondary" text @click="show" />
        <Button label="Copy" icon="pi pi-copy" size="small" severity="secondary" text @click="copy" />
        <a v-if="url" :href="url" target="_blank" rel="noopener noreferrer" class="open"
          >Open <i class="pi pi-external-link" aria-hidden="true"
        /></a>
      </div>
      <QrCode v-if="url" :text="url" :size="168" label="The board's URL as a QR code" />
      <Button label="Renew URL…" icon="pi pi-refresh" size="small" severity="danger" text @click="renew" />
      <small class="field-hint">Showing the URL is in the audit log. The board never shows its own URL.</small>
    </div>

    <div class="field">
      <label>Networks it opens from without sign-in</label>
      <fieldset :disabled="!manage" class="plain">
        <NetworksEditor
          v-model="networks"
          :inside="board.networks.map((n) => n.inside)"
          any-message="Any address: the URL alone opens the board."
        />
      </fieldset>
      <small class="field-hint"
        >None: from anywhere. A signed-in PNMT user opens it from any address, in the browser they signed in
        with.</small
      >
      <small v-if="address.data.value?.address" class="field-hint"
        >Your browser comes from {{ address.data.value.address }}, as PNMT sees it.</small
      >
      <Message v-if="address.data.value?.hint" severity="warn" :closable="false" size="small">{{
        address.data.value.hint
      }}</Message>
      <Message
        v-else-if="networks.length && address.data.value && !address.data.value.proxySet"
        severity="warn"
        :closable="false"
        size="small"
      >
        Proxy isn't set: behind a reverse proxy, every screen seems to come from the proxy's address.
      </Message>
      <small class="field-hint"
        >The session belongs to the host name: when screens open the board at another name than PNMT's admin UI, only
        the networks count. Don't sign in to PNMT in a wall's browser: that browser would then open the admin UI for
        anyone at it.</small
      >
    </div>

    <div class="field">
      <label>Seen</label>
      <span v-if="board.seen.screensNow" class="ok-text">{{
        board.seen.screensNow === 1 ? '1 screen now' : `${board.seen.screensNow} screens now`
      }}</span>
      <span v-if="board.seen.at"
        >Last <RelativeTime :value="board.seen.at" /> from <span class="mono">{{ board.seen.address }}</span></span
      >
      <span v-else class="muted">Never opened yet.</span>
      <small v-if="board.seen.agent" class="muted agent">{{ board.seen.agent }}</small>
    </div>
  </div>
</template>

<style scoped>
.switch {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.url {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.url code {
  overflow-wrap: anywhere;
}

.open {
  font-size: 0.9rem;
}

.plain {
  border: none;
  margin: 0;
  padding: 0;
  min-width: 0;
}

.ok-text {
  color: var(--pnmt-ok);
  font-weight: 600;
}

.agent {
  font-size: 0.8rem;
}
</style>
