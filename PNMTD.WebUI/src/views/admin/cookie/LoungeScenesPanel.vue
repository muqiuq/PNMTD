<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { formatAbsolute } from '@/format'

/**
 * The lounge's scenes (docs/lounge-plan.md §15.3): the last 50, each with its seed, messages, tool calls and cost; opened,
 * its steps (the ground, the tools, the reasoning, the refusals) and its messages, any of which can be removed here, with
 * the memories made from it.
 */
const scenes = useQuery({
  queryKey: [...keys.lounge, 'admin', 'scenes'],
  queryFn: api.lounge.scenes,
  refetchInterval: 30_000,
})
const openId = ref<number | null>(null)
const showing = computed({
  get: () => openId.value !== null,
  set: (v: boolean) => {
    if (!v) openId.value = null
  },
})
const detail = useQuery({
  queryKey: computed(() => [...keys.lounge, 'admin', 'scene', openId.value]),
  queryFn: () => api.lounge.scene(openId.value!),
  enabled: computed(() => openId.value !== null),
})
const { done, ask, busy } = useActions()
const who = (m: S['LoungeMessageItem']) =>
  m.from === 'cookie'
    ? 'Cookie'
    : m.from === 'seraph'
      ? 'Seraph'
      : m.from === 'system'
        ? 'The room'
        : (m.author ?? 'Someone')
async function remove(m: S['LoungeMessageItem']) {
  if (
    !(await ask(
      'Remove this message?',
      `“${m.text.slice(0, 160)}” goes from the room for everyone, and the memories made from it are forgotten.`,
      'Remove',
    ))
  )
    return
  await done(() => api.lounge.removeMessage(m.id), { success: 'Removed', invalidate: [keys.lounge] })
}
const cost = (usd: number) => `${usd.toFixed(4)} USD`
/** The short columns stay on one line; the seed takes what is left. */
const nowrap = { whiteSpace: 'nowrap' }
</script>

<template>
  <section class="panel" aria-labelledby="lounge-scenes-title">
    <div id="lounge-scenes-title" class="panel-header">Scenes</div>
    <div class="panel-body">
      <ErrorState v-if="scenes.isError.value" :error="scenes.error.value" what="the scenes" @retry="scenes.refetch()" />
      <DataTable
        v-else
        :value="scenes.data.value ?? []"
        :loading="scenes.isPending.value"
        size="small"
        data-key="id"
        selection-mode="single"
        aria-label="The last 50 scenes"
        @row-click="(e) => (openId = (e.data as S['LoungeSceneItem']).id)"
      >
        <template #empty><span class="muted">No scene yet.</span></template>
        <Column header="When" :header-style="nowrap" :body-style="nowrap">
          <template #body="{ data }"><RelativeTime :value="data.startedAt" /></template>
        </Column>
        <Column field="kind" header="Kind" :header-style="nowrap" :body-style="nowrap" />
        <Column header="Opened by" :header-style="nowrap" :body-style="nowrap">
          <template #body="{ data }">{{ data.opener === 'seraph' ? 'Seraph' : 'Cookie' }}</template>
        </Column>
        <Column header="Seed">
          <template #body="{ data }">
            <span class="ellipsis">{{ data.seed ?? 'free talk' }}</span>
            <Tag v-if="data.disagree" value="disagree" severity="warn" class="tag" />
          </template>
        </Column>
        <Column header="Messages" :header-style="nowrap" :body-style="nowrap">
          <template #body="{ data }">{{ data.messages }} / {{ data.plannedLength }}</template>
        </Column>
        <Column field="toolCalls" header="Tools" :header-style="nowrap" :body-style="nowrap" />
        <Column header="Cost" :header-style="nowrap" :body-style="nowrap">
          <template #body="{ data }">{{ cost(data.cost) }}</template>
        </Column>
        <Column field="outcome" header="Outcome" :header-style="nowrap" :body-style="nowrap" />
      </DataTable>
    </div>
  </section>
  <Dialog v-model:visible="showing" modal header="A scene" :style="{ width: 'min(960px, 96vw)' }">
    <ErrorState v-if="detail.isError.value" :error="detail.error.value" what="the scene" @retry="detail.refetch()" />
    <template v-else-if="detail.data.value">
      <p class="small muted">
        {{ detail.data.value.scene.kind }}, {{ formatAbsolute(detail.data.value.scene.startedAt) }} ·
        {{ detail.data.value.scene.seed ?? 'free talk' }}
      </p>
      <h3 class="sub">Messages</h3>
      <ul class="messages">
        <li v-for="m in detail.data.value.messages" :key="m.id">
          <strong>{{ who(m) }}</strong> <span class="muted small">{{ formatAbsolute(m.at) }}</span>
          <span v-if="m.removed" class="muted"> · removed</span>
          <Button
            v-else
            label="Remove"
            icon="pi pi-trash"
            size="small"
            severity="danger"
            text
            :disabled="busy"
            @click="remove(m)"
          />
          <div v-if="!m.removed" class="pre">{{ m.text }}</div>
        </li>
      </ul>
      <h3 class="sub">Steps</h3>
      <details v-for="st in detail.data.value.steps" :key="st.id" class="step">
        <summary>
          <Tag :value="st.kind" severity="secondary" /> {{ st.title }}
          <span class="muted small">{{ formatAbsolute(st.at) }}</span>
        </summary>
        <pre class="pre mono">{{ st.body }}</pre>
      </details>
    </template>
  </Dialog>
</template>

<style scoped>
.ellipsis {
  display: inline-block;
  max-width: 24rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  vertical-align: bottom;
}

.tag {
  margin-left: var(--pnmt-space-1);
}

.sub {
  margin: var(--pnmt-space-3) 0 var(--pnmt-space-1);
  font-size: 1rem;
}

.messages {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.pre {
  white-space: pre-wrap;
  word-break: break-word;
}

.step summary {
  cursor: pointer;
}

.step pre {
  max-height: 40vh;
  overflow: auto;
  font-size: 0.8rem;
}

.small {
  font-size: 0.857rem;
}
</style>
