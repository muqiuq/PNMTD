<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { languageLabel, languages, parameterName, parseSeverity, runnable } from './scriptFormat'

/**
 * A script (docs/windows-sensor-plan.md §7.1; docs/linux-sensor-plan.md §8): its text, a new version for each change, and what
 * was found: parameters, help, syntax errors and best-practice findings. A new script chooses its language (PowerShell for
 * Windows servers, bash for Linux servers; it can't change later) and starts from PNMTD's template. PowerShell is parsed by a
 * Windows agent; bash's header is read by PNMTD at the save, and a Linux agent's syntax check (bash -n, shellcheck) is merged in.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ scriptId: string | null; newLanguage?: S['ScriptLanguage'] | null }>()
const emit = defineEmits<{ run: [id: string] }>()

const detail = useQuery({
  queryKey: computed(() => [...keys.agents, 'script', props.scriptId]),
  queryFn: () => api.agents.script(props.scriptId!),
  enabled: computed(() => visible.value && props.scriptId !== null),
  refetchInterval: (q) => (q.state.data?.parseState === 'Pending' ? 2000 : false),
})
const name = ref('')
const description = ref('')
const text = ref('')
const language = ref<S['ScriptLanguage']>('PowerShell')
// A new script's text follows the language's template until it is edited.
const template = ref('')
async function loadTemplate(l: S['ScriptLanguage']) {
  try {
    const t = await api.agents.scriptTemplate(l)
    if (props.scriptId === null && (text.value === '' || text.value === template.value)) text.value = t.text
    template.value = t.text
  } catch {
    // The editor works without a template.
  }
}
watch(
  [visible, () => detail.data.value],
  ([open, d]) => {
    if (!open) return
    if (props.scriptId === null) {
      name.value = ''
      description.value = ''
      text.value = ''
      template.value = ''
      language.value = props.newLanguage ?? 'PowerShell'
      void loadTemplate(language.value)
    } else if (d) {
      name.value = d.name
      description.value = d.description ?? ''
      text.value = d.text
      language.value = d.language
    }
  },
  { immediate: true },
)
watch(language, (l, before) => {
  if (props.scriptId === null && l !== before) void loadTemplate(l)
})
const bash = computed(() => language.value === 'Bash')
const languageOptions = languages.map((l) => ({ label: languageLabel(l), value: l }))
const { run, busy } = useActions()
async function save() {
  const body = {
    name: name.value,
    description: description.value || null,
    text: text.value,
    language: props.scriptId ? null : language.value,
  }
  const saved = await run(
    () => (props.scriptId ? api.agents.updateScript(props.scriptId, body) : api.agents.createScript(body)),
    {
      success: bash.value ? 'Saved: a Linux agent checks it' : 'Saved: an agent parses it',
      invalidate: [keys.agents],
    },
  )
  if (saved && !props.scriptId) visible.value = false
}
const reparse = () => run(() => api.agents.parseScript(props.scriptId!), { invalidate: [keys.agents] })
const d = computed(() => detail.data.value ?? null)
const pendingText = computed(() => {
  if (!d.value) return ''
  if (bash.value)
    return d.value.parserAvailable
      ? 'A Linux agent checks it now (bash -n, shellcheck). It can run already: PNMT read its header.'
      : 'The syntax check waits for a Linux agent; it can run already: PNMT read its header.'
  return d.value.parserAvailable
    ? 'An agent parses it now.'
    : 'Parameters pending: parsed when an agent with PowerShell connects.'
})
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="scriptId ? `Script: ${d?.name ?? ''}` : 'New script'"
    modal
    :style="{ width: '70rem' }"
    :breakpoints="{ '1100px': '96vw' }"
  >
    <div class="editor">
      <form class="form" @submit.prevent="save">
        <div v-if="!scriptId" class="field">
          <label id="script-language">Language</label>
          <SelectButton
            v-model="language"
            :options="languageOptions"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            size="small"
            aria-labelledby="script-language"
          />
          <small class="field-hint"
            >PowerShell runs on Windows servers, bash on Linux servers. The language can't change later.</small
          >
        </div>
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
          <label for="script-text">{{ languageLabel(language) }} script</label>
          <Textarea id="script-text" v-model="text" rows="20" class="code" spellcheck="false" />
          <small v-if="bash" class="field-hint"
            >The header (<code># pnmt:</code>) declares the parameters; each arrives as an environment variable,
            <code>keep_days</code> as <code>$PNMT_KEEP_DAYS</code>. Secrets arrive encrypted and are never
            stored.</small
          >
        </div>
      </form>
      <aside v-if="d" class="side">
        <p class="line">
          {{ languageLabel(d.language) }} · v{{ d.currentVersion }}
          <Tag :value="d.parseState.toLowerCase()" :severity="parseSeverity(d.parseState)" />
          <span class="mono small muted">{{ d.hash.slice(0, 12) }}</span>
        </p>
        <Message v-if="d.parseState === 'Pending'" severity="info" :closable="false" size="small">
          {{ pendingText }}
        </Message>
        <Message v-if="d.parseError" severity="error" :closable="false" size="small">{{ d.parseError }}</Message>
        <template v-if="d.parsed">
          <template v-if="d.parsed.help?.synopsis || d.parsed.help?.description">
            <h4>Help</h4>
            <p v-if="d.parsed.help?.synopsis" class="small help">{{ d.parsed.help.synopsis }}</p>
            <p v-if="d.parsed.help?.description" class="small muted help">{{ d.parsed.help.description }}</p>
          </template>
          <h4>Parameters</h4>
          <ul class="plain small">
            <li v-for="p in d.parsed.parameters" :key="p.name">
              <span class="mono">{{ parameterName(p.name, d.language) }}</span> [{{ p.type }}]<template
                v-if="p.mandatory"
              >
                · mandatory</template
              ><template v-if="p.validateSet"> · {{ p.validateSet.join(', ') }}</template
              ><template v-if="p.rangeMin !== null || p.rangeMax !== null">
                · {{ p.rangeMin ?? '…' }} to {{ p.rangeMax ?? '…' }}</template
              ><template v-if="p.default"> · = {{ p.default }}</template>
              <div v-if="p.helpMessage" class="muted">{{ p.helpMessage }}</div>
            </li>
            <li v-if="!d.parsed.parameters.length" class="muted">None.</li>
          </ul>
          <h4 v-if="d.parsed.errors.length">{{ bash ? 'Errors' : 'Syntax errors' }}</h4>
          <Message v-if="bash && d.parsed.errors.length" severity="error" :closable="false" size="small">
            Errors block runs until they are fixed.
          </Message>
          <ul class="plain small">
            <li v-for="(e, i) in d.parsed.errors" :key="i" class="error">line {{ e.line }}: {{ e.message }}</li>
          </ul>
          <h4 v-if="d.parsed.findings.length">{{ bash ? 'Checks' : 'Best practice' }}</h4>
          <ul class="plain small">
            <li v-for="(f, i) in d.parsed.findings" :key="i" class="warn">
              {{ f.message }}<template v-if="f.line"> (line {{ f.line }})</template>
            </li>
          </ul>
        </template>
        <h4>Versions</h4>
        <ul class="plain small">
          <li v-for="v in d.versions" :key="v.id">
            v{{ v.number }} · <RelativeTime :value="v.createdAt" /> · {{ v.createdByName ?? '' }}
          </li>
        </ul>
      </aside>
    </div>
    <template #footer>
      <Button v-if="scriptId" label="Check again" text severity="secondary" @click="reparse" />
      <Button
        v-if="scriptId && d && runnable(d) && !d.parsed?.errors.length"
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

.help {
  margin: 0 0 var(--pnmt-space-1);
  white-space: pre-wrap;
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
