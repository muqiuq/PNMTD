<script setup lang="ts">
import Button from 'primevue/button'
import { computed, ref } from 'vue'
import MarkdownEditor from '@/components/markdown/MarkdownEditor.vue'
import MarkdownView from '@/components/markdown/MarkdownView.vue'
import type { MarkdownSave } from '@/components/markdown/types'
import { useCopy } from '@/composables/useCopy'
import { useNotify } from '@/composables/useNotify'
import { useSession } from '@/stores/session'

/**
 * *Markdown in PNMT* (docs/locations-knowledge-plan.md §10.7): what the editors do, files and their
 * limits, diagrams and charts with an example of each common Mermaid type, the shortcuts, the search
 * syntax and *sensitive*. *Try it* is the real editor, saving nothing; files pasted into it are
 * uploaded like anywhere else and, used by no page, removed after a day (§5.5).
 */
const props = defineProps<{ search: string }>()

const session = useSession()
const copy = useCopy()
const notify = useNotify()

const has = (...texts: string[]) => {
  const q = props.search.trim().toLowerCase()
  return !q || texts.some((t) => t.toLowerCase().includes(q))
}

const diagrams = [
  {
    title: 'Flowchart',
    note: 'Network and power paths. LR, RL, TB or BT; [box], (rounded), {decision}, [(database)].',
    source: `flowchart LR
  isp[Init7 uplink] --> fw[fw-zrh-01]
  fw --> core[sw-core-01]
  core --> nas[(nas-01)]
  core --> ap{{Wi-Fi APs}}`,
  },
  {
    title: 'Sequence',
    note: 'Who talks to whom, in order.',
    source: `sequenceDiagram
  participant S as Backup script
  participant P as PNMT
  participant O as On-call
  S->>P: event 500 "disk full"
  P->>O: notification
  O-->>P: acknowledged`,
  },
  {
    title: 'State',
    note: 'States and what moves between them.',
    source: `stateDiagram-v2
  [*] --> OK
  OK --> Failing: code ≥ 400
  Failing --> OK: code < 400
  Failing --> Silent: no event
  Silent --> OK: event`,
  },
  {
    title: 'Class',
    note: 'Things and their parts.',
    source: `classDiagram
  class Host {
    +name
    +location
  }
  class Sensor {
    +type
    +interval
  }
  Host "1" --> "*" Sensor : runs`,
  },
  {
    title: 'Entity relationship',
    note: 'Tables and how they relate.',
    source: `erDiagram
  LOCATION ||--o{ HOST : contains
  HOST ||--|{ SENSOR : runs
  LOCATION ||--o{ LOCATION : "is part of"`,
  },
  {
    title: 'Gantt',
    note: 'Maintenance plans.',
    source: `gantt
  title Maintenance, November
  dateFormat YYYY-MM-DD
  section Zurich DC
  UPS batteries :done, 2026-11-02, 2d
  Firmware      :active, 2026-11-10, 3d
  section Office
  Printer swap  :2026-11-18, 1d`,
  },
  {
    title: 'Timeline',
    note: 'What happened when.',
    source: `timeline
  title Zurich DC
  2019 : opened
  2022 : second cage
  2026 : new uplink`,
  },
  {
    title: 'Pie chart',
    note: 'Shares of a whole.',
    source: `pie title Hosts by role
  "Servers" : 14
  "Switches" : 6
  "Firewalls" : 2`,
  },
  {
    title: 'Bar and line chart',
    note: 'Values over time: xychart, with bar and line.',
    source: `xychart
  title "Disk usage (GB)"
  x-axis [Jun, Jul, Aug, Sep, Oct, Nov]
  y-axis "GB" 0 --> 1000
  bar [420, 480, 530, 610, 700, 760]
  line [420, 480, 530, 610, 700, 760]`,
  },
  {
    title: 'Mind map',
    note: 'A topic and its branches, by indentation.',
    source: `mindmap
  root((Zurich DC))
    Cage B
      fw-zrh-01
      sw-core-01
    Office
      Printers
      Wi-Fi`,
  },
  {
    title: 'Git graph',
    note: 'Branches and merges, for a configuration repository.',
    source: `gitGraph
  commit id: "1.0"
  branch uplink
  commit id: "new uplink"
  checkout main
  merge uplink
  commit id: "1.1"`,
  },
  {
    title: 'Quadrant',
    note: 'Two axes, four fields: risks, priorities.',
    source: `quadrantChart
  title Risks
  x-axis Unlikely --> Likely
  y-axis Small --> Large
  quadrant-1 Act now
  quadrant-2 Plan for it
  quadrant-3 Accept
  quadrant-4 Watch
  UPS failure: [0.3, 0.8]
  Uplink outage: [0.6, 0.7]
  Printer jam: [0.9, 0.1]`,
  },
  {
    title: 'Sankey',
    note: 'Flows and their sizes: source, target, value per line.',
    source: `sankey
Uplink,Firewall,100
Firewall,Servers,60
Firewall,Office,40`,
  },
  {
    title: 'Block',
    note: 'Boxes in columns, for racks and layouts.',
    source: `block
  columns 3
  isp["Uplink"] fw["Firewall"] core["Core switch"]
  isp --> fw
  fw --> core`,
  },
  {
    title: 'Architecture',
    note: 'Services in groups, with Mermaid’s own icons (none are fetched).',
    source: `architecture-beta
  group dc(cloud)[Zurich DC]
  service fw(server)[Firewall] in dc
  service db(database)[Database] in dc
  service disk(disk)[Storage] in dc
  fw:R --> L:db
  db:B --> T:disk`,
  },
  {
    title: 'Packet',
    note: 'The fields of a header, by bit.',
    source: `packet
  0-15: "Source port"
  16-31: "Destination port"
  32-63: "Sequence number"`,
  },
]
const fence = (source: string) => `\`\`\`mermaid\n${source}\n\`\`\``
const visibleDiagrams = computed(() => diagrams.filter((d) => has(d.title, d.note, d.source, 'diagram chart mermaid')))

