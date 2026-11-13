<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { api, keys } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { parseSeverity, template } from './scriptFormat'

/**
 * A script (docs/windows-sensor-plan.md §7.1): its text, a new version for each change, and what an agent's parser found:
 * parameters, help, syntax errors and best-practice warnings. A new script starts from the template.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ scriptId: string | null }>()
const emit = defineEmits<{ run: [id: string] }>()

const detail = useQuery({
  queryKey: computed(() => [...keys.windows, 'script', props.scriptId]),
  queryFn: () => api.windows.script(props.scriptId!),
  enabled: computed(() => visible.value && props.scriptId !== null),
  refetchInterval: (q) => (q.state.data?.parseState === 'Pending' ? 2000 : false),
})
const name = ref('')
const description = ref('')
const text = ref('')
watch(
  [visible, () => detail.data.value],
  ([open, d]) => {
    if (!open) return
    if (props.scriptId === null) {
      name.value = ''
      description.value = ''
      text.value = template
    } else if (d) {
      name.value = d.name
      description.value = d.description ?? ''
      text.value = d.text
    }
  },
  { immediate: true },
)
const { run, busy } = useActions()
const savedId = ref<string | null>(null)
async function save() {
  const body = { name: name.value, description: description.value || null, text: text.value }
  const saved = await run(
    () => (props.scriptId ? api.windows.updateScript(props.scriptId, body) : api.windows.createScript(body)),
    {
      success: 'Saved: an agent parses it',
      invalidate: [keys.windows],
    },
  )
  if (saved) {
    savedId.value = saved.id
    if (!props.scriptId) visible.value = false
  }
}
const reparse = () => run(() => api.windows.parseScript(props.scriptId!), { invalidate: [keys.windows] })
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="scriptId ? `Script: ${detail.data.value?.name ?? ''}` : 'New script'"
    modal
    :style="{ width: '70rem' }"
    :breakpoints="{ '1100px': '96vw' }"
  >
    <div class="editor">
      <form class="form" @submit.prevent="save">
        <div class="form-grid">
          <div class="field">
            <label for="script-name">Name</label>
            <InputText id="script-name" v-model="name" maxlength="200" />
          </div>
          <div class="field">
            <label for="script-description">Description</label>
            <InputText id="script-description" v-model="description" maxlength="2000" />
          </div>
        </div>
        <div class="field">
          <label for="script-text">PowerShell</label>
          <Textarea id="script-text" v-model="text" rows="20" class="code" spellcheck="false" />
        </div>
      </form>
      <aside v-if="detail.data.value" class="side">
        <p class="line">
          v{{ detail.data.value.currentVersion }}
          <Tag
            :value="detail.data.value.parseState.toLowerCase()"
            :severity="parseSeverity(detail.data.value.parseState)"
          />
          <span class="mono small muted">{{ detail.data.value.hash.slice(0, 12) }}</span>
        </p>
        <Message v-if="detail.data.value.parseState === 'Pending'" severity="info" :closable="false" size="small">
          {{
            detail.data.value.parserAvailable
              ? 'An agent parses it now.'
              : 'Parameters pending: parsed when an agent with PowerShell connects.'
          }}
        </Message>
        <Message v-if="detail.data.value.parseError" severity="error" :closable="false" size="small">{{
          detail.data.value.parseError
        }}</Message>
        <template v-if="detail.data.value.parsed">
          <h4>Parameters</h4>
          <ul class="plain small">
            <li v-for="p in detail.data.value.parsed.parameters" :key="p.name">
              <span class="mono">${{ p.name }}</span> [{{ p.type }}]<template v-if="p.mandatory"> · mandatory</template
              ><template v-if="p.validateSet"> · {{ p.validateSet.join(', ') }}</template
              ><template v-if="p.default"> · = {{ p.default }}</template>
            </li>
            <li v-if="!detail.data.value.parsed.parameters.length" class="muted">None.</li>
          </ul>
          <h4 v-if="detail.data.value.parsed.errors.length">Syntax errors</h4>
          <ul class="plain small">
            <li v-for="(e, i) in detail.data.value.parsed.errors" :key="i" class="error">
              line {{ e.line }}: {{ e.message }}
            </li>
          </ul>
          <h4 v-if="detail.data.value.parsed.findings.length">Best practice</h4>
          <ul class="plain small">
            <li v-for="(f, i) in detail.data.value.parsed.findings" :key="i" class="warn">
              {{ f.message }}<template v-if="f.line"> (line {{ f.line }})</template>
            </li>
          </ul>
        </template>
        <h4>Versions</h4>
        <ul class="plain small">
          <li v-for="v in detail.data.value.versions" :key="v.id">
            v{{ v.number }} · <RelativeTime :value="v.createdAt" /> · {{ v.createdByName ?? '' }}
          </li>
        </ul>
      </aside>
    </div>
    <template #footer>
      <Button v-if="scriptId" label="Parse again" text severity="secondary" @click="reparse" />
      <Button
        v-if="scriptId && detail.data.value?.parseState === 'Parsed'"
        label="Run…"
        icon="pi pi-play"
        severity="secondary"
        @click="emit('run', scriptId)"
      />
      <Button label="Close" text severity="secondary" @click="visible = false" />
      <Button label="Save" :loading="busy" @click="save" />
    </template>
  </Dialog>
</template>

<style scoped>
.editor {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 20rem;
  gap: var(--pnmt-space-4);
}

@media (max-width: 900px) {
  .editor {
    grid-template-columns: minmax(0, 1fr);
  }
}

.code {
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  width: 100%;
}

.side h4 {
  margin: var(--pnmt-space-3) 0 var(--pnmt-space-1);
}

.plain {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.line {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: center;
}

.error {
  color: var(--pnmt-fail);
}

.warn {
  color: var(--pnmt-warn);
}

.small {
  font-size: 0.857rem;
}
</style>
