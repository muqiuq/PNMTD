<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed } from 'vue'
import { randomBytes, toHex } from '@/format/bytes'
import {
  ACK_MODES,
  FAMILIES,
  SESSION_MODES,
  idBytes,
  idLength,
  suiteChoices,
  suiteInfo,
  suiteMarks,
  suiteName,
  type KeyFamily,
  type SecuritySuite,
} from '@/format/udp'
import { changeFamily, draftHints, KEY_BYTES, secretError, TOKEN_BYTES, type UdpDraft } from './udpDraft'

/**
 * The UDP section of the sensor form (docs/udp-iot-plan.md §6.1), and of the dialog that edits an
 * existing node: device id, key family, the suites a node may use and the one it answers in,
 * session, ACK and replay protection, and the key and token (validated as they are typed). A new
 * node's key and token are generated when saved unless entered by hand; an existing node keeps
 * them unless new ones are entered.
 */
const draft = defineModel<UdpDraft>({ required: true })
const props = withDefaults(
  defineProps<{
    errors: Record<string, string | undefined>
    listenerOff?: string | null
    /** Edit: the node exists, its id and secrets stay unless changed here. */
    mode?: 'create' | 'edit'
  }>(),
  { listenerOff: null, mode: 'create' },
)
const editing = computed(() => props.mode === 'edit')

const set = (patch: Partial<UdpDraft>) => (draft.value = { ...draft.value, ...patch })

const choices = computed(() => suiteChoices(draft.value.keyFamily))
const familyOptions = FAMILIES.map((f) => ({ ...f, label: f.legacy ? `${f.label} (legacy)` : f.label }))
const responseOptions = computed(() => [
  { label: 'As received', value: null as SecuritySuite | null },
  ...choices.value.map((s) => ({ label: suiteName(s), value: s as SecuritySuite | null })),
])
const header = computed(() => {
  const id = draft.value.deviceId
  if (!id || id < 1)
    return editing.value
      ? 'Empty keeps the current id.'
      : 'A free id is picked at random when saved: one header byte below 256, two below 65,536.'
  const length = idLength(id)
  return `IDL ${length} byte${length === 1 ? '' : 's'} · ${idBytes(id)} in the header.`
})
const hints = computed(() => draftHints(draft.value))
const keyError = computed(() => secretError(draft.value.key, KEY_BYTES))
const tokenError = computed(() => secretError(draft.value.token, TOKEN_BYTES))

function onFamily(f: KeyFamily) {
  draft.value = changeFamily(draft.value, f)
}

function toggleSuite(s: SecuritySuite, on: boolean) {
  const next = on ? [...draft.value.allowedSuites, s] : draft.value.allowedSuites.filter((x) => x !== s)
  // In code order, as the server lists them.
  set({ allowedSuites: choices.value.filter((c) => next.includes(c)) })
}
</script>