const shortcuts = [
  ['Ctrl/⌘ S', 'Save (on the editing page)'],
  ['Ctrl/⌘ B, I', 'Bold, italic (visual editor)'],
  ['Ctrl/⌘ K', 'A link (visual editor); elsewhere the global search'],
  ['/', 'Blocks: headings, lists, a table, an image, a code block (visual editor, at a line’s start)'],
  ['F1', 'The command palette (Markdown editor)'],
  ['Ctrl/⌘ F, Ctrl/⌘ H', 'Find, and replace (Markdown editor)'],
  ['Alt + click, Ctrl/⌘ D', 'More cursors, the next match too (Markdown editor)'],
  ['Ctrl/⌘ Shift [ and ]', 'Fold and unfold a section (Markdown editor)'],
]

const example = `# Try it

Write here in the **visual** editor, or switch to *Markdown* above. Nothing here is saved.

- [x] a task list
- [ ] paste a screenshot

| Host | Role |
|------|------|
| fw-zrh-01 | Firewall |

\`\`\`mermaid
flowchart LR
  uplink --> fw[fw-zrh-01] --> core[sw-core-01]
\`\`\`
`
const tryText = ref(example)
const canUpload = computed(() => session.can('User'))
const tryVisible = computed(() =>
  has('try it editor visual markdown paste upload image attach preview switch textarea plain text'),
)
const showEditors = computed(() =>
  has('editor visual markdown crepe monaco switch textarea plain text fallback preview rewrite'),
)
const showFiles = computed(() =>
  has('files images attachments paste drop upload limits 2 MB 25 MB recompress WebP JPEG executables'),
)
const showSearch = computed(() => has('search syntax prefix phrase exclude or #tag tags global'))
const showSensitive = computed(() => has('sensitive AI MCP lock'))
const showShortcuts = computed(() => shortcuts.some(([k, v]) => has(k!, v!)) || has('keyboard shortcuts'))
const any = computed(
  () =>
    tryVisible.value ||
    showEditors.value ||
    showFiles.value ||
    visibleDiagrams.value.length > 0 ||
    showSearch.value ||
    showSensitive.value ||
    showShortcuts.value,
)

