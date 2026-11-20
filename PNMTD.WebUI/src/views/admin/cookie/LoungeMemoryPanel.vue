<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import ProgressBar from 'primevue/progressbar'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { voices } from './loungeAdmin'

/**
 * The memory browser (docs/lounge-plan.md §7, §15.3): one voice's keepsakes, then its memories with their strength now,
 * the people they name, and Forget; Forget everything for one voice or both. A keepsake deleted, the voice chooses a new
 * one the next night.
 */
const voice = ref<'cookie' | 'seraph'>('cookie')
const memories = useQuery({
  queryKey: computed(() => [...keys.lounge, 'admin', 'memories', voice.value]),
  queryFn: () => api.lounge.memories(voice.value),
})
const { done, ask, busy } = useActions()
const name = computed(() => (voice.value === 'seraph' ? 'Seraph' : 'Cookie'))

async function forget(id: number, text: string) {
  if (!(await ask(`Forget this?`, `${name.value} forgets “${text}”. It can't be undone.`, 'Forget'))) return
  await done(() => api.lounge.forgetMemory(id), { success: 'Forgotten', invalidate: [keys.lounge] })
}
async function forgetAll(which: 'cookie' | 'seraph' | null) {
  const who = which === null ? 'Cookie and Seraph' : which === 'seraph' ? 'Seraph' : 'Cookie'
  if (
    !(await ask(
      `${who}: forget everything?`,
      `Every memory and keepsake of ${who} goes. It can't be undone.`,
      'Forget everything',
    ))
  )
    return
  await done(() => api.lounge.forget(which), { success: 'Forgotten', invalidate: [keys.lounge] })
}
</script>

<template>
  <section class="panel" aria-labelledby="lounge-memory-browser">
    <div id="lounge-memory-browser" class="panel-header">What they remember</div>
    <div class="panel-body body">
      <div class="bar">
        <SelectButton
          v-model="voice"
          :options="voices as unknown as { label: string; value: string }[]"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          aria-label="Whose memories"
        />
        <span class="spacer" />
        <Button
          :label="`Forget everything of ${name}`"
          icon="pi pi-eraser"
          size="small"
          severity="danger"
          text
          :disabled="busy"
          @click="forgetAll(voice)"
        />
        <Button
          label="Forget everything of both"
          icon="pi pi-trash"
          size="small"
          severity="danger"
          text
          :disabled="busy"
          @click="forgetAll(null)"
        />
      </div>
      <ErrorState
        v-if="memories.isError.value"
        :error="memories.error.value"
        what="the memories"
        @retry="memories.refetch()"
      />
      <p v-else-if="memories.data.value && !memories.data.value.length" class="muted">
        {{ name }} remembers nothing yet.
      </p>
      <ul v-else class="memories">
        <li v-for="m in memories.data.value ?? []" :key="m.id" class="memory">
          <div class="line">
            <Tag v-if="m.keepsake" value="keepsake" severity="info" />
            <Tag :value="m.kind" severity="secondary" />
            <span class="text">{{ m.text }}</span>
            <Button
              icon="pi pi-times"
              size="small"
              severity="secondary"
              text
              :aria-label="`Forget: ${m.text}`"
              :disabled="busy"
              @click="forget(m.id, m.text)"
            />
          </div>
          <div class="meta small muted">
            <ProgressBar
              v-if="!m.keepsake"
              :value="Math.round(m.strength * 100)"
              :show-value="false"
              class="strength"
              :aria-label="`Strength ${Math.round(m.strength * 100)}%`"
            />
            <span v-if="!m.keepsake">{{ Math.round(m.strength * 100) }}%</span>
            <span>since <RelativeTime :value="m.createdAt" /></span>
            <span v-if="m.recalls">· recalled {{ m.recalls }}×</span>
            <span v-if="m.people.length">· names {{ m.people.join(', ') }}</span>
          </div>
        </li>
      </ul>
    </div>
  </section>
</template>

<style scoped>
.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.bar {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.spacer {
  flex: 1;
}

.memories {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.text {
  flex: 1;
  min-width: 0;
}

.meta {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.strength {
  width: 6rem;
  height: 6px;
}

.small {
  font-size: 0.857rem;
}
</style>