<template>
  <fieldset class="udp-fields wide">
    <legend>UDP</legend>
    <Message v-if="listenerOff" severity="warn" :closable="false" class="wide">{{ listenerOff }}</Message>
    <div class="grid">
      <div class="field">
        <label for="udp-device-id">Device id <span v-if="!editing" class="muted">(optional)</span></label>
        <InputNumber
          :model-value="draft.deviceId"
          input-id="udp-device-id"
          :min="1"
          :max="4294967295"
          :use-grouping="false"
          :placeholder="editing ? 'unchanged' : 'random when saved'"
          :invalid="!!errors['udp.deviceId']"
          @update:model-value="(v: number | null) => set({ deviceId: v })"
        />
        <small v-if="errors['udp.deviceId']" class="field-error">{{ errors['udp.deviceId'] }}</small>
        <small v-else class="field-hint">{{ header }}</small>
      </div>
      <div class="field">
        <label for="udp-family">Key family</label>
        <Select
          :model-value="draft.keyFamily"
          input-id="udp-family"
          :options="familyOptions"
          option-label="label"
          option-value="value"
          :invalid="!!errors['udp.keyFamily']"
          @update:model-value="(f: KeyFamily) => onFamily(f)"
        >
          <template #option="{ option }">
            <span class="option">
              <span>{{ option.label }}</span>
              <small class="muted">{{ option.hint }}</small>
            </span>
          </template>
        </Select>
        <small v-if="errors['udp.keyFamily']" class="field-error">{{ errors['udp.keyFamily'] }}</small>
        <small v-else class="field-hint">One 16-byte key, used with this family only. Encryption needs AES.</small>
      </div>

      <div class="field wide">
        <span id="udp-suites-label" class="label">Allowed suites</span>
        <div class="suites" role="group" aria-labelledby="udp-suites-label">
          <label v-for="s in choices" :key="s" class="suite">
            <Checkbox
              :model-value="draft.allowedSuites.includes(s)"
              binary
              @update:model-value="(on: boolean) => toggleSuite(s, on)"
            />
            <span class="mono">{{ suiteName(s) }}</span>
            <small class="muted">{{ suiteInfo(s)?.tag ? `${suiteInfo(s)?.tag}-byte tag` : 'no tag' }}</small>
            <span v-for="m in suiteMarks(s)" :key="m" class="mark" :class="m">{{ m }}</span>
          </label>
        </div>
        <small v-if="errors['udp.allowedSuites']" class="field-error">{{ errors['udp.allowedSuites'] }}</small>
        <small v-else class="field-hint"
          >Packets in any other suite are rejected. The device chooses per packet: cheap heartbeats and encrypted data
          can come from the same node.</small
        >
      </div>

      <div class="field">
        <label for="udp-response">Response suite</label>
        <Select
          :model-value="draft.responseSuite"
          input-id="udp-response"
          :options="responseOptions"
          option-label="label"
          option-value="value"
          :placeholder="responseOptions[0]?.label"
          :invalid="!!errors['udp.responseSuite']"
          @update:model-value="(s: SecuritySuite | null) => set({ responseSuite: s })"
        />
        <small v-if="errors['udp.responseSuite']" class="field-error">{{ errors['udp.responseSuite'] }}</small>
        <small v-else class="field-hint">ACKs and commands go out in the uplink's suite unless one is set here.</small>
      </div>
      <div class="field">
        <label for="udp-session">Session mode</label>
        <Select
          :model-value="draft.sessionMode"
          input-id="udp-session"
          :options="SESSION_MODES"
          option-label="label"
          option-value="value"
          :invalid="!!errors['udp.sessionMode']"
          @update:model-value="(m: UdpDraft['sessionMode']) => set({ sessionMode: m })"
        />
        <small v-if="errors['udp.sessionMode']" class="field-error">{{ errors['udp.sessionMode'] }}</small>
        <small v-else class="field-hint">JOIN, for devices that cannot keep their counter across restarts.</small>
      </div>
      <div class="field">
        <span id="udp-ack-label" class="label">Acknowledgements</span>
        <SelectButton
          :model-value="draft.ackMode"
          :options="ACK_MODES"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          aria-labelledby="udp-ack-label"
          @update:model-value="(m: UdpDraft['ackMode']) => set({ ackMode: m })"
        />
        <small class="field-hint">On request: an ACK when the packet sets A. A command replaces the ACK.</small>
      </div>
      <div class="field">
        <span class="label">Replay protection</span>
        <div class="switch-row">
          <ToggleSwitch
            :model-value="draft.replayProtection"
            input-id="udp-replay"
            @update:model-value="(on: boolean) => set({ replayProtection: on })"
          />
          <label for="udp-replay">{{ draft.replayProtection ? 'On' : 'Off' }}</label>
        </div>
        <small class="field-hint">The counter must increase for keyed suites. Off only for bench testing.</small>
      </div>

      <div class="field wide secrets">
        <span class="label">Key and token</span>
        <div class="switch-row">
          <ToggleSwitch
            :model-value="draft.manual"
            input-id="udp-manual"
            @update:model-value="(on: boolean) => set({ manual: on })"
          />
          <label for="udp-manual">{{ editing ? 'Enter a new key or token' : 'Enter manually' }}</label>
        </div>
        <small v-if="!draft.manual && editing" class="field-hint"
          >The key and token stay as they are (a new key family gets a new key).</small
        >
        <small v-else-if="!draft.manual" class="field-hint"
          >Generated automatically when saved: a 16-byte key and a 4-byte token, shown on the node's page.</small
        >
        <template v-else>
          <div class="field">
            <label for="udp-key">Key <span class="muted">(32 hex digits)</span></label>
            <div class="with-button">
              <InputText
                id="udp-key"
                :model-value="draft.key"
                class="mono grow"
                autocomplete="off"
                spellcheck="false"
                :placeholder="editing ? '00 01 02 … 0F, or empty to keep it' : '00 01 02 … 0F, or empty to generate'"
                :invalid="!!(keyError || errors['udp.key'])"
                @update:model-value="(v: string | undefined) => set({ key: v ?? '' })"
              />
              <Button
                label="Generate"
                icon="pi pi-refresh"
                size="small"
                severity="secondary"
                outlined
                @click="set({ key: toHex(randomBytes(KEY_BYTES)) })"
              />
            </div>
            <small v-if="keyError || errors['udp.key']" class="field-error">{{ keyError ?? errors['udp.key'] }}</small>
            <small v-else class="field-hint"
              >Spaces, colons and 0x are fine. Generate fills in a random key to put into the firmware first.</small
            >
          </div>
          <div class="field">
            <label for="udp-token">Token <span class="muted">(8 hex digits)</span></label>
            <div class="with-button">
              <InputText
                id="udp-token"
                :model-value="draft.token"
                class="mono grow"
                autocomplete="off"
                spellcheck="false"
                :placeholder="editing ? '5A 17 C3 9E, or empty to keep it' : '5A 17 C3 9E, or empty to generate'"
                :invalid="!!(tokenError || errors['udp.token'])"
                @update:model-value="(v: string | undefined) => set({ token: v ?? '' })"
              />
              <Button
                label="Generate"
                icon="pi pi-refresh"
                size="small"
                severity="secondary"
                outlined
                @click="set({ token: toHex(randomBytes(TOKEN_BYTES)) })"
              />
            </div>
            <small v-if="tokenError || errors['udp.token']" class="field-error">{{
              tokenError ?? errors['udp.token']
            }}</small>
            <small v-else class="field-hint">For TOKEN32, independent of the key.</small>
          </div>
        </template>
      </div>
    </div>
    <Message v-for="h in hints" :key="h" severity="warn" :closable="false" class="hint">{{ h }}</Message>
  </fieldset>
