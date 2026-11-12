<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import { computed, ref } from 'vue'
import { api, keys } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import ErrorState from '@/components/ErrorState.vue'
import PacketInspector from '@/components/PacketInspector.vue'
import { downloadText, fileSlug } from '@/format/markdown'
import { familyLabel, idBytes, suiteName } from '@/format/udp'
import PacketBuilder from './PacketBuilder.vue'
import ProtocolCode from './ProtocolCode.vue'
import ProtocolReference from './ProtocolReference.vue'
import { codeSuite } from './protocolCode'
import { protocolMarkdown } from './protocolMarkdown'

/**
 * The protocol documentation in the UI (docs/udp-iot-plan.md §6.5), for a node at
 * /sensors/:id/protocol and without one at /admin/udp-nodes/protocol. The worked examples, the
 * vectors and the builder come from the server's codec, so the page cannot drift from it. Every
 * role may read it; only User and Admin get the node's real key in the examples. The page downloads
 * as one Markdown file (protocolMarkdown.ts).
 */
const props = defineProps<{ sensorId?: string | null; sensorName?: string | null }>()

const examples = useQuery({
  queryKey: computed(() => [...keys.udp, 'protocol', 'examples', props.sensorId ?? 'reference']),
  queryFn: () => api.udp.examples(props.sensorId ?? undefined),
  staleTime: 60_000,
})
const data = computed(() => examples.data.value)
const vectorsOpen = ref<Set<string>>(new Set())
function toggleVector(title: string) {
  const next = new Set(vectorsOpen.value)
  if (next.has(title)) next.delete(title)
  else next.add(title)
  vectorsOpen.value = next
}

const contents = [
  { id: 'exchange', label: 'Exchange' },
  { id: 'layout', label: 'Layout' },
  { id: 'types', label: 'Types' },
  { id: 'suites', label: 'Suites' },
  { id: 'tags', label: 'Tags' },
  { id: 'counter', label: 'Counter & JOIN' },
  { id: 'commands', label: 'Commands' },
  { id: 'ota', label: 'OTA' },
  { id: 'checklist', label: 'Checklist' },
  { id: 'examples', label: 'Examples' },
  { id: 'builder', label: 'Builder' },
  { id: 'code', label: 'Code' },
]
function go(id: string) {
  document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' })
}

const reference = ref<InstanceType<typeof ProtocolReference> | null>(null)
function download() {
  if (!data.value) return
  const markdown = protocolMarkdown({
    examples: data.value,
    forNode: !!props.sensorId,
    nodeName: props.sensorName,
    reference: (reference.value?.$el as Element | undefined) ?? null,
    pageUrl: window.location.href.split('#')[0],
  })
  const name = props.sensorId ? fileSlug(props.sensorName ?? '') || `device-${data.value.deviceId}` : 'protocol'
  downloadText(`pnmt-udp-${name}.md`, markdown)
}
</script>

