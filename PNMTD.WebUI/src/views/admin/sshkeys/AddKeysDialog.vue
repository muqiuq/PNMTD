<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { shortFingerprint } from '@/views/sensors/linux/linuxFormat'
import OwnerField from './OwnerField.vue'
import { canRegister, keyType, namesByIndex, parseLineState } from './sshKeyFormat'

/**
 * Add keys… and Register… (docs/linux-sensor-plan.md §6.4, §11.4): one or more public keys, as in id_ed25519.pub. As you type,
 * PNMTD reads them (it stores and logs nothing): a row per line with its fingerprint, type, bits, a weak note and a name, prefilled
 * from the comment; keys registered before, twice in the text, lines that aren't keys and private keys are marked and refused.
 * Then the owner (a PNMT user, or a text), the kind and a note for all of them. Register… from a server or the Unregistered list
 * opens it with the keys filled in.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ prefill?: string | null; title?: string }>()
const emit = defineEmits<{ registered: [items: S['SshKeyItem'][]] }>()

const text = ref('')
const parsed = ref<S['SshKeyParseResult'] | null>(null)
const parsing = ref(false)
const names = ref<Record<number, string>>({})
const userId = ref<string | null>(null)
const owner = ref('')
const kind = ref<S['SshKeyKind']>('Person')
const note = ref('')
const kinds = [
  { label: 'Person', value: 'Person' },
  { label: 'Service', value: 'Service' },
]

// Each change is read after a pause; an answer to an older text is dropped.
let timer: ReturnType<typeof setTimeout> | undefined
let sequence = 0
async function parse(value: string) {
  const mine = ++sequence
  if (!value.trim()) {
    parsed.value = null
    return
  }
  parsing.value = true
  try {
    const result = await api.sshKeys.parse(value)
    if (mine === sequence) parsed.value = result
  } catch {
    if (mine === sequence) parsed.value = null
  } finally {
    if (mine === sequence) parsing.value = false
  }
}
watch(text, (value) => {
  clearTimeout(timer)
  timer = setTimeout(() => void parse(value), 400)
})
watch(
  visible,
  (open) => {
    if (!open) return
    text.value = props.prefill ?? ''
    parsed.value = null
    names.value = {}
    userId.value = null
    owner.value = ''
    kind.value = 'Person'
    note.value = ''
    if (text.value) void parse(text.value)
  },
  { immediate: true },
)

const rows = computed(() =>
  (parsed.value?.lines ?? []).map((l) => ({ line: l, state: parseLineState(l) })).filter((r) => r.state !== null),
)
const hasOwner = computed(() => !!userId.value || !!owner.value.trim())
const ready = computed(() => canRegister(parsed.value) && hasOwner.value && !parsing.value)
const nameOf = (l: S['SshKeyParseLine']) => names.value[l.line] ?? l.suggestedName ?? ''
function clear() {
  text.value = ''
  parsed.value = null
}

const { run, busy } = useActions()
async function register() {
  if (!parsed.value) return
  const count = parsed.value.keys
  const items = await run(
    () =>
      api.sshKeys.register({
        publicKeys: text.value,
        name: null,
        names: namesByIndex(parsed.value!.lines, names.value),
        userId: userId.value,
        owner: userId.value ? null : owner.value.trim(),
        kind: kind.value,
        note: note.value.trim() || null,
      }),
    {
      success: count === 1 ? 'Key registered' : `${count} keys registered`,
      failure: 'Not registered',
      invalidate: [keys.sshKeys, keys.agents],
    },
  )
  if (items) {
    emit('registered', items)
    visible.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="title ?? 'Add keys'"
    modal
    :style="{ width: '58rem' }"
    :breakpoints="{ '960px': '96vw' }"
  >
    <form class="form" @submit.prevent="register">
      <div class="field">
        <label for="keys-text">Public keys, one per line</label>
        <Textarea
          id="keys-text"
          v-model="text"
          rows="5"
          class="code"
          spellcheck="false"
          placeholder="ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAA… philipp@laptop"
        />
        <small class="field-hint"
          >As in id_ed25519.pub. PNMT names and assigns keys; it never puts them on a server.</small
        >
      </div>
      <Message v-if="parsed?.privateKey" severity="error" :closable="false">
        <div class="private">
          <span
            >This is a <strong>private key</strong>. PNMT stores and logs none of it and registers nothing while it is
            in the field. Take it out; a private key that was copied anywhere should be replaced.</span
          >
          <Button label="Clear the field" size="small" severity="danger" outlined @click="clear" />
        </div>
      </Message>
      <section v-if="rows.length" class="panel">
        <DataTable :value="rows" size="small" data-key="line.line">
          <Column header="Line" style="width: 4rem">
            <template #body="{ data }"
              ><span class="muted mono small">{{ data.line.line }}</span></template
            >
          </Column>
          <Column header="Key" style="width: 13rem">
            <template #body="{ data }">
              <template v-if="data.line.fingerprint">
                {{ keyType(data.line.type ?? '', data.line.bits) }}
                <div class="mono small muted" :title="data.line.fingerprint">
                  {{ shortFingerprint(data.line.fingerprint) }}
                </div>
              </template>
            </template>
          </Column>
          <Column header="Name">
            <template #body="{ data }">
              <InputText
                v-if="data.state.ok"
                :model-value="nameOf(data.line)"
                size="small"
                maxlength="200"
                fluid
                :aria-label="`Name of the key on line ${data.line.line}`"
                @update:model-value="(v: string | undefined) => (names[data.line.line] = v ?? '')"
              />
            </template>
          </Column>
          <Column header="" style="width: 16rem">
            <template #body="{ data }">
              <div class="tags">
                <Tag v-if="!data.state.ok" :value="data.state.label" :severity="data.state.severity" />
                <Tag v-else value="new" severity="success" />
                <Tag v-if="data.line.weakness" :value="data.line.weakness" severity="danger" />
                <span v-if="data.line.servers" class="muted small"
                  >authorized on {{ data.line.servers }} {{ data.line.servers === 1 ? 'server' : 'servers' }}</span
                >
              </div>
            </template>
          </Column>
        </DataTable>
      </section>
      <p v-else-if="parsing" class="muted small">Reading…</p>
      <Message v-if="parsed && parsed.refused && !parsed.privateKey" severity="warn" :closable="false" size="small">
        {{ parsed.refused }} {{ parsed.refused === 1 ? 'line is' : 'lines are' }} refused: take
        {{ parsed.refused === 1 ? 'it' : 'them' }} out to register the others.
      </Message>
      <div class="form-grid">
        <OwnerField v-model:user-id="userId" v-model:owner="owner" />
        <div class="field">
          <label id="keys-kind">Kind</label>
          <SelectButton
            v-model="kind"
            :options="kinds"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            size="small"
            aria-labelledby="keys-kind"
          />
          <small class="field-hint">A person's key, or a service's (a backup server, a CI runner).</small>
        </div>
        <div class="field wide">
          <label for="keys-note">Note <span class="muted">(optional)</span></label>
          <InputText id="keys-note" v-model="note" maxlength="2000" />
        </div>
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        :label="parsed?.keys && parsed.keys > 1 ? `Register ${parsed.keys} keys` : 'Register'"
        icon="pi pi-key"
        :loading="busy"
        :disabled="!ready"
        @click="register"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.code {
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  width: 100%;
}

.private {
  display: flex;
  gap: var(--pnmt-space-3);
  align-items: center;
  flex-wrap: wrap;
}

.tags {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
  align-items: center;
}

.small {
  font-size: 0.857rem;
}
</style>
