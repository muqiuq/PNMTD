<script setup lang="ts">
import Message from 'primevue/message'
import SelectButton from 'primevue/selectbutton'
import { computed, ref, useId } from 'vue'
import type { S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import { bytes } from './agentFormat'
import {
  architectures,
  installLine,
  joinLine,
  linuxFile,
  pnmtAvailability,
  type InstallSource,
  type InstallTool,
  type InstallUser,
} from './installLine'

/**
 * The Linux agent's one line (docs/linux-sensor-plan.md §4.4, §11.1) with its toggles: where the binary comes from (PNMT, the
 * default, or GitHub, with the same hash), x64 or arm64, wget or curl, and root or `sudo sh -c '…'`. With a token (Add server)
 * it is filled in; without (Download) the placeholder stays. When PNMT's own file isn't available, it says why and GitHub is
 * preselected; a choice made by hand stays.
 */
const props = defineProps<{ downloads: S['AgentDownloadLinks']; token?: string | null }>()
const uid = useId()

const architecture = ref('x64')
const tool = ref<InstallTool>('wget')
const user = ref<InstallUser>('root')
const chosen = ref<InstallSource | null>(null)
const availability = computed(() => pnmtAvailability(props.downloads, architecture.value))
const source = computed<InstallSource>({
  get: () => chosen.value ?? (availability.value.available ? 'PNMT' : 'GitHub'),
  set: (v) => (chosen.value = v),
})
const line = computed(() =>
  installLine(
    props.downloads,
    { source: source.value, architecture: architecture.value, tool: tool.value, user: user.value },
    props.token,
  ),
)
const lines = computed(() => props.downloads.linux.find((l) => l.architecture === architecture.value) ?? null)
const file = computed(() => linuxFile(props.downloads, architecture.value))
const join = computed(() => joinLine(props.downloads.url, props.token || props.downloads.tokenPlaceholder))
const sources = computed(() => [
  { label: 'PNMT', value: 'PNMT', disabled: !lines.value?.pnmt },
  { label: 'GitHub', value: 'GitHub', disabled: !lines.value?.gitHub },
])
const users = [
  { label: 'root', value: 'root' },
  { label: "sudo sh -c '…'", value: 'sudo' },
]
</script>

<template>
  <div class="install">
    <div class="toggles">
      <div class="toggle">
        <span :id="`${uid}-source`" class="toggle-label">From</span>
        <SelectButton
          v-model="source"
          :options="sources"
          option-label="label"
          option-value="value"
          option-disabled="disabled"
          :allow-empty="false"
          size="small"
          :aria-labelledby="`${uid}-source`"
        />
      </div>
      <div class="toggle">
        <span :id="`${uid}-arch`" class="toggle-label">Architecture</span>
        <SelectButton
          v-model="architecture"
          :options="architectures"
          :allow-empty="false"
          size="small"
          :aria-labelledby="`${uid}-arch`"
        />
      </div>
      <div class="toggle">
        <span :id="`${uid}-tool`" class="toggle-label">With</span>
        <SelectButton
          v-model="tool"
          :options="['wget', 'curl']"
          :allow-empty="false"
          size="small"
          :aria-labelledby="`${uid}-tool`"
        />
      </div>
      <div class="toggle">
        <span :id="`${uid}-user`" class="toggle-label">As</span>
        <SelectButton
          v-model="user"
          :options="users"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          :aria-labelledby="`${uid}-user`"
        />
      </div>
    </div>
    <Message
      v-if="availability.note && (line || lines?.gitHub || file?.state === 'Failed')"
      :severity="availability.available ? 'info' : 'warn'"
      :closable="false"
      size="small"
      >{{ availability.note }}</Message
    >
    <CopyField v-if="line" :value="line" label="Install line" />
    <template v-else>
      <Message severity="warn" :closable="false" size="small">
        The binary's SHA-256 isn't known yet, so there is no install line: PNMT could read it neither from GitHub nor
        from its download folder. Install the agent another way (the release's files), then join with:
      </Message>
      <CopyField :value="join" label="Join line" />
    </template>
    <div class="small muted facts-line">
      <span v-if="lines?.sha256" class="mono">SHA-256 {{ lines.sha256 }}</span>
      <span v-if="file?.size">{{ bytes(file.size) }}</span>
      <a v-if="file" :href="file.gitHubUrl" rel="noopener">{{ file.name }} on GitHub</a>
    </div>
    <small class="field-hint">
      Debian's standard utilities bring wget; minimal images (containers, some cloud images) may have neither wget nor
      curl: <code>apt-get install wget</code> first.
    </small>
  </div>
</template>

<style scoped>
.install {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.toggles {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2) var(--pnmt-space-4);
}

.toggle {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.toggle-label {
  font-size: 0.857rem;
  color: var(--pnmt-muted);
}

.facts-line {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1) var(--pnmt-space-3);
  overflow-wrap: anywhere;
}

.small {
  font-size: 0.857rem;
}
</style>