<template>
  <div class="protocol">
    <ErrorState
      v-if="examples.isError.value && !data"
      :error="examples.error.value"
      what="the protocol examples"
      @retry="examples.refetch()"
    />
    <div v-else-if="!data" class="center"><ProgressSpinner style="width: 40px; height: 40px" /></div>
    <template v-else>
      <section class="panel intro">
        <div class="panel-body">
          <p class="lead">
            <strong>PNMT-UDP v1</strong>: one datagram up, at most one down. A heartbeat is 5 to 13 bytes. The examples
            on this page are built by PNMTD's own codec<template v-if="sensorId">
              for this node: device id {{ data.deviceId }} (<code>{{ idBytes(data.deviceId) }}</code
              >), {{ familyLabel(data.keyFamily) }}, {{ data.allowedSuites.map(suiteName).join(', ') }}</template
            ><template v-else> with device id 7 and the reference key</template>.
          </p>
          <Message v-if="data.referenceKey" severity="info" :closable="false" class="key-note">
            <template v-if="sensorId">
              You see the reference key <code>00 01 … 0F</code> and token <code>5A 17 C3 9E</code> in the examples: only
              User and Admin see this node's key. The structure is the same; the tags differ.
            </template>
            <template v-else>
              The reference key <code>00 01 02 … 0F</code> and token <code>5A 17 C3 9E</code>, as in the specification's
              test vectors.
            </template>
          </Message>
          <dl class="facts">
            <dt>Endpoint</dt>
            <dd>
              <code>{{ data.endpoint }}</code> (UDP)
            </dd>
            <dt>Largest datagram</dt>
            <dd>{{ data.maxDatagram }} bytes</dd>
          </dl>
          <div class="toolbar">
            <nav class="contents" aria-label="On this page">
              <Button v-for="c in contents" :key="c.id" :label="c.label" size="small" text @click="go(c.id)" />
            </nav>
            <Button
              label="Download as Markdown"
              icon="pi pi-download"
              size="small"
              severity="secondary"
              outlined
              :title="
                data.keyHex
                  ? 'This page as a .md file, with the node\'s key and token in the examples'
                  : 'This page as a .md file'
              "
              @click="download"
            />
          </div>
        </div>
      </section>

      <ProtocolReference ref="reference" :examples="data" />

      <section id="examples" class="panel">
        <h2 class="panel-header">10. Worked examples{{ sensorId ? ' for this node' : '' }}</h2>
        <div class="panel-body examples">
          <article v-for="e in data.examples" :key="e.title" class="example">
            <h3>{{ e.title }}</h3>
            <p class="muted">{{ e.description }}</p>
            <PacketInspector :datagram="e.datagram" :fields="e.fields" />
            <CopyField v-if="e.shell" :value="e.shell" label="xxd | nc" class="code" />
          </article>
        </div>
      </section>

      <section v-if="data.vectors.length" id="vectors" class="panel">
        <h2 class="panel-header">Reference vectors</h2>
        <div class="panel-body">
          <p class="muted vectors-intro">
            Device id 7, key <code>00 01 … 0F</code>, token <code>5A 17 C3 9E</code>, built live; a test pins them to
            the specification. #9–#12 are #1 with another suite: the suite is in byte 1, which the MAC covers.
          </p>
          <ul class="vectors">
            <li v-for="v in data.vectors" :key="v.title">
              <button
                type="button"
                class="vector"
                :aria-expanded="vectorsOpen.has(v.title)"
                @click="toggleVector(v.title)"
              >
                <i
                  :class="vectorsOpen.has(v.title) ? 'pi pi-chevron-down' : 'pi pi-chevron-right'"
                  aria-hidden="true"
                />
                <span class="title">{{ v.title }}</span>
                <code class="hex">{{ v.hex }}</code>
              </button>
              <div v-if="vectorsOpen.has(v.title)" class="inspect">
                <PacketInspector :datagram="v.datagram" :fields="v.fields" />
              </div>
            </li>
          </ul>
        </div>
      </section>

      <section id="builder" class="panel">
        <h2 class="panel-header">11. Packet builder</h2>
        <div class="panel-body">
          <PacketBuilder :sensor-id="sensorId" :examples="data" />
        </div>
      </section>

      <section id="code" class="panel">
        <h2 class="panel-header">12. Code</h2>
        <div class="panel-body">
          <p class="muted code-intro">
            With this {{ sensorId ? "node's" : 'reference' }} values filled in. The suite is
            {{ suiteName(codeSuite(data)) }}.
          </p>
          <ProtocolCode :examples="data" />
        </div>
      </section>
    </template>
  </div>
</template>

<style scoped>
.protocol {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  max-width: 72rem;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.intro .panel-body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.lead {
  margin: 0;
}

.key-note {
  margin: 0;
}

.toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: var(--pnmt-space-2);
}

.contents {
  display: flex;
  flex-wrap: wrap;
  gap: 0;
  margin-left: calc(-1 * var(--pnmt-space-2));
}

h2.panel-header {
  font-size: 1rem;
  margin: 0;
}

/* One column: a 16-byte hex dump line needs about 38rem, more than half of the page's 72rem. */
.examples {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-4);
}

.example {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.example + .example {
  border-top: 1px solid var(--pnmt-border);
  padding-top: var(--pnmt-space-4);
}

.example h3 {
  margin: 0;
  font-size: 1rem;
}

.example p {
  margin: 0;
}

.vectors-intro,
.code-intro {
  margin: 0 0 var(--pnmt-space-2);
}

.vectors {
  list-style: none;
  margin: 0;
  padding: 0;
}

.vectors li {
  border-bottom: 1px solid var(--pnmt-border);
}

.vector {
  all: unset;
  box-sizing: border-box;
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  width: 100%;
  min-height: 34px;
  cursor: pointer;
  flex-wrap: wrap;
}

.vector:focus-visible {
  outline: 1px solid var(--p-primary-color);
}

.vector .title {
  font-weight: 500;
}

.vector .hex {
  margin-left: auto;
  color: var(--pnmt-muted);
}

.inspect {
  padding: var(--pnmt-space-2) 0 var(--pnmt-space-3);
}

.code :deep(.value) {
  white-space: pre-wrap;
}
</style>