function onSave(_save: MarkdownSave) {
  notify.info('Nothing is saved here', 'This editor is an example; on a page, Save keeps a revision.')
}
</script>

<template>
  <section v-if="any" class="panel" aria-labelledby="markdown-title">
    <div id="markdown-title" class="panel-header">Markdown in PNMT</div>
    <div class="panel-body markdown-docs">
      <p v-if="showEditors" class="lead">
        Knowledge base pages and location fields are written in <strong>Markdown</strong> (GitHub’s flavour: tables,
        task lists, strikethrough, autolinks, footnotes). What is stored is exactly what the editor wrote, readable
        without PNMT and downloadable as a <code>.md</code> file.
      </p>

      <article v-if="tryVisible" class="try">
        <h3>Try it</h3>
        <p class="muted">
          The real editor, with the switch, Preview and uploads<template v-if="canUpload">
            (pasted files are uploaded, and removed after a day, since no page uses them)</template
          ><template v-else> (uploads need the User role)</template>. Nothing typed here is saved.
        </p>
        <MarkdownEditor
          v-model="tryText"
          mode="page"
          layout="inline"
          height="26rem"
          :uploads="canUpload"
          placeholder="Write, or type / for blocks…"
          @save="onSave"
        >
          <template #bar>
            <Button label="Start over" size="small" text severity="secondary" @click="tryText = example" />
          </template>
        </MarkdownEditor>
      </article>

      <div class="columns">
        <article v-if="showEditors">
          <h3>Two editors, one text</h3>
          <ul>
            <li>
              <strong>Visual</strong> shows the page as it reads: a toolbar on top and on a selection, <kbd>/</kbd> for
              blocks, tables with row and column tools, task lists, images, code blocks, and a
              <code>mermaid</code> block’s diagram under its code.
            </li>
            <li>
              <strong>Markdown</strong> is VS Code’s editor: line numbers, highlighting, folding by headings, find and
              replace, several cursors, the command palette (<kbd>F1</kbd>). <em>Preview</em> shows the page beside it.
            </li>
            <li>
              <strong>The switch</strong> remembers the last mode, for pages and for fields apart. Before the visual
              editor opens a text written by hand, PNMT checks how it would write it, and if lines would change (a
              <code>*</code> list becomes <code>-</code>, a table is aligned) it says how many and asks.
            </li>
            <li>
              <strong>Where they can’t run</strong> (Markdown on a phone, an old browser, a blocked file), a plain text
              field takes over, with pasting files, Preview and Ctrl/⌘ S.
            </li>
            <li>
              <strong>Drafts</strong> stay in this browser while you edit: after a crash or a closed tab, the editing
              page offers to restore them. A save made by someone else in between is shown, never overwritten silently.
            </li>
          </ul>
        </article>

        <article v-if="showFiles">
          <h3>Images and files</h3>
          <ul>
            <li>
              <strong>Paste or drop</strong> a screenshot, a photo or any file; or use <em>Image</em> and
              <em>Attach file</em>. It is uploaded at once and put where the cursor is.
            </li>
            <li>
              <strong>Images</strong> (PNG, JPEG, GIF, WebP) are shown in the page, at most <strong>2 MB</strong>. A
              larger one is made smaller in the browser (WebP or JPEG, then at most 3,840 pixels wide), and you’re told;
              <em>Attach the original instead</em> keeps a floor plan sharp. Location data in photos is removed.
            </li>
            <li>
              <strong>Any other file</strong> is an attachment, at most <strong>25 MB</strong>, always downloaded, never
              opened in the browser. Programs and scripts (<code>.exe</code>, <code>.bat</code>, <code>.ps1</code>,
              <code>.js</code>, …) are refused.
            </li>
            <li>
              <strong>Images from other sites</strong> are never loaded: a page shows them as a link,
              <em>external image, not loaded</em>. Raw HTML is shown as text.
            </li>
          </ul>
        </article>

        <article v-if="showShortcuts">
          <h3>Keyboard</h3>
          <dl class="facts">
            <template v-for="[keys, what] in shortcuts" :key="keys">
              <dt>
                <kbd>{{ keys }}</kbd>
              </dt>
              <dd>{{ what }}</dd>
            </template>
          </dl>
        </article>

        <article v-if="showSearch">
          <h3>Searching pages</h3>
          <ul>
            <li>
              The global search (<kbd>Ctrl/⌘ K</kbd>) finds pages by number (<code>KB-42</code>), title, summary and
              tag; <code>#backup</code> finds the pages tagged <em>backup</em>.
            </li>
            <li>
              The knowledge base searches the text: every word matches as a start (<code>drucker</code> finds
              <em>Druckerwarteschlange</em>), <code>"two words"</code> as a phrase, <code>-word</code> excludes,
              <code>or</code> gives alternatives.
            </li>
            <li>Accents count: <em>Zurich</em> doesn’t find <em>Zürich</em>.</li>
          </ul>
        </article>

        <article v-if="showSensitive">
          <h3>Sensitive</h3>
          <p>
            A page marked <i class="pi pi-lock" aria-label="sensitive" /> <strong>sensitive</strong> is never sent to an
            AI and never shown to MCP; people (Viewers included) see it as usual. Tags from AI need two statements
            first: no credentials, and no personal or confidential data. PNMT is not a password manager: when a text
            looks like it holds a password, saving warns.
          </p>
        </article>
      </div>

      <template v-if="visibleDiagrams.length">
        <h3 class="diagrams-title">Diagrams and charts</h3>
        <p class="muted">
          A code block named <code>mermaid</code> is drawn as a diagram, as on GitHub and in VS Code; any other viewer
          shows its source. Diagrams are drawn in the browser, nothing is fetched, and a syntax error shows the message
          with the source. Copy an example into a page:
        </p>
        <div class="diagrams">
          <article v-for="d in visibleDiagrams" :key="d.title" class="diagram">
            <header>
              <h4>{{ d.title }}</h4>
              <Button
                v-tooltip.left="'Copy'"
                icon="pi pi-copy"
                size="small"
                text
                severity="secondary"
                :aria-label="`Copy the ${d.title} example`"
                @click="copy(fence(d.source), `The ${d.title} example`)"
              />
            </header>
            <p class="muted">{{ d.note }}</p>
            <MarkdownView :source="fence(d.source)" />
          </article>
        </div>
      </template>
    </div>
  </section>
</template>

<style scoped>
.markdown-docs {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-4);
}

.lead {
  margin: 0;
  max-width: 60rem;
}

.try p {
  margin: 0 0 var(--pnmt-space-2);
}

h3 {
  margin-bottom: var(--pnmt-space-2);
}

.columns {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(26rem, 1fr));
  gap: var(--pnmt-space-4);
}

.columns ul {
  margin: 0;
  padding-left: 1.2em;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.columns p {
  margin: 0;
}

kbd {
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  padding: 0 0.35em;
  border: 1px solid var(--pnmt-border);
  border-bottom-width: 2px;
  border-radius: var(--p-border-radius-sm);
  background: var(--pnmt-bg);
  white-space: nowrap;
}

.diagrams-title {
  margin: 0;
}

.diagrams {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(24rem, 1fr));
  gap: var(--pnmt-space-3);
}

.diagram {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
  min-width: 0;
}

.diagram header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.diagram h4 {
  margin: 0;
  font-size: 1rem;
  font-weight: 600;
}

.diagram p {
  margin: 0;
}

@media (max-width: 599px) {
  .columns,
  .diagrams {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