</template>

<style scoped>
.udp-fields {
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  padding: var(--pnmt-space-3);
  margin: 0;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

legend {
  font-weight: 600;
  padding: 0 var(--pnmt-space-1);
}

.grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--pnmt-space-3) var(--pnmt-space-4);
}

.grid .wide {
  grid-column: 1 / -1;
}

.label {
  font-weight: 500;
}

.suites {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(15rem, 1fr));
  gap: var(--pnmt-space-1) var(--pnmt-space-3);
}

.suite {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-height: 28px;
}

.mark {
  font-size: 0.75rem;
  font-weight: 600;
  padding: 0 0.35rem;
  border-radius: var(--p-border-radius-sm);
  background: var(--pnmt-none-soft);
  color: var(--pnmt-muted);
}

.mark.weak {
  background: var(--pnmt-warn-soft);
  color: var(--pnmt-warn);
}

.mark.encrypted {
  background: var(--pnmt-ok-soft);
  color: var(--pnmt-ok);
}

.option {
  display: flex;
  flex-direction: column;
}

.switch-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-height: 32px;
}

.secrets {
  gap: var(--pnmt-space-2);
}

.with-button {
  display: flex;
  gap: var(--pnmt-space-2);
}

.grow {
  flex: 1;
  min-width: 0;
}

.mono {
  font-family: var(--pnmt-mono);
}

.hint {
  margin: 0;
}

@media (max-width: 599px) {
  .grid {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
